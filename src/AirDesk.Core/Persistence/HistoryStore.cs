using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

internal sealed class HistoryStore
{
    readonly string path, legacyPath;
    HistoryPoint[] archive = [];
    readonly List<HistoryPoint> pending = new();
    readonly Dictionary<SensorReference, DateTimeOffset> observed = new();
    readonly object gate = new();
    readonly SemaphoreSlim disk = new(1, 1);
    readonly Task loading;
    DateTimeOffset lastSample, lastFlush = DateTimeOffset.UtcNow;
    (DateTimeOffset From, DateTimeOffset At)? savedRange;
    HistoryRetention retention;
    HashSet<SensorReference>? sensors;
    long removedPending;
    int revision;
    bool stopped, legacyLoaded;
    bool invalidOnLoad;
    volatile string? error;
    public string? Error => error;
    public HistoryRetention Retention { get { lock (gate) return retention; } }
    public int Revision { get { lock (gate) return revision; } }
    public (DateTimeOffset From, DateTimeOffset At)? SavedRange { get { lock (gate) return savedRange; } }

    public HistoryStore(string? folder = null)
    {
        folder ??= AtomicJson.Folder;
        path = Path.Combine(folder, "history-v2.json.gz");
        legacyPath = Path.Combine(folder, "history-v1.json");
        loading = Task.Run(Load);
    }

    void Load()
    {
        try
        {
            HistoryPoint[] rows;
            if (File.Exists(path)) rows = HistoryJournal.Read(path);
            else if (File.Exists(legacyPath))
            {
                rows = HistoryJournal.ReadLegacy(legacyPath);
                legacyLoaded = true;
            }
            else return;
            lock (gate)
            {
                archive = rows.Where(point => point?.Valid == true).OrderBy(point => point.At).TakeLast(HistoryModel.MaxPoints).ToArray();
                savedRange = HistoryModel.RecordedRange(archive);
                foreach (var point in archive) observed[point.Sensor] = point.At;
                revision++;
            }
        }
        catch (Exception failure) { invalidOnLoad = true; Fail(failure); }
    }

    public void Configure(HistoryRetention value, IEnumerable<SensorReference>? selection = null)
    {
        bool retentionChanged;
        var next = selection?.ToHashSet();
        lock (gate)
        {
            retentionChanged = retention != value;
            bool sameSensors = sensors == null ? next == null : next != null && sensors.SetEquals(next);
            if (!retentionChanged && sameSensors) return;
            retention = value;
            sensors = next;
            lastSample = default;
            revision++;
        }
        if (retentionChanged) _ = FlushAsync();
    }

    public void Offer(Telemetry telemetry, DateTimeOffset now)
    {
        if (!loading.IsCompleted) return;
        bool flush = false;
        lock (gate)
        {
            int interval = HistoryModel.Interval(retention);
            if (stopped || interval == 0 || now >= lastSample && now - lastSample < TimeSpan.FromSeconds(interval)) return;
            lastSample = now;
            if (observed.Count > 256)
            {
                var converted = telemetry.HistoryCandidates.Select(telemetry.PersistentReference).ToHashSet();
                foreach (var key in observed.Keys.Where(key => telemetry.Find(key) == null && !converted.Contains(key)).ToArray()) observed.Remove(key);
            }
            var sources = sensors == null ? telemetry.HistorySources : telemetry.HistoryCandidates;
            foreach (var reading in sources)
            {
                var sensor = sensors == null ? reading.Reference : telemetry.PersistentReference(reading);
                if (sensors != null && !sensors.Contains(sensor)) continue;
                if (reading is not { Available: true, Value: float value, UpdatedAt: { } time } ||
                    !float.IsFinite(value) || time > now.AddMinutes(1) ||
                    observed.TryGetValue(sensor, out var old) && time <= old) continue;
                observed[sensor] = time;
                pending.Add(new(sensor, reading.Name, reading.Unit, time, time, value, value, value, 1, interval));
                revision++;
            }
            DiscardPending(Math.Max(0, pending.Count - HistoryModel.MaxPoints));
            var flushInterval = TimeSpan.FromMinutes(retention is HistoryRetention.Week or HistoryRetention.Month ? 5 : 1);
            if (now < lastFlush || now - lastFlush >= flushInterval) { lastFlush = now; flush = true; }
        }
        if (flush) _ = FlushAsync();
    }

    public HistoryPoint[] Read()
    {
        lock (gate) return [.. archive, .. pending];
    }

    public async Task<HistoryPoint[]> ReadAsync() { await loading.ConfigureAwait(false); return Read(); }
    public bool OwnsPath(string candidate) => new[] { path, legacyPath }.Any(owned =>
        Path.GetFullPath(candidate).Equals(Path.GetFullPath(owned), StringComparison.OrdinalIgnoreCase));

    public async Task FlushAsync()
    {
        await loading.ConfigureAwait(false);
        if (invalidOnLoad) return;
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(Flush).ConfigureAwait(false);
        }
        catch (Exception failure) { Fail(failure); }
        finally { disk.Release(); }
    }

    void Flush()
    {
        HistoryPoint[] current;
        HistoryRetention strategy;
        long endOfBatch;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            strategy = retention;
            var cutoff = now - HistoryModel.Period(strategy);
            bool rotate = strategy != HistoryRetention.Off && archive.Any(point => point.From < cutoff);
            if (pending.Count == 0 && !rotate && !(legacyLoaded && strategy != HistoryRetention.Off)) return;
            current = [.. archive, .. pending];
            endOfBatch = removedPending + pending.Count;
        }
        var compact = strategy == HistoryRetention.Off
            ? current.Where(point => point.Valid).OrderBy(point => point.At).TakeLast(HistoryModel.MaxPoints).ToArray()
            : HistoryModel.Compact(current, now, strategy);
        using var encoded = new MemoryStream();
        while (!HistoryJournal.TryEncode(compact, encoded))
            compact = compact.Skip(Math.Max(1, compact.Length / 10)).ToArray();
        AtomicJson.WriteFile(path, stream => encoded.CopyTo(stream));
        var range = HistoryModel.RecordedRange(compact);
        lock (gate)
        {
            archive = compact;
            savedRange = range;
            DiscardPending((int)Math.Clamp(endOfBatch - removedPending, 0, pending.Count));
            revision++;
            error = null;
        }
        if (legacyLoaded)
        {
            File.Delete(legacyPath);
            legacyLoaded = false;
        }
    }

    void DiscardPending(int count)
    {
        if (count == 0) return;
        pending.RemoveRange(0, count);
        removedPending += count;
    }

    public async Task ClearAsync()
    {
        await loading.ConfigureAwait(false);
        if (invalidOnLoad) throw new InvalidDataException("Исходный файл истории сохранён. Скопируйте или переместите его отдельно после выхода из AirDesk.");
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                lock (gate)
                {
                    File.Delete(legacyPath);
                    File.Delete(path);
                    legacyLoaded = false;
                    archive = []; pending.Clear(); observed.Clear(); savedRange = null;
                    revision++;
                    lastSample = DateTimeOffset.UtcNow;
                }
                error = null;
            }).ConfigureAwait(false);
        }
        finally { disk.Release(); }
    }

    public Task StopAsync()
    {
        lock (gate) stopped = true;
        return FlushAsync();
    }

    void Fail(Exception failure)
    {
        lock (gate)
        {
            if (error != failure.Message) revision++;
            error = failure.Message;
        }
        DiagnosticLog.Write(failure.ToString());
    }
}
