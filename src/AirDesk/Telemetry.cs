using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace AirDesk;

public enum ReadingQuality { Fresh, NoSensor, AccessDenied, Unsupported, Unresponsive, Stale, Unverified }

public record Reading(string Device, string DeviceId, string DeviceType, string Name, string Kind, string Unit,
    float? Value, string Id, ReadingQuality Quality, DateTimeOffset? UpdatedAt)
{
    public string Source => "LibreHardwareMonitor";
    public bool Available => Quality == ReadingQuality.Fresh && Value.HasValue;
    public SensorReference Reference => new(Id, DeviceId, Device);

    public static Reading From(IHardware device, ISensor sensor, DateTimeOffset now, ReadingQuality quality)
    {
        float? value = sensor.Value is float number && float.IsFinite(number) ? number : null;
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
        return new(device.Name, device.Identifier.ToString(), device.HardwareType.ToString(), sensor.Name,
            sensor.SensorType.ToString(), unit, quality is ReadingQuality.Fresh or ReadingQuality.Unverified ? value : null,
            sensor.Identifier.ToString(), quality, quality is ReadingQuality.Fresh or ReadingQuality.Unverified ? now : null);
    }
}

public record Metric(string Label, Reading? Reading, float? Derived = null, string Unit = "");

public sealed class DeviceReadings
{
    readonly Dictionary<string, Reading> byKey;
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
        byKey = Readings.GroupBy(reading => reading.Id[Id.Length..]).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
    }

    Reading? Get(string key) => byKey.GetValueOrDefault(key);
    Reading? Named(string kind, params string[] labels) => labels.Select(label => Readings.Where(r => r.Kind == kind && r.Name == label).Take(2).ToArray())
        .FirstOrDefault(matches => matches.Length == 1)?[0];
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
    public float? MemoryTotal => Gpu ? Named("SmallData", "GPU Memory Total")?.Value : Get("/data/0")?.Value + Get("/data/1")?.Value;

    public Metric[] Details()
    {
        var items = new List<Metric>();
        if (Temperature != null || Gpu || Disk || Type == "Cpu") items.Add(new("Температура", Temperature));
        if (Load != null || Gpu || Disk || Type is "Cpu" or "Memory") items.Add(new("Нагрузка / активность", Load));
        if (Gpu)
        {
            items.Add(new("Горячая точка", Hotspot));
            items.Add(new("VRAM занято", MemoryUsed));
            items.Add(new("VRAM всего", null, MemoryTotal, "МБ"));
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
            if (Type == "Memory" && !Readings.Any(r => r.Kind == "Temperature")) items.Add(new("Температура памяти", null));
        }
        if (Disk)
        {
            items.Add(new("Чтение", Named("Throughput", "Read Rate", "Read Speed")));
            items.Add(new("Запись", Named("Throughput", "Write Rate", "Write Speed")));
            items.Add(new("Оставшийся ресурс SMART", Named("Level", "Life", "Remaining Life", "SSD Life Left")));
            items.AddRange(Readings.Where(r => r.Kind == "Data" && (r.Name == "Data Read" || r.Name == "Data Written")).Select(r => new Metric(r.Name, r)));
        }
        if (Type is "Motherboard" or "SuperIO")
            items.AddRange(Readings.Where(r => r.Kind is "Voltage" or "Temperature" or "Fan" or "Control").Select(r => new Metric(r.Name, r)));
        return items.ToArray();
    }
}

public sealed class Telemetry
{
    public static readonly Telemetry Empty = new([]);
    public Reading[] Readings { get; }
    public DeviceReadings[] Devices { get; }
    public DeviceReadings? Cpu { get; }
    public DeviceReadings? Ram { get; }
    public DeviceReadings[] Gpus { get; }
    public DeviceReadings[] Disks { get; }
    public TemperatureInput[] CurveSources { get; }

    public Telemetry(Reading[] readings)
    {
        Readings = readings;
        Devices = readings.GroupBy(reading => reading.DeviceId).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DeviceReadings(group)).ToArray();
        Cpu = Devices.FirstOrDefault(device => device.Type == "Cpu");
        Ram = Devices.SingleOrDefault(device => device.Id == "/ram");
        Gpus = Devices.Where(device => device.Gpu).ToArray();
        Disks = Devices.Where(device => device.Disk).ToArray();
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
