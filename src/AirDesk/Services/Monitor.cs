using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

public record Snapshot(DateTimeOffset Time, Telemetry Telemetry, CoolingView Cooling, string? PollIssue, PollDiagnostics Polling);

public sealed class Monitor
{
    readonly bool monitorOnly;
    readonly Func<ISensorProvider>? sensorFactory;
    readonly ConcurrentQueue<(Action<CoolingSafety> Action, TaskCompletionSource<string> Result)> commands = new();
    readonly AutoResetEvent wake = new(false);
    readonly TaskCompletionSource<CoolingView?> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    long lastProgress, testDeadline;
    int stopping, releaseRequested, watchdogReported;
    volatile Snapshot? latest;
    public Snapshot? Latest => latest;
    public Monitor(bool monitorOnly = false, Func<ISensorProvider>? sensorFactory = null)
    {
        this.monitorOnly = monitorOnly;
        this.sensorFactory = sensorFactory;
    }
    internal Action<Telemetry, DateTimeOffset>? Sampled { get; set; }
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
        CoolingSafety? safety = null;
        WindowsCooling? adapter = null;
        ISensorProvider? provider = null;
        Telemetry telemetry = Telemetry.Empty;
        PollDiagnostics polling = PollDiagnostics.Empty;
        var interval = TimeSpan.FromMilliseconds(850);
        string? pollIssue = null;
        bool StopPending() => Volatile.Read(ref stopping) != 0 || Volatile.Read(ref releaseRequested) != 0;
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
                    nextTick = elapsed.Elapsed + TimeSpan.FromMilliseconds(safety.OwnsOutput ? 200 : 850);
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
                        bool discover = adapter.RetryDiscovery && started >= nextDiscovery && !safety.OwnsOutput;
                        if (discover) nextDiscovery = started + TimeSpan.FromSeconds(5);
                        SensorPoll samples = provider.Poll(started, discover);
                        var duration = elapsed.Elapsed - started;
                        long missed = polling.Polls == 0 ? 0 : (long)((elapsed.Elapsed - nextPoll).TotalMilliseconds / interval.TotalMilliseconds);
                        polling = polling.Observe(duration, missed, samples);
                        bool tooSlow = duration >= CoolingSafety.Freshness;
                        pollIssue = tooSlow ? "Опрос оборудования превысил допустимое время." :
                            samples.FailedDevices > 0 ? $"Недоступны показания устройств: {samples.FailedDevices}." : null;
                        telemetry = samples.Telemetry;
                        if (tooSlow) safety.Interrupt(pollIssue!, true);
                        // A delayed poll must not revive a test or a stale control session.
                        safety.TickBeforeSample();
                        safety.Observe(monitorOnly ? new FanSample(null, null, null, null, false,
                            "В этом сеансе доступен только мониторинг.") : adapter.Sample(telemetry));
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
                        safety.Interrupt(pollIssue, true);
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
            Interlocked.Exchange(ref stopping, 1);
            CancelQueued();
            try { provider?.Dispose(); }
            catch (Exception error) { Log(error); }
            finally { adapter?.Dispose(); }
            finished.TrySetResult(safety?.View());
        }
    }

    void CancelQueued()
    {
        while (commands.TryDequeue(out var item)) item.Result.TrySetResult("Команда отменена.");
    }

    public static void Log(Exception error) => DiagnosticLog.Write(error.ToString());
    static void LogEvent(string message) => DiagnosticLog.Write(message);
}
