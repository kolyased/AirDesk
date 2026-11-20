using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

public enum ReadingQuality { Fresh, NoSensor, AccessDenied, Unsupported, Unresponsive, Stale, Unverified }

public record Reading(string Device, string DeviceId, string DeviceType, string Name, string Kind, string Unit,
    float? Value, string Id, ReadingQuality Quality, DateTimeOffset? UpdatedAt)
{
    public string Source => "LibreHardwareMonitor";
    public string? DeviceInstance { get; init; }
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
    public string? InstanceId { get; }
    public Reading[] Readings { get; }
    public bool Gpu => Type is "GpuNvidia" or "GpuAmd" or "GpuIntel";
    public bool Disk => Type == "Storage";

    public DeviceReadings(IGrouping<string, Reading> readings, string? instanceId = null)
    {
        Id = readings.Key;
        Readings = readings.OrderBy(reading => reading.Id, StringComparer.Ordinal).ToArray();
        Name = Readings[0].Device;
        Type = Readings[0].DeviceType;
        InstanceId = instanceId;
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
        if (Temperature != null || Gpu || Disk || Type == "Cpu")
            items.Add(new(Gpu ? "Температура ядра" : "Температура", Temperature, Section: "Температуры", Missing: ReadingQuality.NoSensor));
        if (Gpu) items.Add(new("Горячая точка", Hotspot, Section: "Температуры", Missing: ReadingQuality.NoSensor));
        if (Load != null || Gpu || Disk || Type is "Cpu" or "Memory")
            items.Add(new(Disk ? "Активность" : "Загрузка", Load, Section: "Нагрузка"));
        if (Gpu)
        {
            items.Add(new("Занято", MemoryUsed, Section: "Видеопамять (VRAM)"));
            items.Add(new("Всего", VramTotal, Section: "Видеопамять (VRAM)"));
        }
        if (Id == "/ram")
        {
            var used = MemoryUsed;
            var free = Get("/data/1");
            var failure = used is { Available: false } ? used : free;
            items.Add(new("Используется", used, Section: "Объём памяти"));
            items.Add(new("Всего", null, MemoryTotal, "ГБ", "Объём памяти",
                failure is { Available: false } ? failure.Quality : ReadingQuality.Unsupported));
        }
        if (Type is "Cpu" or "Memory" || Gpu)
        {
            AddKind("Clock", "Частоты", "Частота");
            if (Gpu || Type == "Cpu" || Readings.Any(r => r.Kind == "Power")) AddKind("Power", "Мощность", "Потребление");
            if (Type == "Memory") AddKind("Temperature", "Температуры", "Температура памяти", ReadingQuality.NoSensor);
        }
        if (Disk)
        {
            items.Add(new("Чтение", Named("Throughput", "Read Rate", "Read Speed"), Section: "Передача данных"));
            items.Add(new("Запись", Named("Throughput", "Write Rate", "Write Speed"), Section: "Передача данных"));
            items.AddRange(Readings.Where(r => r.Kind == "Data" && (r.Name == "Data Read" || r.Name == "Data Written"))
                .Select(r => new Metric(r.Name, r, Section: "Передача данных")));
            items.Add(new("Оставшийся ресурс SMART", SmartPercent("Life", "Remaining Life", "SSD Life Left"), Section: "Ресурс накопителя"));
            if (Id.StartsWith("/nvme/", StringComparison.Ordinal))
            {
                items.Add(new("Использованный ресурс NVMe", SmartPercent("Percentage Used", allowAbove100: true), Section: "Ресурс накопителя"));
                items.Add(new("Доступный резерв NVMe", SmartPercent("Available Spare"), Section: "Ресурс накопителя"));
                items.Add(new("Порог резерва NVMe", SmartPercent("Available Spare Threshold"), Section: "Ресурс накопителя"));
            }
        }
        if (Type is "Motherboard" or "SuperIO")
            items.AddRange(Readings.Where(r => r.Kind is "Voltage" or "Temperature" or "Fan" or "Control")
                .Select(r => new Metric(r.Name, r, Section: BoardSection(r))).OrderBy(metric => metric.Section, StringComparer.CurrentCulture));
        if (items.Count == 0) items.AddRange(Readings.Select(r => new Metric(r.Name, r, Section: "Показания")));
        return items.ToArray();

        void AddKind(string kind, string section, string placeholder, ReadingQuality missing = ReadingQuality.Unsupported)
        {
            int previous = items.Count;
            items.AddRange(Readings.Where(r => r.Kind == kind).Select(r => new Metric(r.Name, r, Section: section)));
            if (items.Count == previous) items.Add(new(placeholder, null, Section: section, Missing: missing));
        }
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
    internal const string GpuInstancePrefix = "gpu-instance:";
    readonly Dictionary<SensorReference, Reading> bySensor;
    readonly Dictionary<string, string> instances;
    public static readonly Telemetry Empty = new([]);
    public Reading[] Readings { get; }
    public DeviceReadings[] Devices { get; }
    public DeviceReadings? Cpu { get; }
    public DeviceReadings? Ram { get; }
    public DeviceReadings[] Gpus { get; }
    public DeviceReadings[] Disks { get; }
    public TemperatureInput[] CurveSources { get; }
    public Reading[] HistorySources { get; }
    public IEnumerable<Reading> HistoryCandidates => Readings.Where(reading => reading.Kind is "Temperature" or "Load" or "Fan" &&
        bySensor.ContainsKey(reading.Reference));
    public Reading? Find(SensorReference sensor) => bySensor.GetValueOrDefault(sensor);
    internal SensorReference PersistentReference(Reading reading) => InstanceReference(reading, instances.GetValueOrDefault(reading.DeviceId)) ?? reading.Reference;
    public DeviceReadings? PrimaryGpu(string? id, string? instanceId = null, string? originalName = null) => instanceId != null
        ? Gpus.SingleOrDefault(device => device.InstanceId == instanceId)
        : id == null ? Gpus.FirstOrDefault() : Gpus.FirstOrDefault(device => device.Id == id &&
            (originalName == null || device.Name == originalName));

    public static TimeSpan MaxAge(string deviceType) => deviceType switch
    {
        "Storage" => TimeSpan.FromSeconds(22), "Memory" => TimeSpan.FromSeconds(8), _ => TimeSpan.FromSeconds(4)
    };

    public Telemetry(Reading[] readings)
    {
        Readings = readings;
        bySensor = readings.GroupBy(r => r.Reference).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var adapterIds = readings.Where(reading => reading.DeviceInstance != null)
            .Select(reading => (reading.DeviceId, Instance: reading.DeviceInstance!)).Distinct().ToArray();
        instances = adapterIds.GroupBy(pair => pair.Instance, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).GroupBy(pair => pair.DeviceId, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Instance, StringComparer.Ordinal);
        Devices = readings.GroupBy(reading => reading.DeviceId).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DeviceReadings(group, instances.GetValueOrDefault(group.Key))).ToArray();
        Cpu = Devices.FirstOrDefault(device => device.Type == "Cpu");
        Ram = Devices.SingleOrDefault(device => device.Id == "/ram");
        Gpus = Devices.Where(device => device.Gpu).ToArray();
        Disks = Devices.Where(device => device.Disk).ToArray();
        HistorySources = Devices.SelectMany(device => new[] { device.Temperature, device.Load, device.Hotspot })
            .OfType<Reading>().Concat(readings.Where(reading => reading.Kind == "Fan" && reading.Value > 0 && reading.DeviceType == "SuperIO"))
            .Where(reading => bySensor.ContainsKey(reading.Reference)).DistinctBy(reading => reading.Reference).ToArray();
        var modelNames = Devices.ToLookup(device => (device.Type, device.Name.ToUpperInvariant()));
        CurveSources = Devices.Where(device => device.Type == "Cpu" || device.Gpu)
            .SelectMany(device => CurveFor(device, modelNames[(device.Type, device.Name.ToUpperInvariant())].Count() == 1))
            .GroupBy(input => input.Source).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    }

    static IEnumerable<TemperatureInput> CurveFor(DeviceReadings device, bool uniqueModel)
    {
        if (device.Gpu ? device.InstanceId == null : !uniqueModel) yield break;
        string name = uniqueModel ? device.Name : $"{device.Name} ({device.Id})";
        var temperature = device.Temperature;
        var hotspot = device.Hotspot;
        foreach (var reading in device.Readings)
        {
            if (reading.Kind != "Temperature" || (reading != temperature && reading != hotspot &&
                !(device.Type == "Cpu" && reading.Name is "Core Max" or "Core Average" or "CCD1 (Tdie)"))) continue;
            var instance = InstanceReference(reading, device.InstanceId);
            if (device.Gpu && instance == null) continue;
            var source = instance ?? reading.Reference;
            yield return new(source, $"{name} · {reading.Name}",
                reading.Available && FanPolicy.ValidTemperature(reading.Value) ? reading.Value : null, reading.Id);
        }
    }

    static SensorReference? InstanceReference(Reading reading, string? instanceId)
    {
        if (instanceId == null || !reading.Id.StartsWith(reading.DeviceId + "/", StringComparison.Ordinal)) return null;
        string key = GpuInstancePrefix + instanceId;
        return new(key + reading.Id[reading.DeviceId.Length..], key, reading.Device);
    }
}
