using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AirDesk;
using Xunit;

public sealed class DeviceSelectionTests
{
    const string First = @"PCI\VEN_10DE&DEV_2882\4&10001&0&0008";
    const string Second = @"PCI\VEN_10DE&DEV_2882\4&20001&0&0008";

    [Fact]
    public void ExplicitGpuSelectionDoesNotFallBackToAnotherCard()
    {
        var data = new Telemetry([Gpu(0, 40)]);
        Assert.NotNull(data.PrimaryGpu(null));
        Assert.Null(data.PrimaryGpu("/gpu-nvidia/1"));
    }

    [Fact]
    public void IdenticalAdaptersKeepTheSelectedPhysicalGpuWhenIndexesChange()
    {
        var original = new Telemetry([Gpu(0, 40, First), Gpu(1, 70, Second)]);
        var reordered = new Telemetry([Gpu(0, 72, Second), Gpu(1, 41, First)]);
        Assert.Equal(72f, reordered.PrimaryGpu(original.Gpus[1].Id, original.Gpus[1].InstanceId)?.Temperature?.Value);
        Assert.Null(new Telemetry([Gpu(1, 41, First)]).PrimaryGpu(original.Gpus[1].Id, original.Gpus[1].InstanceId));
        Assert.Null(new Telemetry([Gpu(0, 40, First), Gpu(1, 70, First)]).PrimaryGpu(original.Gpus[0].Id, First));
    }

    [Fact]
    public void CurveRequiresDistinctHardwareIdsForIdenticalGpus()
    {
        var original = new Telemetry([Gpu(0, 40, First), Gpu(1, 70, Second)]);
        var reordered = new Telemetry([Gpu(0, 71, Second), Gpu(1, 41, First)]);
        Assert.Equal(2, original.CurveSources.Length);
        var hotGpu = original.CurveSources.Single(source => source.Celsius == 70).Source;
        Assert.Equal(71f, Curve.Resolve(FanProfile.Default with { Primary = hotGpu }, 35, reordered.CurveSources).Temperature);
        Assert.Null(Curve.Resolve(FanProfile.Default with { Primary = Gpu(1, 70).Reference }, 35, original.CurveSources).Temperature);
        Assert.Empty(new Telemetry([Gpu(0, 40, First), Gpu(1, 70, First)]).CurveSources);
        Assert.DoesNotContain(new Telemetry([Gpu(0, 40), Gpu(1, 70, Second)]).CurveSources, source => source.Celsius == 40);
        var singleGpu = new Telemetry([Gpu(1, 71, Second)]);
        Assert.Null(Curve.Resolve(FanProfile.Default with { Primary = Gpu(1, 70).Reference }, 35, singleGpu.CurveSources).Temperature);
        Assert.Equal(71f, Curve.Resolve(FanProfile.Default with { Primary = hotGpu }, 35, singleGpu.CurveSources).Temperature);
        Assert.Empty(new Telemetry([Gpu(1, 71)]).CurveSources);
    }

    [Fact]
    public void SameNamedSensorsKeepDistinctReferencesAcrossGpuIndexChanges()
    {
        var primary = Gpu(0, 40, First);
        var secondary = primary with { Id = primary.DeviceId + "/temperature/1", Value = 53 };
        var original = new Telemetry([primary, secondary, Gpu(1, 70, Second)]);

        var moved = Gpu(1, 41, First);
        var movedSecondary = moved with { Id = moved.DeviceId + "/temperature/1", Value = 54 };
        var reordered = new Telemetry([Gpu(0, 71, Second), movedSecondary, moved]);
        var sources = reordered.HistoryCandidates.ToDictionary(reordered.PersistentReference);
        Assert.Equal(3, sources.Count);
        Assert.Equal(41f, sources[original.PersistentReference(primary)].Value);
        Assert.Equal(54f, sources[original.PersistentReference(secondary)].Value);
        Assert.Equal(41f, reordered.PrimaryGpu(primary.DeviceId, First)?.Temperature?.Value);
    }

    [Fact]
    public void DuplicateSensorIdsAreExcludedWithoutHidingTheOtherGpu()
    {
        var ambiguous = Gpu(0, 40, First);
        var other = Gpu(1, 70, Second);
        var data = new Telemetry([ambiguous, ambiguous with { Value = 95 }, other]);

        Assert.Null(data.Find(ambiguous.Reference));
        Assert.Null(data.PrimaryGpu(ambiguous.DeviceId, First)?.Temperature);
        Assert.Equal(other, Assert.Single(data.HistoryCandidates));
        Assert.Equal(other, Assert.Single(data.HistorySources));
        Assert.Equal(70f, Assert.Single(data.CurveSources).Celsius);
    }

