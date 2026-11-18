using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace AirDesk;

public record Snapshot(DateTimeOffset Time, Telemetry Telemetry, CoolingView Cooling, string? PollIssue);

public sealed class Monitor
{
    readonly ConcurrentQueue<(Action<CoolingSafety> Action, TaskCompletionSource<string> Result)> commands = new();
    readonly AutoResetEvent wake = new(false);
    readonly TaskCompletionSource<CoolingView?> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    long lastProgress, testDeadline;
    int stopping, releaseRequested, watchdogReported;
    volatile Snapshot? latest;
    public Snapshot? Latest => latest;
    public bool Stalled => Volatile.Read(ref stopping) == 0 && elapsed.ElapsedTicks - Interlocked.Read(ref lastProgress) >=
        (long)(Stopwatch.Frequency * CoolingSafety.Freshness.TotalSeconds);

    public void Start()
    {
        new Thread(Run) { Name = "AirDesk hardware executor", IsBackground = true }.Start();
    }

    public Task<string> Submit(Action<CoolingSafety> action)
    {
        if (Volatile.Read(ref stopping) != 0 || finished.Task.IsCompleted || Stalled)
            return Task.FromResult("Исполнитель недоступен. Проверьте состояние драйвера.");
        if (commands.Count >= 8) return Task.FromResult("Исполнитель занят. Дождитесь окончания команды.");
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Enqueue((action, result));
        wake.Set();
        return result.Task;
    }

    public void Release()
    {
        Interlocked.Exchange(ref releaseRequested, 1);
        wake.Set();
    }

