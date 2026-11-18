using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AirDesk;
using Xunit;

public sealed class HistoryTests
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
        Check(HistoryModel.Compact(raw, now, HistoryRetention.Hour).Single().At == now, "Expired points rotate out");
        var many = Enumerable.Range(0, HistoryModel.MaxPoints + 100).Select(i => Point(now.AddMilliseconds(-i * 10), 40));
        Check(HistoryModel.Compact(many, now, HistoryRetention.Hour).Length == HistoryModel.MaxPoints, "History stays bounded even on short intervals");
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
            Check(restarted.Read().Length == 0 && !File.Exists(Path.Combine(directory, "history-v1.json")), "Cleanup clears both memory and file");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

}
