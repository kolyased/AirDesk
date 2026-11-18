using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

public enum ReadingQuality { Fresh, NoSensor, AccessDenied, Unsupported, Unresponsive, Stale, Unverified }

public record Reading(string Device, string DeviceId, string DeviceType, string Name, string Kind, string Unit,
    float? Value, string Id, ReadingQuality Quality, DateTimeOffset? UpdatedAt)
{
    public string Source => "LibreHardwareMonitor";
    public bool Available => Quality == ReadingQuality.Fresh && Value.HasValue;
    public SensorReference Reference => new(Id, DeviceId, Device);

    public static Reading[] MergePoll(Reading[] current, Reading[] previous, IReadOnlySet<(string Id, string Name)> devices,
        DateTimeOffset completedAt, TimeSpan freshness)
    {
        var last = previous.GroupBy(r => r.Reference).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var readings = new List<Reading>(current.Length);
        foreach (var item in current)
        {
            last.Remove(item.Reference, out var prior);
            var reading = item.UpdatedAt == null && prior?.UpdatedAt != null
                ? item with { UpdatedAt = prior.UpdatedAt, Value = prior.Value }
                : item;
            if (reading.Quality is ReadingQuality.Fresh or ReadingQuality.Unverified && reading.UpdatedAt is { } sampleTime &&
                completedAt - sampleTime >= freshness) reading = reading with { Quality = ReadingQuality.Stale };
            readings.Add(reading);
        }
        foreach (var remaining in last.Values)
            if (devices.Contains((remaining.DeviceId, remaining.Device))) readings.Add(remaining with { Quality = ReadingQuality.NoSensor });
        return readings.ToArray();
    }


}

public record Metric(string Label, Reading? Reading, float? Derived = null, string Unit = "", string Section = "",
    ReadingQuality Missing = ReadingQuality.Unsupported);

public sealed class DeviceReadings
{
    readonly Dictionary<string, Reading> byKey;
    readonly Dictionary<(string Kind, string Name), Reading> byName;
    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public Reading[] Readings { get; }
    public bool Gpu => Type is "GpuNvidia" or "GpuAmd" or "GpuIntel";
    public bool Disk => Type == "Storage";

    public DeviceReadings(IGrouping<string, Reading> readings)
    {
        Id = readings.Key;
        Readings = readings.OrderBy(reading => reading.Id, StringComparer.Ordinal).ToArray();
        Name = Readings[0].Device;
        Type = Readings[0].DeviceType;
        byKey = Readings.Where(reading => reading.Id.StartsWith(Id + "/", StringComparison.Ordinal))
            .GroupBy(reading => reading.Id[Id.Length..]).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        byName = Readings.GroupBy(reading => (reading.Kind, reading.Name)).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
    }

