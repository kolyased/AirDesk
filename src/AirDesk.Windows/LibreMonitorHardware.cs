using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;

namespace AirDesk;

// All methods run on the same hardware executor as firmware writes.
internal sealed class LibreMonitorHardware : IMonitorHardware
{
    sealed record Frame(Reading[] Readings, TimeSpan Next, bool Failed);
    readonly WindowsCooling cooling = new();
    readonly Computer computer = new() { IsCpuEnabled = true, IsMotherboardEnabled = true, IsMemoryEnabled = true,
        IsGpuEnabled = true, IsStorageEnabled = true };
    readonly Dictionary<IHardware, Frame> frames = new();
    readonly Dictionary<IHardware, string?> gpuInstances = new();
    IHardware[] devices = [];
    TimeSpan nextEnumeration;
    bool opened;

    public void Open()
    {
        opened = true;
        computer.Open();
        Enumerate(); nextEnumeration = TimeSpan.FromSeconds(10);
        cooling.Discover(devices);
    }

    public SensorPoll Poll(TimeSpan elapsed, bool discover)
    {
        if (elapsed >= nextEnumeration || discover)
        {
            Enumerate(); nextEnumeration = elapsed + TimeSpan.FromSeconds(10);
            if (discover) cooling.Discover(devices);
        }
        int updated = 0, deferred = 0, failed = 0;
        foreach (var device in devices)
        {
            frames.TryGetValue(device, out var previous);
            if (previous != null && elapsed < previous.Next) { deferred++; if (previous.Failed) failed++; continue; }
            ReadingQuality quality;
            try
            {
                cooling.Update(device);
                quality = device.HardwareType == HardwareType.SuperIO && cooling.BoardUpdateBlocked ? ReadingQuality.Stale : ReadingQuality.Fresh;
            }
            catch (Exception error)
            {
                if (previous?.Failed != true) Monitor.Log(error);
                quality = error is UnauthorizedAccessException ? ReadingQuality.AccessDenied : ReadingQuality.Unresponsive;
            }
            DateTimeOffset measured = DateTimeOffset.UtcNow;
            var prior = previous?.Readings ?? [];
            Reading[] readings;
            try
            {
                string? instanceId = gpuInstances.GetValueOrDefault(device);
                readings = Reading.MergePoll(device.Sensors.Select(sensor => Normalize(device, sensor, measured, quality, instanceId)).ToArray(),
                    prior, new HashSet<(string, string)> { (device.Identifier.ToString(), device.Name) }, measured,
                    Telemetry.MaxAge(device.HardwareType.ToString()));
                if (quality != ReadingQuality.Fresh)
                    readings = readings.Select(item => item with { Quality = quality }).ToArray();
            }
            catch (Exception error)
            {
                if (previous?.Failed != true) Monitor.Log(error);
                quality = ReadingQuality.Unresponsive;
                readings = prior.Select(item => item with { Quality = quality }).ToArray();
            }
            if (quality != ReadingQuality.Fresh) failed++;
            frames[device] = new(readings, elapsed + Interval(device.HardwareType), quality != ReadingQuality.Fresh);
            updated++;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var values = devices.SelectMany(device => frames[device].Readings).Select(reading =>
            reading.Quality is ReadingQuality.Fresh or ReadingQuality.Unverified && reading.UpdatedAt is { } at &&
            now - at >= Telemetry.MaxAge(reading.DeviceType) ? reading with { Quality = ReadingQuality.Stale } : reading).ToArray();
        return new(new Telemetry(values), updated, deferred, failed);
    }

    static Reading Normalize(IHardware device, ISensor sensor, DateTimeOffset now, ReadingQuality quality, string? instanceId)
    {
        float? value = sensor.Value is float number && float.IsFinite(number) ? number : null;
        if (device.HardwareType == HardwareType.Storage && sensor.SensorType == SensorType.Temperature && value == 0)
            value = null; // LHM 0.9.6 uses zero for absent SMART temperatures.
        if (quality == ReadingQuality.Fresh && !value.HasValue) quality = ReadingQuality.NoSensor;
        if (quality == ReadingQuality.Fresh && device.HardwareType == HardwareType.SuperIO &&
            sensor.SensorType is SensorType.Temperature or SensorType.Voltage &&
            (sensor.Name.StartsWith("Temperature #", StringComparison.Ordinal) || sensor.Name.StartsWith("Voltage #", StringComparison.Ordinal)))
            quality = ReadingQuality.Unverified;
        string unit = sensor.SensorType switch
        {
            SensorType.Temperature => "°C", SensorType.Control or SensorType.Load or SensorType.Level => "%",
            SensorType.Clock => "МГц", SensorType.Frequency => "Гц", SensorType.Voltage => "В", SensorType.Current => "А",
            SensorType.Power => "Вт", SensorType.Fan => "об/мин", SensorType.Data => "ГБ", SensorType.SmallData => "МБ",
            SensorType.Throughput => "Б/с", _ => ""
        };
        if (sensor.SensorType == SensorType.Factor && sensor.Name == "Power On Hours") unit = "ч";
        return new Reading(device.Name, device.Identifier.ToString(), device.HardwareType.ToString(), sensor.Name,
            sensor.SensorType.ToString(), unit, quality is ReadingQuality.Fresh or ReadingQuality.Unverified ? value : null,
            sensor.Identifier.ToString(), quality, quality is ReadingQuality.Fresh or ReadingQuality.Unverified ? now : null)
            { DeviceInstance = instanceId };
    }

    static TimeSpan Interval(HardwareType type) => TimeSpan.FromMilliseconds(type switch
    {
        HardwareType.Storage => 5000, HardwareType.Memory => 2200, HardwareType.Motherboard => 10000, _ => 750
    });

    void Enumerate()
    {
        devices = computer.Hardware.SelectMany(Walk).ToArray();
        foreach (var removed in frames.Keys.Except(devices).ToArray()) frames.Remove(removed);
        foreach (var removed in gpuInstances.Keys.Except(devices).ToArray()) gpuInstances.Remove(removed);
        foreach (var gpu in devices.OfType<GenericGpu>().Where(gpu => !gpuInstances.ContainsKey(gpu)))
        {
            try
            {
                string? id = gpu.DeviceId?.Trim().ToUpperInvariant();
                gpuInstances.Add(gpu, id is { Length: <= 240 } && id.StartsWith(@"PCI\VEN_", StringComparison.Ordinal) &&
                    id.Split('\\') is [_, { Length: > 0 }, { Length: > 0 }] ? id : null);
            }
            catch (Exception error)
            {
                gpuInstances.Add(gpu, null);
                Monitor.Log(error);
            }
        }
    }

    static IEnumerable<IHardware> Walk(IHardware hardware)
    {
        yield return hardware;
        foreach (var child in hardware.SubHardware)
            foreach (var item in Walk(child)) yield return item;
    }

    public bool RetryDiscovery => cooling.RetryDiscovery;
    public FanSample Sample(Telemetry telemetry) => cooling.Sample(telemetry);
    public void Acquire() => cooling.Acquire();
    public void AcquireRecoveryLease() => cooling.AcquireRecoveryLease();
    public float? Write(float percent, Action<float?> guard) => cooling.Write(percent, guard);
    public RestoreResult Restore() => cooling.Restore();
    public void ReleaseLease() => cooling.ReleaseLease();

    public void Dispose()
    {
        try
        {
            if (opened) { opened = false; computer.Close(); }
        }
        finally { cooling.Dispose(); }
    }
}
