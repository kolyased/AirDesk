using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

internal sealed class HistoryStore
{
    sealed record Journal(int Version, HistoryPoint[] Points);

    readonly string path;
    readonly List<HistoryPoint> points = new();
    readonly Dictionary<SensorReference, DateTimeOffset> observed = new();
    readonly object gate = new();
    readonly SemaphoreSlim disk = new(1, 1);
    readonly Task loading;
    DateTimeOffset lastSample, lastFlush = DateTimeOffset.UtcNow;
    HistoryRetention retention;
    int revision, savedRevision;
    bool stopped;
    volatile string? error;
    public string? Error => error;
    public HistoryRetention Retention { get { lock (gate) return retention; } }
    public int Revision { get { lock (gate) return revision; } }

    public HistoryStore(string? folder = null)
    {
        path = Path.Combine(folder ?? AtomicJson.Folder, "history-v1.json");
        loading = Task.Run(Load);
    }

    void Load()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            if (info.Length > HistoryModel.MaxBytes) throw new InvalidDataException("Файл истории превышает лимит.");
            var document = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path));
            if (document?.Version != 1 || document.Points == null) throw new InvalidDataException("Неизвестный формат истории.");
            lock (gate)
            {
                points.AddRange(document.Points.Where(point => point?.Valid == true).OrderBy(point => point.At).TakeLast(HistoryModel.MaxPoints));
                revision++;
                savedRevision = retention == HistoryRetention.Off ? revision : revision - 1;
            }
        }
        catch (Exception failure) { Fail(failure); }
    }

    public void Configure(HistoryRetention value)
    {
        lock (gate)
        {
            if (retention == value) return;
            retention = value;
            lastSample = default;
            if (value != HistoryRetention.Off) revision++;
        }
        if (value != HistoryRetention.Off) _ = FlushAsync();
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
                foreach (var key in observed.Keys.Where(key => telemetry.Find(key) == null).ToArray()) observed.Remove(key);
            foreach (var reading in telemetry.HistorySources.Take(32))
            {
                if (reading is not { Available: true, Value: float value, UpdatedAt: { } time } ||
                    observed.TryGetValue(reading.Reference, out var old) && time <= old) continue;
                observed[reading.Reference] = time;
                points.Add(new(reading.Reference, reading.Name, reading.Unit, time, time, value, value, value, 1, interval));
                revision++;
            }
            if (points.Count > HistoryModel.MaxPoints + 512)
                points.RemoveRange(0, points.Count - HistoryModel.MaxPoints);
            if (now - lastFlush >= TimeSpan.FromMinutes(1)) { lastFlush = now; flush = true; }
        }
        if (flush) _ = FlushAsync();
    }

    public HistoryPoint[] Read()
    {
        lock (gate) return points.ToArray();
    }

    public async Task<HistoryPoint[]> ReadAsync() { await loading.ConfigureAwait(false); return Read(); }
    public bool OwnsPath(string candidate) => Path.GetFullPath(candidate).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    public async Task FlushAsync()
    {
        await loading.ConfigureAwait(false);
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                HistoryPoint[] current;
                HistoryRetention strategy;
                int snapshot;
                lock (gate)
                {
                    if (savedRevision == revision) return;
                    current = points.ToArray();
                    strategy = retention;
                    snapshot = revision;
                }
                var compact = HistoryModel.Compact(current, DateTimeOffset.UtcNow, strategy == HistoryRetention.Off ? HistoryRetention.Month : strategy);
                while (JsonSerializer.SerializeToUtf8Bytes(new Journal(1, compact)).Length > HistoryModel.MaxBytes)
                    compact = compact.Skip(Math.Max(1, compact.Length / 10)).ToArray();
                AtomicJson.Write(path, new Journal(1, compact));
                lock (gate)
                {
                    if (snapshot == revision)
                    {
                        points.Clear(); points.AddRange(compact);
                        revision++;
                        savedRevision = revision;
                    }
                    else savedRevision = snapshot;
                }
                error = null;
            }).ConfigureAwait(false);
        }
        catch (Exception failure) { Fail(failure); }
        finally { disk.Release(); }
    }

    public async Task ClearAsync()
    {
        await loading.ConfigureAwait(false);
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                lock (gate)
                {
                    File.Delete(path);
                    points.Clear(); observed.Clear();
                    revision++;
                    savedRevision = revision;
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

    void Fail(Exception failure) { error = failure.Message; DiagnosticLog.Write(failure.ToString()); }
}
