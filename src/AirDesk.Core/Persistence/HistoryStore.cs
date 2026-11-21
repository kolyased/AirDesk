using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

internal sealed record HistorySaveResult(string? Error = null)
{
    public bool Succeeded => Error is null;
}

internal sealed class HistoryStore
{
    const string RecoverySuffix = ".recovered-";
    sealed record UnreadableArchive(string Path, Exception Failure)
    {
        public string Message => $"Не удалось прочитать {System.IO.Path.GetFileName(Path)}. {Failure.Message}";
        public InvalidDataException WriteBlocked() =>
            new($"{Message} Исходный файл сохранён. Новые показания не записаны.", Failure);
    }

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
    bool stopped, legacyLoaded, rollbackRowsPending, createRecoveredArchive;
    volatile UnreadableArchive? unreadableArchive;
    volatile string? saveError, legacyError, recoveryNotice;
    public string? Error
    {
        get
        {
            string? error = saveError ?? unreadableArchive?.Message ?? legacyError;
            string? notice = recoveryNotice;
            return error == null ? notice : notice == null ? error : $"{error}\n{notice}";
        }
    }
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
        if (!LoadArchive(path)) LoadArchive(legacyPath);
    }

    bool LoadArchive(string source)
    {
        try
        {
            var rows = source == path ? ReadCurrentAndRollbackHistory() : HistoryJournal.ReadLegacy(source);
            lock (gate)
            {
                legacyLoaded = source == legacyPath;
                unreadableArchive = null;
                saveError = null;
                archive = rows.Where(point => point?.Valid == true).OrderBy(point => point.At).TakeLast(HistoryModel.MaxPoints).ToArray();
                savedRange = HistoryModel.RecordedRange(archive);
                var saved = new Dictionary<SensorReference, DateTimeOffset>();
                foreach (var point in archive) saved[point.Sensor] = point.At;
                // A file that was temporarily locked can become readable after sampling has started.
                removedPending += pending.RemoveAll(point => saved.TryGetValue(point.Sensor, out var time) && point.At <= time);
                foreach (var (sensor, time) in saved)
                    if (!observed.TryGetValue(sensor, out var latest) || time > latest) observed[sensor] = time;
                revision++;
            }
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception failure)
        {
            lock (gate)
            {
                unreadableArchive = new(source, failure);
                revision++;
            }
            DiagnosticLog.Write(failure.ToString());
        }
        return true;
    }

    HistoryPoint[] ReadCurrentAndRollbackHistory()
    {
        var current = HistoryJournal.Read(path);
        try
        {
            var legacy = HistoryJournal.ReadLegacy(legacyPath);
            // An empty v2 journal still supersedes measurements made before it was saved.
            DateTimeOffset through = current.Where(point => point.Valid).Max(point => (DateTimeOffset?)point.At)
                ?? File.GetLastWriteTimeUtc(path);
            var latestAccepted = DateTimeOffset.UtcNow.AddMinutes(1);
            var rollback = legacy
                .Where(point => point?.Valid == true && point.From > through && point.At <= latestAccepted).Distinct().ToArray();
            rollbackRowsPending = rollback.Length != 0;
            return rollbackRowsPending ? [.. current, .. rollback] : current;
        }
        catch (FileNotFoundException) { return current; }
        catch (DirectoryNotFoundException) { return current; }
        catch (Exception failure)
        {
            legacyError = "Не удалось прочитать history-v1.json: " + failure.Message;
            DiagnosticLog.Write(failure.ToString());
            return current;
        }
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
                var sensor = telemetry.PersistentReference(reading);
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
    public bool OwnsPath(string candidate)
    {
        string fullPath = Path.GetFullPath(candidate);
        return new[] { path, legacyPath }.Select(Path.GetFullPath).Any(owned =>
            fullPath.Equals(owned, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(owned + RecoverySuffix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(fullPath.AsSpan(owned.Length + RecoverySuffix.Length), "N", out _));
    }

    public async Task<HistorySaveResult> FlushAsync()
    {
        await loading.ConfigureAwait(false);
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(Flush).ConfigureAwait(false);
            return new();
        }
        catch (Exception failure)
        {
            Fail(failure);
            return new(failure.Message);
        }
        finally { disk.Release(); }
    }

    void Flush()
    {
        if (unreadableArchive is { } problem)
        {
            lock (gate) if (pending.Count == 0) return;
            RecoverArchive(problem);
        }

        HistoryPoint[] current;
        HistoryRetention strategy;
        long endOfBatch;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            strategy = retention;
            var cutoff = now - HistoryModel.Period(strategy);
            bool rotate = strategy != HistoryRetention.Off && archive.Any(point => point.From < cutoff);
            if (pending.Count == 0 && !rotate && !rollbackRowsPending && !(legacyLoaded && strategy != HistoryRetention.Off)) return;
            current = [.. archive, .. pending];
            endOfBatch = removedPending + pending.Count;
        }
        var compact = strategy == HistoryRetention.Off
            ? current.Where(point => point.Valid).OrderBy(point => point.At).TakeLast(HistoryModel.MaxPoints).ToArray()
            : HistoryModel.Compact(current, now, strategy);
        using var encoded = new MemoryStream();
        while (!HistoryJournal.TryEncode(compact, encoded))
            compact = compact.Skip(Math.Max(1, compact.Length / 10)).ToArray();
        WriteArchive(encoded);
        var range = HistoryModel.RecordedRange(compact);
        lock (gate)
        {
            archive = compact;
            savedRange = range;
            rollbackRowsPending = false;
            DiscardPending((int)Math.Clamp(endOfBatch - removedPending, 0, pending.Count));
            revision++;
            saveError = null;
        }
        if (legacyLoaded)
        {
            File.Delete(legacyPath);
            legacyLoaded = false;
        }
    }

    void RecoverArchive(UnreadableArchive problem)
    {
        if (problem.Failure is IOException or UnauthorizedAccessException)
        {
            // Retry access errors before deciding whether the file contains a readable archive.
            if (LoadArchive(problem.Path))
            {
                if (unreadableArchive is not { } latest) return;
                if (latest.Failure is not (InvalidDataException or JsonException)) throw latest.WriteBlocked();
            }
        }
        else if (problem.Failure is not (InvalidDataException or JsonException)) throw problem.WriteBlocked();

        // A superseded v1 file must not be loaded as active history if recovery stops before publishing the replacement.
        if (problem.Path == path) PreserveRollbackArchive();
        PreserveArchive(problem.Path);
        createRecoveredArchive = true;
        lock (gate)
        {
            unreadableArchive = null;
            revision++;
        }
    }

    void PreserveRollbackArchive()
    {
        try { HistoryJournal.ReadLegacy(legacyPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (UnsupportedHistoryVersionException failure)
        {
            legacyError = new UnreadableArchive(legacyPath, failure).Message;
            return;
        }
        catch (Exception failure) when (failure is InvalidDataException or JsonException)
        {
            DiagnosticLog.Write(failure.ToString());
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new UnreadableArchive(legacyPath, failure).WriteBlocked();
        }
        PreserveArchive(legacyPath);
    }

    void PreserveArchive(string source)
    {
        string preserved = source + RecoverySuffix + Guid.NewGuid().ToString("N");
        try
        {
            if ((File.GetAttributes(source) & FileAttributes.Directory) != 0)
                throw new IOException("Путь к файлу истории занят папкой.");
            File.Move(source, preserved);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Не удалось сохранить исходный файл истории «{Path.GetFileName(source)}». " +
                $"Новые показания не записаны. {failure.Message}", failure);
        }
        lock (gate)
        {
            string notice = $"Исходный файл истории сохранён отдельно под именем «{Path.GetFileName(preserved)}».";
            recoveryNotice = recoveryNotice is null ? notice : $"{recoveryNotice}\n{notice}";
            revision++;
        }
    }

    void WriteArchive(MemoryStream encoded)
    {
        if (!createRecoveredArchive)
        {
            AtomicJson.WriteFile(path, stream => encoded.CopyTo(stream));
            return;
        }

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            AtomicJson.WriteFile(temporary, stream => encoded.CopyTo(stream));
            File.Move(temporary, path);
            createRecoveredArchive = false;
        }
        finally { File.Delete(temporary); }
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
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            if (unreadableArchive != null || legacyError != null || createRecoveredArchive)
                throw new InvalidDataException("Исходный файл истории сохранён. Скопируйте или переместите его отдельно после выхода из AirDesk.");
            await Task.Run(() =>
            {
                lock (gate)
                {
                    File.Delete(legacyPath);
                    File.Delete(path);
                    legacyLoaded = rollbackRowsPending = false;
                    archive = []; pending.Clear(); observed.Clear(); savedRange = null;
                    revision++;
                    lastSample = DateTimeOffset.UtcNow;
                }
                saveError = null;
            }).ConfigureAwait(false);
        }
        finally { disk.Release(); }
    }

    public Task<HistorySaveResult> StopAsync()
    {
        lock (gate) stopped = true;
        return FlushAsync();
    }

    void Fail(Exception failure)
    {
        lock (gate)
        {
            if (saveError != failure.Message) revision++;
            saveError = failure.Message;
        }
        DiagnosticLog.Write(failure.ToString());
    }
}