    public async Task<CoolingView?> ShutdownAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        wake.Set();
        return await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    void Run()
    {
        var computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true, IsMemoryEnabled = true,
            IsGpuEnabled = true, IsStorageEnabled = true };
        CoolingSafety? safety = null;
        WindowsCooling? adapter = null;
        Telemetry telemetry = Telemetry.Empty;
        string? pollIssue = null;
        bool StopPending() => Volatile.Read(ref stopping) != 0 || Volatile.Read(ref releaseRequested) != 0;
        try
        {
            adapter = new WindowsCooling();
            using var deadline = new Timer(_ =>
            {
                long due = Interlocked.Read(ref testDeadline);
                if (due != 0 && elapsed.Elapsed.Ticks >= due) wake.Set();
                if (Stalled && safety?.OwnsOutput == true && Interlocked.Exchange(ref watchdogReported, 1) == 0)
                {
                    Interlocked.Exchange(ref releaseRequested, 1);
                    wake.Set();
                    LogEvent("Аппаратный вызов просрочен. Возврат запрошен, выполнение ожидает освобождения драйвера.");
                }
            }, null, 100, 100);
            safety = new(adapter, new LocalCalibration(), ControllerProcesses.Scan, () => elapsed.Elapsed, LogEvent, StopPending);
            computer.Open();
            adapter.Discover(computer.Hardware.SelectMany(Walk).ToArray());
            TimeSpan nextPoll = TimeSpan.Zero, nextTick = TimeSpan.Zero, nextDiscovery = elapsed.Elapsed + TimeSpan.FromSeconds(5);
            while (!safety.ShutdownComplete)
            {
                Interlocked.Exchange(ref lastProgress, elapsed.ElapsedTicks);
                Interlocked.Exchange(ref watchdogReported, 0);
                if (Volatile.Read(ref stopping) != 0) safety.Close();
                if (Interlocked.Exchange(ref releaseRequested, 0) != 0)
                {
                    CancelQueued();
                    safety.Stop("пользователь вернул управление BIOS/EC");
                    nextTick = TimeSpan.Zero;
                }
                bool expired = safety.Deadline is { } due && elapsed.Elapsed >= due;
                if (elapsed.Elapsed >= nextTick || expired || Volatile.Read(ref stopping) != 0)
                {
                    safety.Tick();
                    nextTick = elapsed.Elapsed + TimeSpan.FromMilliseconds(200);
                }
                Publish();
                if (safety.ShutdownComplete) break;

                if (Volatile.Read(ref stopping) == 0 && commands.TryDequeue(out var command))
                {
                    try { command.Action(safety); command.Result.TrySetResult(""); }
                    catch (Exception error) { Log(error); command.Result.TrySetResult(error.Message); }
                    Publish();
                }

                if (Volatile.Read(ref stopping) == 0 && elapsed.Elapsed >= nextPoll && safety.View().State != ChannelState.Restoring)
                {
                    TimeSpan started = elapsed.Elapsed;
                    try
                    {
                        var readings = new List<Reading>();
                        var available = computer.Hardware.SelectMany(Walk).ToArray();
                        if (adapter.RetryDiscovery && elapsed.Elapsed >= nextDiscovery)
                        {
                            adapter.Discover(available);
                            nextDiscovery = elapsed.Elapsed + TimeSpan.FromSeconds(5);
                        }
                        foreach (var hardware in available)
                        {
                            try
                            {
                                adapter.Update(hardware);
                                DateTimeOffset now = DateTimeOffset.UtcNow;
                                var quality = hardware.HardwareType == HardwareType.SuperIO && adapter.BoardUpdateBlocked ? ReadingQuality.Stale : ReadingQuality.Fresh;
                                readings.AddRange(hardware.Sensors.Select(sensor => Reading.From(hardware, sensor, now, quality)));
                            }
                            catch (Exception error)
                            {
                                Log(error);
                                readings.AddRange(hardware.Sensors.Select(sensor => Reading.From(hardware, sensor, DateTimeOffset.UtcNow,
                                    error is UnauthorizedAccessException ? ReadingQuality.AccessDenied : ReadingQuality.Unresponsive)));
                            }
                        }
                        pollIssue = elapsed.Elapsed - started >= CoolingSafety.Freshness ? "Опрос оборудования превысил допустимое время." : null;
                        telemetry = new Telemetry(Reading.MergePoll(readings.ToArray(), telemetry.Readings,
                            available.Select(device => (device.Identifier.ToString(), device.Name)).ToHashSet(),
                            DateTimeOffset.UtcNow, CoolingSafety.Freshness));
                        if (pollIssue != null) safety.Interrupt(pollIssue, true);
                        // A delayed poll must not revive a test or a stale control session.
                        safety.TickBeforeSample();
                        safety.Observe(adapter.Sample(telemetry));
                        safety.Tick();
                    }
                    catch (Exception error)
                    {
                        Log(error);
                        pollIssue = "Сбой опроса: " + error.Message;
                        safety.Interrupt(pollIssue, true);
                        safety.Tick();
                    }
                    nextPoll = elapsed.Elapsed + TimeSpan.FromMilliseconds(850);
                    Publish();
                }
                wake.WaitOne(100);
            }

            void Publish()
            {
                Interlocked.Exchange(ref testDeadline, safety.Deadline?.Ticks ?? 0);
                latest = new(DateTimeOffset.Now, telemetry, safety.View(), pollIssue);
            }
        }
        catch (Exception error)
        {
            Log(error);
            if (safety?.OwnsOutput == true)
            {
                safety.Close();
                var until = elapsed.Elapsed + TimeSpan.FromSeconds(3);
                while (!safety.ShutdownComplete && elapsed.Elapsed < until)
                {
                    safety.Tick();
                    if (!safety.ShutdownComplete) Thread.Sleep(100);
                }
            }
            var previous = latest;
            if (safety != null) latest = new(DateTimeOffset.Now, previous?.Telemetry ?? Telemetry.Empty, safety.View(), error.Message);
        }
        finally
        {
            Interlocked.Exchange(ref stopping, 1);
            CancelQueued();
            try { computer.Close(); }
            catch (Exception error) { Log(error); }
            finally { adapter?.Dispose(); }
            finished.TrySetResult(safety?.View());
        }
    }

    void CancelQueued()
    {
        while (commands.TryDequeue(out var item)) item.Result.TrySetResult("Команда отменена.");
    }

    static IEnumerable<IHardware> Walk(IHardware hardware)
    {
        yield return hardware;
        foreach (var child in hardware.SubHardware)
            foreach (var item in Walk(child)) yield return item;
    }

    public static void Log(Exception error) => LogEvent(error.ToString());

    static void LogEvent(string message)
    {
        try
        {
            Directory.CreateDirectory(LocalCalibration.Folder);
            string file = Path.Combine(LocalCalibration.Folder, "cooling.log");
            if (new FileInfo(file).Exists && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".old", true);
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException error) { Trace.TraceError(error.Message); }
        catch (UnauthorizedAccessException error) { Trace.TraceError(error.Message); }
    }
}