    [Fact]
    public async Task DefaultHistoryKeepsIdenticalGpusSeparateAcrossRestartAndIndexChanges()
    {
        var directory = Directory.CreateTempSubdirectory("AirDesk-device-history-");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var first = Gpu(0, 43, First) with { UpdatedAt = now.AddSeconds(-10) };
            var second = Gpu(1, 70, Second) with { UpdatedAt = now.AddSeconds(-10) };
            var original = new Telemetry([first, second]);
            var legacyAt = now.AddSeconds(-20);
            var legacy = new HistoryPoint(first.Reference, first.Name, first.Unit, legacyAt, legacyAt, 42, 42, 42, 1, 10);
            File.WriteAllText(Path.Combine(directory.FullName, "history-v1.json"),
                JsonSerializer.Serialize(new { Version = 1, Points = new[] { legacy } }));

            var store = new HistoryStore(directory.FullName);
            await store.ReadAsync();
            store.Configure(HistoryRetention.Hour);
            store.Offer(original, now.AddSeconds(-10));
            await store.StopAsync();

            var restarted = new HistoryStore(directory.FullName);
            await restarted.ReadAsync();
            restarted.Configure(HistoryRetention.Hour);
            var reordered = new Telemetry([Gpu(0, 72, Second) with { UpdatedAt = now }, Gpu(1, 44, First) with { UpdatedAt = now }]);
            restarted.Offer(reordered, now);
            await restarted.StopAsync();

            var points = await new HistoryStore(directory.FullName).ReadAsync();
            var series = points.ToLookup(point => point.Sensor);
            Assert.Equal(3, series.Count);
            Assert.Equal(legacy, Assert.Single(series[first.Reference]));
            Assert.Equal(new[] { 43f, 44f }, series[original.PersistentReference(first)].Select(point => point.Mean));
            Assert.Equal(new[] { 70f, 72f }, series[original.PersistentReference(second)].Select(point => point.Mean));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task HistoryFollowsAnExplicitlySelectedGpuAfterIndexesChange()
    {
        var directory = Directory.CreateTempSubdirectory("AirDesk-device-history-");
        try
        {
            var original = new Telemetry([Gpu(0, 43, First), Gpu(1, 70, Second)]);
            var selection = original.PersistentReference(original.Gpus[1].Temperature!);
            var store = new HistoryStore(directory.FullName);
            await store.ReadAsync();
            store.Configure(HistoryRetention.Hour, [selection]);
            var reordered = new Telemetry([Gpu(0, 72, Second), Gpu(1, 40, First)]);
            store.Offer(reordered, DateTimeOffset.UtcNow);
            await store.StopAsync();
            var point = Assert.Single(await new HistoryStore(directory.FullName).ReadAsync());
            Assert.Equal(selection, point.Sensor);
            Assert.Equal(72f, point.Mean);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void RenamedDeviceAndGpuSelectionSurviveReloadWithoutChangingTheHardwareName()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"AirDesk-device-{Guid.NewGuid():N}");
        try
        {
            string path = Path.Combine(folder, "settings.json");
            var preferences = UserPreferences.Load(path);
            var data = new Telemetry([Gpu(0, 70, First), Gpu(1, 40, Second)]);
            var selected = data.Gpus[1];
            preferences.SelectGpu(selected);
            preferences.SetAlias(UserPreferences.DeviceAliasKey(selected.Id, selected.InstanceId), "   Вторая карта   ", selected.Id);
            var reloaded = UserPreferences.Load(path);
            var reordered = new Telemetry([Gpu(0, 45, Second), Gpu(1, 78, First)]);
            var resolved = Assert.IsType<DeviceReadings>(reloaded.SelectedGpu(reordered));
            Assert.Equal("Вторая карта", reloaded.DeviceLabel(resolved.Id, resolved.Name, resolved.InstanceId));
            Assert.Equal("RTX", resolved.Name);
            Assert.Equal("RTX", reloaded.DeviceLabel(reordered.Gpus[1].Id, "RTX", First));
            reloaded.SetAlias(UserPreferences.DeviceAliasKey(resolved.Id, resolved.InstanceId), "", resolved.Id);
            Assert.Equal("RTX", UserPreferences.Load(path).DeviceLabel(resolved.Id, resolved.Name, resolved.InstanceId));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    static Reading Gpu(int index, float temperature, string? instance = null) => new("RTX", $"/gpu-nvidia/{index}",
        "GpuNvidia", "GPU Core", "Temperature", "°C", temperature, $"/gpu-nvidia/{index}/temperature/0", ReadingQuality.Fresh, DateTimeOffset.UtcNow)
        { DeviceInstance = instance };
}
