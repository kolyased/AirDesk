using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AirDesk;
using Xunit;
using Xunit.Abstractions;

public sealed class HistoryTests(ITestOutputHelper output)
{
    static void Check(bool result, string name) => Assert.True(result, name);

    [Fact]
    public void ExportProducesCsvAndJsonWithoutOpeningAnInternalFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var time = DateTimeOffset.UtcNow;
            var row = new HistoryPoint(new("id", "device", "  =1+1"), "CPU \"Package\"", "°C", time, time, 42.5f, 41, 44, 1, 10);
            string csv = Path.Combine(directory, "history.csv");
            string json = Path.Combine(directory, "history.json");
            HistoryExport.Save(csv, [row]);
            HistoryExport.Save(json, [row]);
            string data = File.ReadAllText(csv);
            Assert.Contains("\"'  =1+1\"", data, StringComparison.Ordinal);
            Assert.Contains("\"CPU \"\"Package\"\"\"", data, StringComparison.Ordinal);
            Assert.Contains(",42.5,41,44,1", data, StringComparison.Ordinal);
            using var journal = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal(42.5, journal.RootElement.GetProperty("Points")[0].GetProperty("Mean").GetDouble());
            Assert.Throws<IOException>(() => HistoryExport.Save(Path.Combine(AtomicJson.Folder, "fan-binding-v1.json"), [row]));
            Assert.Throws<IOException>(() => HistoryExport.Save(Path.Combine(directory, "wrong.txt"), [row]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UnreadableHistoryCannotBeOverwrittenOrClearedWithoutPreservingOriginal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "history-v1.json");
            const string unknown = "{\"Version\":93,\"Points\":[]}";
            File.WriteAllText(path, unknown);
            var history = new HistoryStore(directory);
            history.Configure(HistoryRetention.Hour);
            await history.ReadAsync();
            await history.StopAsync();
            await Assert.ThrowsAsync<InvalidDataException>(history.ClearAsync);
            Assert.Equal(unknown, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CorruptCompressedHistoryDoesNotRestoreStaleLegacyData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string legacy = Path.Combine(directory, "history-v1.json");
            string compressed = Path.Combine(directory, "history-v2.json.gz");
            var now = DateTimeOffset.UtcNow;
            var disk = DiskTemperature(0, now);
            var legacyPoint = new HistoryPoint(disk.Reference, disk.Name, disk.Unit, now, now, 40, 40, 40, 1, 10);
            string original = JsonSerializer.Serialize(new { Version = 1, Points = new[] { legacyPoint } });
            File.WriteAllText(legacy, original);
            File.WriteAllText(compressed, "incomplete");
            var history = new HistoryStore(directory);
            Assert.Empty(await history.ReadAsync());
            Assert.NotNull(history.Error);
            history.Configure(HistoryRetention.Hour);
            history.Offer(new Telemetry([disk]), now);
            await history.StopAsync();
            await Assert.ThrowsAsync<InvalidDataException>(history.ClearAsync);
            Assert.Equal("incomplete", File.ReadAllText(compressed));
            Assert.Equal(original, File.ReadAllText(legacy));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AggregationKeepsTimestampsExtremaAndRetention()
    {
        var now = DateTimeOffset.Parse("2026-11-18T15:40:00Z");
        var key = new SensorReference("/gpu-nvidia/0/temperature/0", "/gpu-nvidia/0", "Example GPU");
        HistoryPoint Point(DateTimeOffset time, float value) => new(key, "GPU Core", "°C", time, time, value, value, value, 1, 10);
        var old = now.AddHours(-2);
        var raw = new[] { Point(old, 35), Point(old.AddSeconds(10), 45), Point(old.AddSeconds(120), 60), Point(now, 40) };
        var compact = HistoryModel.Compact(raw, now, HistoryRetention.Day);
        Check(compact.Length == 3 && compact[0] is { Mean: 40, Min: 35, Max: 45, Count: 2 } && compact[0].At == old.AddSeconds(10),
            "Historic rollup preserves weighted mean, extrema, count and real timestamps");
        Check(!HistoryModel.Continuous(compact[0], compact[1]), "Rollup does not span a gap");
        Check(!HistoryModel.Continuous(Point(old, 40), Point(old.AddSeconds(20), 42)), "A missed scheduled sample breaks continuity");
        Check(HistoryModel.Compact(raw, now, HistoryRetention.Hour).Single().At == now, "Expired points rotate out");
        var many = Enumerable.Range(0, HistoryModel.MaxPoints + 100).Select(i => Point(now.AddMilliseconds(-i * 10), 40));
        Check(HistoryModel.Compact(many, now, HistoryRetention.Hour).Length == HistoryModel.MaxPoints, "History stays bounded even on short intervals");
    }

    [Fact]
    public async Task DefaultRecordingIncludesEveryRepresentativeSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new HistoryStore(directory);
            await history.ReadAsync();
            history.Configure(HistoryRetention.Hour);
            var now = DateTimeOffset.UtcNow;
            var readings = Enumerable.Range(0, 48).Select(i => DiskTemperature(i, now)).ToArray();
            history.Offer(new Telemetry(readings), now);
            await history.StopAsync();
            var restarted = new HistoryStore(directory);
            Assert.Equal(readings.Select(reading => reading.Id).Order(),
                (await restarted.ReadAsync()).Select(point => point.Sensor.Id).Order());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MonthlyAggregationKeepsOldestAndNewestSamplesAcrossManySensors()
    {
        var now = DateTimeOffset.Parse("2026-11-18T16:00:00Z");
        var old = now.AddDays(-29);
        var raw = Enumerable.Range(0, 48).SelectMany(i =>
        {
            var sensor = DiskTemperature(i, now).Reference;
            return Enumerable.Range(0, 29 * 48 + 1).Select(step =>
            {
                var at = old.AddMinutes(30 * step);
                float temperature = 40 + (float)(Math.Sin(step * .037 + i) * 9);
                return new HistoryPoint(sensor, "Temperature", "°C", at, at, temperature, temperature - 1, temperature + 1, 15, 1800);
            });
        });

        var elapsed = Stopwatch.StartNew();
        var compact = HistoryModel.Compact(raw, now, HistoryRetention.Month);
        using var encoded = new MemoryStream();
        Assert.True(HistoryJournal.TryEncode(compact, encoded));
        long writeMs = elapsed.ElapsedMilliseconds;
        string path = Path.Combine(Path.GetTempPath(), "AirDesk-history-" + Guid.NewGuid().ToString("N") + ".json.gz");
        try
        {
            File.WriteAllBytes(path, encoded.ToArray());
            elapsed.Restart();
            Assert.Equal(compact.OrderBy(point => point.Sensor.Id).ThenBy(point => point.At),
                HistoryJournal.Read(path).OrderBy(point => point.Sensor.Id).ThenBy(point => point.At));
            output.WriteLine($"48 sensors × 29 days, {compact.Length:N0} retained rows, {encoded.Length:N0} bytes compressed. Compact/encode {writeMs} ms, read/compare {elapsed.ElapsedMilliseconds} ms.");
        }
        finally { File.Delete(path); }
        var bySensor = compact.GroupBy(point => point.Sensor).ToArray();
        Assert.Equal(48, bySensor.Length);
        foreach (var rows in bySensor)
        {
            Assert.Equal(old, rows.Min(point => point.From));
            Assert.Equal(now, rows.Max(point => point.At));
            Assert.Equal((29 * 48 + 1) * 15, rows.Sum(point => point.Count));
        }
    }

    static Reading DiskTemperature(int i, DateTimeOffset at) =>
        new($"Disk {i}", $"/disk/{i}", "Storage", "Temperature", "Temperature", "°C", 40, $"/disk/{i}/temperature/0", ReadingQuality.Fresh, at);

    [Fact]
    public async Task SensorSelectionSurvivesRestartAndCanIncludeNonDefaultSources()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = Path.Combine(directory, "settings.json");
            File.WriteAllText(settings, "{\"SchemaVersion\":2,\"Retention\":1}");
            var old = UserPreferences.Load(settings);
            Assert.Null(old.HistorySensors);
            var now = DateTimeOffset.UtcNow;
            var disk = DiskTemperature(0, now);
            var extra = disk with { Name = "Secondary Temperature", Id = disk.Id + "/secondary" };
            var telemetry = new Telemetry([disk, extra]);
            Assert.DoesNotContain(telemetry.HistorySources, reading => reading.Reference == extra.Reference);
            old.HistorySensors = [extra.Reference, extra.Reference];
            old.Save();
            var saved = UserPreferences.Load(settings);
            Assert.Equal(extra.Reference, Assert.Single(saved.HistorySensors!));
            var history = new HistoryStore(directory);
            await history.ReadAsync();
            history.Configure(saved.Retention, saved.HistorySensors);
            history.Offer(telemetry, now);
            history.Configure(saved.Retention, []);
            history.Offer(new Telemetry([extra with { UpdatedAt = now.AddSeconds(10) }]), now.AddSeconds(10));
            await history.StopAsync();
            var restarted = new HistoryStore(directory);
            Assert.Equal(extra.Reference, Assert.Single(await restarted.ReadAsync()).Sensor);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DisablingRecordingFlushesPendingSamples()
    {
        var directory = Directory.CreateTempSubdirectory("AirDesk-history-");
        try
        {
            var history = new HistoryStore(directory.FullName);
            await history.ReadAsync();
            history.Configure(HistoryRetention.Month);
            await history.FlushAsync();
            var now = DateTimeOffset.UtcNow;
            history.Offer(new Telemetry([DiskTemperature(0, now) with { Value = 45 }]), now);
            string file = Path.Combine(directory.FullName, "history-v2.json.gz");
            Assert.False(File.Exists(file));
            history.Configure(HistoryRetention.Off);
            using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!File.Exists(file)) await Task.Delay(20, deadline.Token);
            Assert.Equal(45f, Assert.Single(HistoryJournal.Read(file)).Mean);
            await history.StopAsync();
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task MigratesLegacyHistoryAndReportsRecordedRangeFromActualTimestamps()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var legacy = Path.Combine(directory, "history-v1.json");
            var compressed = Path.Combine(directory, "history-v2.json.gz");
            var disk = DiskTemperature(0, now);
            var start = now.AddDays(-16);
            var old = new HistoryPoint(disk.Reference, disk.Name, disk.Unit, start, start.AddMinutes(10), 40, 38, 45, 5, 120);
            File.WriteAllText(legacy, JsonSerializer.Serialize(new { Version = 1, Points = new[] { old } }));

            var history = new HistoryStore(directory);
            Assert.Equal(old, Assert.Single(await history.ReadAsync()));
            history.Configure(HistoryRetention.Month);
            await history.FlushAsync();
            Assert.True(File.Exists(compressed));
            Assert.False(File.Exists(legacy));
            history.Offer(new Telemetry([disk]), now);
            await history.StopAsync();

            var restarted = new HistoryStore(directory);
            var rows = await restarted.ReadAsync();
            Assert.Equal(old, rows[0]);
            Assert.Equal((start, now), HistoryModel.RecordedRange(rows));
            Assert.True(restarted.OwnsPath(legacy));
            Assert.True(restarted.OwnsPath(compressed));
            await restarted.ClearAsync();
            Assert.False(File.Exists(compressed));
            Assert.Null(HistoryModel.RecordedRange(await new HistoryStore(directory).ReadAsync()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ThresholdsRequireRealSamplesHysteresisAndQuietPeriod()
    {
        var now = DateTimeOffset.Parse("2026-11-18T15:40:00Z");
        var key = new SensorReference("/gpu-nvidia/0/temperature/0", "/gpu-nvidia/0", "Example GPU");
        Reading ReadingAt(DateTimeOffset at, float value, ReadingQuality quality = ReadingQuality.Fresh) => new(key.DeviceName,
            key.DeviceId, "GpuNvidia", "GPU Core", "Temperature", "°C", value, key.Id, quality, at);
        var rule = new ThresholdRule(Guid.NewGuid(), key, "GPU", ThresholdDirection.Above, 70, 3, 3, 12);
        var evaluator = new ThresholdEvaluator();
        int Observe(int seconds, float value, ReadingQuality quality = ReadingQuality.Fresh) =>
            evaluator.Observe([rule], new Telemetry([ReadingAt(now.AddSeconds(seconds), value, quality)])).Count();
        Check(Observe(0, 72) == 0 && Observe(2, 72) == 0 && Observe(3, 75) == 1, "Alert only after threshold is sustained");
        Check(Observe(4, 69) == 0 && Observe(5, 74) == 0, "Hysteresis prevents flapping");
        Check(Observe(6, 66) == 0 && Observe(7, 75) == 0 && Observe(10, 76) == 0, "Quiet interval suppresses a quick second excursion");
        Check(Observe(12, 76, ReadingQuality.Stale) == 0 && Observe(15, 76) == 0 && Observe(18, 76) == 1,
            "Missing and stale data break a threshold hold; recovery arms a new observed hold");
        Check(Observe(18, 76) == 0, "Identical cached samples are not counted twice");
    }

    [Fact]
    public void SettingsMigrateAndRecoverWithoutOverwritingFutureVersions()
    {
        var now = DateTimeOffset.Parse("2026-11-18T15:40:00Z");
        var key = new SensorReference("/gpu-nvidia/0/temperature/0", "/gpu-nvidia/0", "Example GPU");
        var rule = new ThresholdRule(Guid.NewGuid(), key, "GPU", ThresholdDirection.Above, 70, 3, 3, 12);
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "settings.json");
            File.WriteAllText(file, "{\"Theme\":1,\"Aliases\":{\"/cpu\":\"Processor\"}}");
            var legacy = UserPreferences.Load(file, _ => { });
            Check(legacy.SchemaVersion == 2 && legacy.Theme == Appearance.Dark && legacy.Label("/cpu", "") == "Processor", "Unversioned settings migrate without losing aliases");
            legacy.Save(); legacy.Theme = Appearance.Light; legacy.Rules = [rule]; legacy.Save();
            var loaded = UserPreferences.Load(file, _ => { });
            Check(loaded.Theme == Appearance.Light && loaded.Rules.Single() == rule, "Profile preferences and rules survive restart");
            File.WriteAllText(file, "{");
            var recovered = UserPreferences.Load(file, _ => { });
            Check(recovered.Theme == Appearance.Dark && recovered.RecoveryNotice != null, "Corrupt primary settings recover from last good backup");
            recovered.Save();
            Check(UserPreferences.Load(file, _ => { }).Theme == Appearance.Dark && File.ReadAllText(file + ".bak").StartsWith('{'),
                "Repair keeps the valid backup");
            File.WriteAllText(file, "{\"SchemaVersion\":99}");
            var future = UserPreferences.Load(file, _ => { });
            Assert.Throws<InvalidOperationException>(future.Save);

        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StoreSurvivesRestartAndClearsMemoryAndDisk()
    {
        var now = DateTimeOffset.Parse("2026-11-18T15:40:00Z");
        var key = new SensorReference("/gpu-nvidia/0/temperature/0", "/gpu-nvidia/0", "Example GPU");
        Reading ReadingAt(DateTimeOffset at, float value, ReadingQuality quality = ReadingQuality.Fresh) => new(key.DeviceName,
            key.DeviceId, "GpuNvidia", "GPU Core", "Temperature", "°C", value, key.Id, quality, at);
        string directory = Path.Combine(Path.GetTempPath(), "AirDesk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var history = new HistoryStore(directory);
            await history.ReadAsync();
            var sample = DateTimeOffset.UtcNow;
            history.Offer(new Telemetry([ReadingAt(sample, 40)]), sample);
            Check(history.Read().Length == 0, "Disabled history does not collect");
            history.Configure(HistoryRetention.Hour);
            history.Offer(new Telemetry([ReadingAt(sample, 40)]), sample);
            history.Offer(new Telemetry([ReadingAt(sample.AddSeconds(1), 41)]), sample.AddSeconds(1));
            await history.StopAsync();
            var restarted = new HistoryStore(directory);
            Check((await restarted.ReadAsync()).Single().Mean == 40, "Batched history survives restart and respects sampling interval");
            await restarted.ClearAsync();
            Check(restarted.Read().Length == 0 && !File.Exists(Path.Combine(directory, "history-v2.json.gz")), "Cleanup clears both memory and file");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

}
