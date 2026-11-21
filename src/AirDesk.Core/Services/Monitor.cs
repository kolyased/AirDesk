using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

public record Snapshot(DateTimeOffset Time, Telemetry Telemetry, CoolingView Cooling, string? PollIssue, PollDiagnostics Polling);

internal sealed class Monitor
{
    readonly bool monitorOnly;
    readonly Func<IMonitorHardware> hardwareFactory;
    readonly Func<ICalibrationStore> calibrationFactory;
    readonly Func<string?> conflictScan;
    readonly Action<string> log;
    readonly object commandGate = new();
    readonly Queue<(Action<CoolingSafety> Action, TaskCompletionSource<string> Result)> commands = new();
    readonly AutoResetEvent wake = new(false);
    readonly TaskCompletionSource<CoolingView?> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    long lastProgress, testDeadline;
    int stopping, releaseRequested, watchdogReported;
    int started, powerChanged, suspended;
    volatile Snapshot? latest;
    volatile string? failure;
    public Snapshot? Latest => latest;
    public string? Failure => failure;

    public Monitor(Func<IMonitorHardware> hardwareFactory, Func<ICalibrationStore> calibrationFactory,
        Func<string?> conflictScan, bool monitorOnly = false, Action<string>? log = null)
    {
        this.hardwareFactory = hardwareFactory;
        this.calibrationFactory = calibrationFactory;
        this.conflictScan = conflictScan;
        this.monitorOnly = monitorOnly;
        this.log = log ?? DiagnosticLog.Write;
    }
    internal Action<Telemetry, DateTimeOffset>? Sampled { get; set; }
    public bool Stalled => Volatile.Read(ref started) != 0 && Volatile.Read(ref stopping) == 0 && Volatile.Read(ref suspended) == 0 && elapsed.ElapsedTicks - Interlocked.Read(ref lastProgress) >=
        (long)(Stopwatch.Frequency * CoolingSafety.Freshness.TotalSeconds);

    public void Start()
    {
        lock (commandGate)
        {
            if (started != 0 || stopping != 0) return;
            Interlocked.Exchange(ref lastProgress, elapsed.ElapsedTicks);
            Volatile.Write(ref started, 1);
            try { new Thread(Run) { Name = "AirDesk hardware executor", IsBackground = true }.Start(); }
            catch
            {
                StopAcceptingCommands();
                wake.Dispose();
                finished.TrySetResult(null);
                throw;
            }
        }
    }

    public Task<string> Submit(Action<CoolingSafety> action)
    {
        lock (commandGate)
        {
            if (started == 0 || stopping != 0 || Stalled)
                return Task.FromResult("Исполнитель недоступен. Проверьте состояние драйвера.");
            if (PowerTransitionPending() || Volatile.Read(ref releaseRequested) != 0)
                return Task.FromResult("Дождитесь обновления показаний и завершения возврата управления.");
            if (commands.Count >= 8) return Task.FromResult("Исполнитель занят. Дождитесь окончания команды.");
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            commands.Enqueue((action, result));
            wake.Set();
            return result.Task;
        }
    }

    public void Release()
    {
        lock (commandGate)
        {
            if (stopping != 0) return;
            Volatile.Write(ref releaseRequested, 1);
            CancelQueued();
            wake.Set();
        }
    }

    public void PowerChanged(bool isSuspending)
    {
        lock (commandGate)
        {
            if (stopping != 0) return;
            Volatile.Write(ref powerChanged, 1);
            Volatile.Write(ref suspended, isSuspending ? 1 : 0);
            CancelQueued();
            wake.Set();
        }
    }

    bool PowerTransitionPending() => Volatile.Read(ref suspended) != 0 || Volatile.Read(ref powerChanged) != 0;

