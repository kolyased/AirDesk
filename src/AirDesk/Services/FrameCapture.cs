using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

internal sealed class FrameCapture : IDisposable
{
    const int MaxBatch = 256 * 1024;
    readonly CancellationTokenSource stop = new();
    readonly byte[] buffer = new byte[8192];
    readonly List<byte> partial = new(256);
    readonly string path;
    readonly int processId;
    readonly Task run;
    PresentMonFrames? parser;
    long offset;
    bool discardLine;
    DateTime stamp;
    volatile Result latest = new(null, default, "Ожидание новых кадров PresentMon…");

    sealed record Result(FrameRate? Frames, DateTimeOffset At, string Status);

    public FrameCapture(string file, int pid)
    {
        path = Path.GetFullPath(file);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || pid <= 0)
            throw new ArgumentException("Выберите CSV на локальном диске и укажите положительный PID.");
        processId = pid;
        run = Task.Run(() => ReadLoop(stop.Token));
    }

    public FrameRate? Current
    {
        get
        {
            var value = latest;
            return DateTimeOffset.UtcNow - value.At <= TimeSpan.FromSeconds(3) ? value.Frames : null;
        }
    }
    public string Status => Current == null ? (latest.Frames == null ? latest.Status : "Новые кадры выбранного PID пока не поступают.") : "Кадры поступают.";

    async Task ReadLoop(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { await ReadOnce(cancel).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
                {
                    parser = null;
                    latest = new(null, default, error is FormatException ? error.Message : "CSV PresentMon пока недоступен.");
                }
            } while (await timer.WaitForNextTickAsync(cancel).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    async Task ReadOnce(CancellationToken cancel)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, useAsync: true);
        long end = stream.Length;
        DateTime created = File.GetCreationTimeUtc(path);
        if (parser == null || created != stamp || end < offset)
        {
            int count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancel).ConfigureAwait(false);
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            if (newline < 0)
            {
                if (count == buffer.Length) throw new FormatException("Некорректный заголовок CSV PresentMon.");
                return;
            }
            parser = new PresentMonFrames(Encoding.UTF8.GetString(buffer, 0, newline).TrimEnd('\r'));
            latest = new(null, default, "Ожидание новых кадров PresentMon…");
            offset = end;
            stream.Position = end - 1;
            discardLine = stream.ReadByte() != '\n';
            partial.Clear();
            stamp = created;
            return;
        }
        if (end - offset > MaxBatch)
        {
            offset = end - MaxBatch;
            partial.Clear();
            discardLine = true;
        }
        stream.Position = offset;
        var lines = new List<string>();
        int remaining = (int)(end - offset);
        while (remaining > 0)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), cancel).ConfigureAwait(false);
            if (count == 0) break;
            remaining -= count;
            offset += count;
            for (int index = 0; index < count; index++)
            {
                byte value = buffer[index];
                if (value == '\n')
                {
                    if (!discardLine && partial.Count > 0) lines.Add(Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(partial)).TrimEnd('\r'));
                    partial.Clear();
                    discardLine = false;
                }
                else if (!discardLine)
                {
                    if (partial.Count < 4096) partial.Add(value);
                    else { partial.Clear(); discardLine = true; }
                }
            }
        }
        if (parser.Summarize(lines, processId) is { } measured) latest = new(measured, DateTimeOffset.UtcNow, "");
    }

    public void Dispose()
    {
        stop.Cancel();
        _ = run.ContinueWith(task =>
        {
            if (task.Exception != null) Monitor.Log(task.Exception);
            stop.Dispose();
        }, TaskScheduler.Default);
    }
}