    Reading? Get(string key) => byKey.GetValueOrDefault(key);
    Reading? Named(string kind, params string[] labels) => labels.Select(label => byName.GetValueOrDefault((kind, label)))
        .FirstOrDefault(reading => reading != null);
    float? AvailableValue(Reading? reading) => reading?.Available == true ? reading.Value : null;
    public Reading? Temperature => Type switch
    {
        "Cpu" => Named("Temperature", "CPU Package", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Core Max"),
        "GpuNvidia" => Get("/temperature/0"),
        "GpuAmd" or "GpuIntel" => Named("Temperature", "GPU Core", "GPU Temperature", "GPU Edge"),
        "Storage" => Named("Temperature", "Composite Temperature", "Temperature"),
        _ => null
    };
    public Reading? Load => Type switch
    {
        "Cpu" => Named("Load", "CPU Total"),
        "GpuNvidia" => Get("/load/0"),
        "GpuAmd" or "GpuIntel" => Named("Load", "GPU Core", "GPU Total", "GPU D3D 3D"),
        "Storage" => Named("Load", "Total Activity"),
        "Memory" when Id == "/ram" => Get("/load/0"),
        _ => null
    };
    public Reading? Hotspot => Gpu ? Named("Temperature", "GPU Hot Spot", "GPU Hotspot", "GPU Junction") : null;
    public Reading? MemoryUsed => Gpu ? Named("SmallData", "GPU Memory Used", "D3D Dedicated Memory Used") : Get("/data/0");
    public Reading? VramTotal => Gpu ? Named("SmallData", "GPU Memory Total") : null;
    public float? MemoryTotal => Gpu ? AvailableValue(VramTotal) : AvailableValue(Get("/data/0")) + AvailableValue(Get("/data/1"));

    public Metric[] Details()
    {
        var items = new List<Metric>();
        if (Temperature != null || Gpu || Disk || Type == "Cpu") items.Add(new("Температура", Temperature, Missing: ReadingQuality.NoSensor));
        if (Load != null || Gpu || Disk || Type is "Cpu" or "Memory") items.Add(new("Нагрузка / активность", Load));
        if (Gpu)
        {
            items.Add(new("Горячая точка", Hotspot, Missing: ReadingQuality.NoSensor));
            items.Add(new("VRAM занято", MemoryUsed));
            items.Add(new("VRAM всего", VramTotal));
        }
        if (Id == "/ram")
        {
            items.Add(new("Используется", MemoryUsed));
            items.Add(new("Всего", null, MemoryTotal, "ГБ"));
        }
        if (Type is "Cpu" or "Memory" || Gpu)
        {
            items.AddRange(Readings.Where(r => r.Kind == "Clock" || r.Kind == "Power" || Type == "Memory" && r.Kind == "Temperature")
                .Select(r => new Metric(r.Name, r)));
            if (!Readings.Any(r => r.Kind == "Clock")) items.Add(new("Частота", null));
            if ((Gpu || Type == "Cpu") && !Readings.Any(r => r.Kind == "Power")) items.Add(new("Потребление", null));
            if (Type == "Memory" && !Readings.Any(r => r.Kind == "Temperature")) items.Add(new("Температура памяти", null, Missing: ReadingQuality.NoSensor));
        }
        if (Disk)
        {
            items.Add(new("Чтение", Named("Throughput", "Read Rate", "Read Speed")));
            items.Add(new("Запись", Named("Throughput", "Write Rate", "Write Speed")));
            items.Add(new("Оставшийся ресурс SMART", SmartPercent("Life", "Remaining Life", "SSD Life Left")));
            if (Id.StartsWith("/nvme/", StringComparison.Ordinal))
            {
                items.Add(new("Использованный ресурс NVMe", SmartPercent("Percentage Used", allowAbove100: true)));
                items.Add(new("Доступный резерв NVMe", SmartPercent("Available Spare")));
                items.Add(new("Порог резерва NVMe", SmartPercent("Available Spare Threshold")));
            }
            items.AddRange(Readings.Where(r => r.Kind == "Data" && (r.Name == "Data Read" || r.Name == "Data Written")).Select(r => new Metric(r.Name, r)));
        }
        if (Type is "Motherboard" or "SuperIO")
            items.AddRange(Readings.Where(r => r.Kind is "Voltage" or "Temperature" or "Fan" or "Control")
                .Select(r => new Metric(r.Name, r, Section: BoardSection(r))).OrderBy(metric => metric.Section, StringComparer.CurrentCulture));
        return items.ToArray();
    }

    Reading? SmartPercent(params string[] labels) => SmartPercent(false, labels);
    Reading? SmartPercent(string label, bool allowAbove100) => SmartPercent(allowAbove100, label);

    Reading? SmartPercent(bool allowAbove100, params string[] labels)
    {
        var reading = Named("Level", labels);
        return reading?.Value is float value && (value < 0 || value > (allowAbove100 ? 255 : 100))
            ? reading with { Quality = ReadingQuality.Unverified, Value = null }
            : reading;
    }

    static string BoardSection(Reading reading)
    {
        if (reading.Quality == ReadingQuality.Unverified || reading.Name.StartsWith("Temperature #", StringComparison.Ordinal) ||
            reading.Name.StartsWith("Voltage #", StringComparison.Ordinal)) return "Неопознанные входы";
        return reading.Kind switch
        {
            "Voltage" => "Напряжения", "Temperature" => "Температуры", "Fan" => "Тахометры", _ => "Выходы"
        };
    }
}

public sealed class Telemetry
{
    readonly Dictionary<SensorReference, Reading> bySensor;
    public static readonly Telemetry Empty = new([]);
    public Reading[] Readings { get; }
    public DeviceReadings[] Devices { get; }
    public DeviceReadings? Cpu { get; }
    public DeviceReadings? Ram { get; }
    public DeviceReadings[] Gpus { get; }
    public DeviceReadings[] Disks { get; }
    public TemperatureInput[] CurveSources { get; }
    public Reading[] HistorySources { get; }
    public Reading? Find(SensorReference sensor) => bySensor.GetValueOrDefault(sensor);

    public static TimeSpan MaxAge(string deviceType) => deviceType switch
    {
        "Storage" => TimeSpan.FromSeconds(22), "Memory" => TimeSpan.FromSeconds(8), _ => TimeSpan.FromSeconds(4)
    };

    public Telemetry(Reading[] readings)
    {
        Readings = readings;
        bySensor = readings.GroupBy(r => r.Reference).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        Devices = readings.GroupBy(reading => reading.DeviceId).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DeviceReadings(group)).ToArray();
        Cpu = Devices.FirstOrDefault(device => device.Type == "Cpu");
        Ram = Devices.SingleOrDefault(device => device.Id == "/ram");
        Gpus = Devices.Where(device => device.Gpu).ToArray();
        Disks = Devices.Where(device => device.Disk).ToArray();
        HistorySources = Devices.SelectMany(device => new[] { device.Temperature, device.Load, device.Hotspot })
            .OfType<Reading>().Concat(readings.Where(reading => reading.Kind == "Fan" && reading.Value > 0 && reading.DeviceType == "SuperIO"))
            .DistinctBy(reading => reading.Reference).ToArray();
        CurveSources = Devices.Where(device => device.Type == "Cpu" || device.Gpu)
            .Where(device => Devices.Count(other => other.Type == device.Type && other.Name == device.Name) == 1)
            .SelectMany(device => device.Readings.Where(reading => reading.Kind == "Temperature" &&
                (reading == device.Temperature || reading == device.Hotspot || device.Type == "Cpu" &&
                 reading.Name is "Core Max" or "Core Average" or "CCD1 (Tdie)")))
            .GroupBy(reading => reading.Reference).Where(group => group.Count() == 1).Select(group => group.Single())
            .Select(reading => new TemperatureInput(reading.Reference, $"{reading.Device} · {reading.Name}",
                reading.Available && FanPolicy.ValidTemperature(reading.Value) ? reading.Value : null)).ToArray();
    }
}
