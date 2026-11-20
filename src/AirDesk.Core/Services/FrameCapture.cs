using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

internal sealed class FrameCapture : IDisposable
{
    const int MaxBatch = 256 * 1024;
    const int SharingViolation = unchecked((int)0x80070020);
    const long FrameLifetimeMilliseconds = 3000;
    const string CheckPresentMon = "Проверьте, идёт ли запись, и посмотрите сообщения ETW в консоли PresentMon.";
    const string WaitingForHeader = "CSV PresentMon ещё не содержит полного заголовка. " + CheckPresentMon;
    readonly CancellationTokenSource stop = new();
    readonly byte[] buffer = new byte[8192];
    readonly List<byte> partial = new(256);
    readonly string path;
    readonly int processId;
    readonly Task run;
    byte[] header = [];
    PresentMonFrames? parser;
    long offset;
    bool discardLine;
    DateTime stamp;
    int disposed;
    volatile Result latest = new(null, 0, "Подключение к CSV PresentMon…");

    sealed record Result(FrameRate? Frames, long At, string? Status);

    public FrameCapture(string file, int pid)
    {
        path = Path.GetFullPath(file);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || pid <= 0)
            throw new ArgumentException("Выберите CSV на локальном диске и укажите положительный PID.");
        processId = pid;
        run = Task.Run(() => ReadLoop(stop.Token));
    }

    public string FilePath => path;
    public int ProcessId => processId;
    public FrameRate? Current => Snapshot.Frames;
    public (FrameRate? Frames, string Status) Snapshot
    {
        get
        {
            var value = latest;
            if (Environment.TickCount64 - value.At <= FrameLifetimeMilliseconds)
                return (value.Frames, value.Frames != null ? "Кадры поступают." : value.Status ?? "Ожидание новых кадров PresentMon…");

            return (null, value.Status ?? "Новых кадров выбранного PID нет. " + CheckPresentMon);
        }
    }

    async Task ReadLoop(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        bool recovering = false;
        try
        {
            do
            {
                try
                {
                    await ReadOnce(recovering, cancel).ConfigureAwait(false);
                    recovering = false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or FormatException)
                {
                    if (error is FileNotFoundException or DirectoryNotFoundException or FormatException) parser = null;
                    string status = error switch
                    {
                        FileNotFoundException or DirectoryNotFoundException => "CSV PresentMon не найден. Проверьте путь к файлу. " + CheckPresentMon,
                        UnauthorizedAccessException or SecurityException => "Нет доступа к CSV PresentMon. Проверьте разрешение на чтение файла.",
                        FormatException => error.Message,
                        IOException { HResult: SharingViolation } => "Не удалось прочитать CSV PresentMon. Файл заблокирован для чтения. Для живого FPS используйте --output_stdout по инструкции AirDesk.",
                        _ => "Не удалось прочитать CSV PresentMon. Чтение будет повторено."
                    };
                    latest = new(null, 0, status);
                    recovering = true;
                }
            } while (await timer.WaitForNextTickAsync(cancel).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error)
        {
            latest = new(null, 0, "Чтение CSV PresentMon остановлено из-за ошибки. Подключите файл заново.");
            DiagnosticLog.Write(error.ToString());
        }
    }

    async Task ReadOnce(bool recovering, CancellationToken cancel)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, useAsync: true);
        long end = stream.Length;
        // On Unix, creation time can be synthesized from timestamps that change on append.
        DateTime created = OperatingSystem.IsWindows() ? File.GetCreationTimeUtc(stream.SafeFileHandle) : default;
        if (parser == null || created != stamp || end < offset || !await HasSameHeader(stream, cancel).ConfigureAwait(false))
        {
            parser = null;
            stream.Position = 0;
            int limit = (int)Math.Min(buffer.Length, end);
            int count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, limit), limit, false, cancel).ConfigureAwait(false);
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            if (newline < 0)
            {
                if (count == buffer.Length) throw new FormatException("Некорректный заголовок CSV PresentMon.");
                latest = new(null, 0, WaitingForHeader);
                return;
            }
            var newParser = new PresentMonFrames(Encoding.UTF8.GetString(buffer, 0, newline).TrimEnd('\r'));
            var newHeader = buffer.AsSpan(0, newline + 1).ToArray();
            stream.Position = end - 1;
            int last = stream.ReadByte();
            if (last < 0)
            {
                latest = new(null, 0, WaitingForHeader);
                return;
            }
            parser = newParser;
            header = newHeader;
            latest = new(null, Environment.TickCount64, null);
            offset = end;
            discardLine = last != '\n';
            partial.Clear();
            stamp = created;
            return;
        }
        if (recovering) latest = new(null, Environment.TickCount64, null);
        if (end == offset) return;
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
        if (lines.Count == 0) return;

        var batch = parser.Analyze(lines, processId);
        if (batch.Rate is { } measured)
        {
            latest = new(measured, Environment.TickCount64, null);
            return;
        }

        string detail = batch.Status switch
        {
            FrameBatchStatus.MissingPid => "В новых строках CSV нет кадров выбранного процесса. Проверьте PID и выбранный файл.",
            FrameBatchStatus.InvalidIntervals => "В новых строках выбранного PID нет пригодных данных об интервалах кадров.",
            _ => "Новых кадров выбранного PID пока недостаточно для расчёта FPS."
        };
        latest = latest with { Status = detail };
    }

    async ValueTask<bool> HasSameHeader(FileStream stream, CancellationToken cancel)
    {
        int count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, header.Length), header.Length, false, cancel).ConfigureAwait(false);
        return count == header.Length && buffer.AsSpan(0, count).SequenceEqual(header);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        _ = run.ContinueWith(task =>
        {
            if (task.Exception != null) DiagnosticLog.Write(task.Exception.ToString());
            stop.Dispose();
        }, TaskScheduler.Default);
    }
}