    public Task<CoolingView?> ShutdownAsync()
    {
        lock (commandGate)
        {
            if (stopping == 0)
            {
                StopAcceptingCommands();
                if (started != 0) wake.Set();
                else
                {
                    wake.Dispose();
                    finished.TrySetResult(null);
                }
            }
        }
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    void Run()
    {
        CoolingSafety? safety = null;
        IMonitorHardware? hardware = null;
        Timer? deadline = null;
        Telemetry telemetry = Telemetry.Empty;
        PollDiagnostics polling = PollDiagnostics.Empty;
        var interval = TimeSpan.FromMilliseconds(850);
        string? pollIssue = null;
        bool StopPending() => Volatile.Read(ref stopping) != 0 || Volatile.Read(ref releaseRequested) != 0 || PowerTransitionPending();
        try
        {
            hardware = hardwareFactory();
            safety = new(hardware, calibrationFactory(), conflictScan, () => elapsed.Elapsed, LogEvent, StopPending);
            deadline = new Timer(_ =>
            {
                long due = Interlocked.Read(ref testDeadline);
                if (due != 0 && elapsed.Elapsed.Ticks >= due) wake.Set();
                if (Stalled && safety.OwnsOutput && Interlocked.Exchange(ref watchdogReported, 1) == 0)
                {
                    Interlocked.Exchange(ref releaseRequested, 1);
                    wake.Set();
                    LogEvent("Аппаратный вызов просрочен. Возврат запрошен, выполнение ожидает освобождения драйвера.");
                }
            }, null, 100, 100);
            hardware.Open();
            TimeSpan nextPoll = TimeSpan.Zero, nextTick = TimeSpan.Zero, nextDiscovery = elapsed.Elapsed + TimeSpan.FromSeconds(5);
            while (!safety.ShutdownComplete)
            {
                Interlocked.Exchange(ref lastProgress, elapsed.ElapsedTicks);
                Interlocked.Exchange(ref watchdogReported, 0);
                if (Volatile.Read(ref stopping) != 0) safety.Close();
                if (Interlocked.Exchange(ref powerChanged, 0) != 0)
                {
                    safety.InvalidateForPowerChange();
                    telemetry = Telemetry.Empty;
                    pollIssue = "Показания будут обновлены после пробуждения Windows.";
                    nextPoll = nextTick = elapsed.Elapsed;
                }
                if (ConsumeReleaseRequest())
                {
                    safety.Stop("пользователь вернул управление BIOS/EC");
                    nextTick = TimeSpan.Zero;
                }
                bool expired = safety.Deadline is { } due && elapsed.Elapsed >= due;
                if (elapsed.Elapsed >= nextTick || expired || Volatile.Read(ref stopping) != 0)
                {
                    safety.Tick();
                    nextTick = elapsed.Elapsed + TimeSpan.FromMilliseconds(safety.OwnsOutput ? 200 : 850);
                }
                Publish();
                if (safety.ShutdownComplete) break;

                if (!StopPending() && TryTakeCommand(out var command))
                {
                    try { command.Action(safety); command.Result.TrySetResult(""); }
                    catch (Exception error) { Report(error); command.Result.TrySetResult(error.Message); }
                    Publish();
                }

                if (!StopPending() && elapsed.Elapsed >= nextPoll && safety.View().State != ChannelState.Restoring)
                {
                    TimeSpan started = elapsed.Elapsed;
                    try
                    {
                        bool discover = hardware.RetryDiscovery && started >= nextDiscovery && !safety.OwnsOutput;
                        if (discover) nextDiscovery = started + TimeSpan.FromSeconds(5);
                        SensorPoll samples = hardware.Poll(started, discover);
                        if (StopPending()) continue; // The request can arrive while the driver is reading.
                        var duration = elapsed.Elapsed - started;
                        long missed = polling.Polls == 0 ? 0 : (long)((elapsed.Elapsed - nextPoll).TotalMilliseconds / interval.TotalMilliseconds);
                        polling = polling.Observe(duration, missed, samples);
                        bool tooSlow = duration >= CoolingSafety.Freshness;
                        pollIssue = tooSlow ? "Опрос оборудования превысил допустимое время." :
                            samples.FailedDevices > 0 ? $"Недоступны показания устройств: {samples.FailedDevices}." : null;
                        safety.TickBeforeSample();
                        if (tooSlow)
                        {
                            telemetry = Telemetry.Empty;
                            safety.InvalidateSample(pollIssue!);
                        }
                        else
                        {
                            var fan = monitorOnly ? new FanSample(null, null, null, null, false,
                                "В этом сеансе доступен только мониторинг.") : hardware.Sample(samples.Telemetry);
                            if (StopPending()) continue;
                            safety.Observe(fan);
                        }
                        safety.Tick();
                        if (StopPending()) continue;
                        telemetry = tooSlow ? Telemetry.Empty : samples.Telemetry;
                        if (!tooSlow)
                        {
                            try { Sampled?.Invoke(telemetry, DateTimeOffset.UtcNow); }
                            catch (Exception error) { Report(error); }
                        }
                    }
                    catch (Exception error)
                    {
                        Report(error);
                        if (StopPending()) continue;
                        pollIssue = "Сбой опроса: " + error.Message;
                        telemetry = Telemetry.Empty;
                        safety.InvalidateSample(pollIssue);
                        safety.RestoreIfDue();
                    }
                    var next = nextPoll + interval;
                    nextPoll = next > elapsed.Elapsed ? next : elapsed.Elapsed + interval;
                    Publish();
                }
                wake.WaitOne(100);
            }

            void Publish()
            {
                Interlocked.Exchange(ref testDeadline, safety.Deadline?.Ticks ?? 0);
                var cooling = safety.View();
                var previous = latest;
                if (previous == null || previous.Telemetry != telemetry || previous.Cooling != cooling ||
                    previous.PollIssue != pollIssue || previous.Polling != polling)
                    latest = new(DateTimeOffset.Now, telemetry, cooling, pollIssue, polling);
            }
        }
        catch (Exception error)
        {
            Report(error);
            lock (commandGate) StopAcceptingCommands();
            failure = "Фоновый опрос остановился. Откройте диагностику и перезапустите AirDesk.";
            try
            {
                if (safety?.OwnsOutput == true)
                {
                    safety.Close();
                    var until = elapsed.Elapsed + TimeSpan.FromSeconds(3);
                    while (!safety.ShutdownComplete && elapsed.Elapsed < until)
                    {
                        safety.RestoreIfDue();
                        if (!safety.ShutdownComplete) Thread.Sleep(100);
                    }
                }
                if (safety != null) latest = new(DateTimeOffset.Now, Telemetry.Empty, safety.View(), error.Message, polling);
            }
            catch (Exception recoveryError) { Report(recoveryError); }
        }
        finally
        {
            lock (commandGate) StopAcceptingCommands();
            try { if (deadline != null) deadline.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { Report(error); }
            try { hardware?.Dispose(); }
            catch (Exception error) { Report(error); }
            wake.Dispose();
            try { finished.TrySetResult(safety?.View()); }
            catch (Exception error)
            {
                Report(error);
                finished.TrySetResult(latest?.Cooling);
            }
        }
    }

    void StopAcceptingCommands()
    {
        Volatile.Write(ref stopping, 1);
        CancelQueued();
    }

    void CancelQueued()
    {
        while (commands.TryDequeue(out var item)) item.Result.TrySetResult("Команда отменена.");
    }

    bool ConsumeReleaseRequest()
    {
        lock (commandGate)
        {
            if (Interlocked.Exchange(ref releaseRequested, 0) == 0) return false;
            CancelQueued();
            return true;
        }
    }

    bool TryTakeCommand(out (Action<CoolingSafety> Action, TaskCompletionSource<string> Result) command)
    {
        lock (commandGate)
        {
            command = default;
            return !PowerTransitionPending() && Volatile.Read(ref releaseRequested) == 0 &&
                Volatile.Read(ref stopping) == 0 && commands.TryDequeue(out command);
        }
    }

    public static void Log(Exception error) => DiagnosticLog.Write(error.ToString());
    void Report(Exception error) => LogEvent(error.ToString());

    void LogEvent(string message)
    {
        try { log(message); }
        catch (Exception error) { Trace.TraceError(error.Message); }
    }
}
