using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

public record Snapshot(DateTimeOffset Time, Telemetry Telemetry, CoolingView Cooling, string? PollIssue, PollDiagnostics Polling);

public sealed class Monitor
{
    readonly bool monitorOnly;
    readonly Func<ISensorProvider>? sensorFactory;
    readonly object commandGate = new();
    readonly Queue<(Action<CoolingSafety> Action, TaskCompletionSource<string> Result)> commands = new();
    readonly AutoResetEvent wake = new(false);
    readonly TaskCompletionSource<CoolingView?> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    long lastProgress, testDeadline;
    int stopping, releaseRequested, watchdogReported;
    int started, powerChanged, suspended;
    volatile Snapshot? latest;
    public Snapshot? Latest => latest;
    public Monitor(bool monitorOnly = false, Func<ISensorProvider>? sensorFactory = null)
    {
        this.monitorOnly = monitorOnly;
        this.sensorFactory = sensorFactory;
    }
    internal Action<Telemetry, DateTimeOffset>? Sampled { get; set; }
    public bool Stalled => Volatile.Read(ref stopping) == 0 && Volatile.Read(ref suspended) == 0 && elapsed.ElapsedTicks - Interlocked.Read(ref lastProgress) >=
        (long)(Stopwatch.Frequency * CoolingSafety.Freshness.TotalSeconds);

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
            new Thread(Run) { Name = "AirDesk hardware executor", IsBackground = true }.Start();
    }

    public Task<string> Submit(Action<CoolingSafety> action)
    {
        lock (commandGate)
        {
            if (Volatile.Read(ref stopping) != 0 || Stalled)
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
        Interlocked.Exchange(ref releaseRequested, 1);
        wake.Set();
    }

    public void PowerChanged(bool isSuspending)
    {
        lock (commandGate)
        {
            Volatile.Write(ref powerChanged, 1);
            Volatile.Write(ref suspended, isSuspending ? 1 : 0);
            CancelQueued();
            wake.Set();
        }
    }

    bool PowerTransitionPending() => Volatile.Read(ref suspended) != 0 || Volatile.Read(ref powerChanged) != 0;

    public async Task<CoolingView?> ShutdownAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        wake.Set();
        return await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    void Run()
    {
        CoolingSafety? safety = null;
        WindowsCooling? adapter = null;
        ISensorProvider? provider = null;
        Telemetry telemetry = Telemetry.Empty;
        PollDiagnostics polling = PollDiagnostics.Empty;
        var interval = TimeSpan.FromMilliseconds(850);
        string? pollIssue = null;
        bool StopPending() => Volatile.Read(ref stopping) != 0 || Volatile.Read(ref releaseRequested) != 0 || PowerTransitionPending();
        try
        {
            adapter = new WindowsCooling();
            provider = sensorFactory?.Invoke() ?? new LibreSensorProvider(adapter);
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
            provider.Open();
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
                    catch (Exception error) { Log(error); command.Result.TrySetResult(error.Message); }
                    Publish();
                }

                if (!StopPending() && elapsed.Elapsed >= nextPoll && safety.View().State != ChannelState.Restoring)
                {
                    TimeSpan started = elapsed.Elapsed;
                    try
                    {
                        bool discover = adapter.RetryDiscovery && started >= nextDiscovery && !safety.OwnsOutput;
                        if (discover) nextDiscovery = started + TimeSpan.FromSeconds(5);
                        SensorPoll samples = provider.Poll(started, discover);
                        if (PowerTransitionPending()) continue; // This poll may have started before suspend.
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
                            if (safety.OwnsOutput) safety.Interrupt(pollIssue!, true);
                        }
                        else
                        {
                            telemetry = samples.Telemetry;
                            safety.Observe(monitorOnly ? new FanSample(null, null, null, null, false,
                                "В этом сеансе доступен только мониторинг.") : adapter.Sample(telemetry));
                        }
                        safety.Tick();
                        if (!tooSlow)
                        {
                            try { Sampled?.Invoke(telemetry, DateTimeOffset.UtcNow); }
                            catch (Exception error) { Log(error); }
                        }
                    }
                    catch (Exception error)
                    {
                        Log(error);
                        pollIssue = "Сбой опроса: " + error.Message;
                        if (safety.OwnsOutput) safety.Interrupt(pollIssue, true);
                        safety.Tick();
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
            if (safety != null) latest = new(DateTimeOffset.Now, previous?.Telemetry ?? Telemetry.Empty, safety.View(), error.Message, polling);
        }
        finally
        {
            lock (commandGate)
            {
                Interlocked.Exchange(ref stopping, 1);
                CancelQueued();
            }
            try { provider?.Dispose(); }
            catch (Exception error) { Log(error); }
            try { adapter?.Dispose(); }
            catch (Exception error) { Log(error); }
            finished.TrySetResult(safety?.View());
        }
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
    static void LogEvent(string message) => DiagnosticLog.Write(message);
}
