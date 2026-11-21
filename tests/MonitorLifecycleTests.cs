using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AirDesk;
using Xunit;
using Executor = AirDesk.Monitor;

public sealed class MonitorLifecycleTests
{
    static readonly ChannelIdentity Channel = new("Vendor", "Board", "1", "1", "chip", "control", "fan", "cpu", "driver", "library", 1);

    [Fact]
    public async Task ClosingBeforeStartCompletesWithoutOpeningHardware()
    {
        var hardware = new Hardware();
        var executor = Create(hardware);
        Assert.NotEmpty(await executor.Submit(_ => throw new InvalidOperationException()).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Null(await executor.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        executor.Release();
        executor.PowerChanged(true);
        executor.Start();
        Assert.Null(await executor.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, hardware.Opens);
    }

    [Fact]
    public async Task StartupFailureIsVisibleAndDisposesTheAdapterEvenIfLoggingFails()
    {
        var hardware = new Hardware { Opening = () => throw new IOException("opening fixture") };
        var messages = new ConcurrentQueue<string>();
        var executor = Create(hardware, message => { messages.Enqueue(message); throw new IOException("logger fixture"); });
        executor.Start();

        await WaitFor(() => executor.Failure != null);
        await executor.ShutdownAsync();
        executor.Start();
        Assert.Equal(1, hardware.Opens);
        Assert.Equal(1, hardware.Disposals);
        Assert.False(executor.Stalled);
        Assert.Contains(messages, message => message.Contains("opening fixture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedPollClearsReadingsAndRecoversAfterAFailingSampleCallback()
    {
        var hardware = new Hardware { Polling = number => number == 2 ? throw new IOException("poll fixture") : Readings() };
        var messages = new ConcurrentQueue<string>();
        var executor = Create(hardware, messages.Enqueue);
        int samples = 0;
        executor.Sampled = (_, _) =>
        {
            if (Interlocked.Increment(ref samples) == 1) throw new InvalidOperationException("sample fixture");
        };
        executor.Start();
        try
        {
            await WaitFor(() => executor.Latest?.PollIssue?.Contains("poll fixture", StringComparison.Ordinal) == true);
            Assert.Empty(executor.Latest!.Telemetry.Readings);
            Assert.False(executor.Latest.Cooling.CanEnable);
            await WaitFor(() => Volatile.Read(ref samples) >= 2 && executor.Latest is { PollIssue: null, Telemetry.Readings.Length: > 0 });
            Assert.Null(executor.Failure);
            Assert.Contains(messages, message => message.Contains("sample fixture", StringComparison.Ordinal));
        }
        finally { await executor.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownOrSuspendCancelsQueuedCommandsDuringAPoll(bool suspend)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var hardware = new Hardware { Polling = number =>
        {
            if (number == 1) { entered.TrySetResult(); if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            return Readings();
        } };
        var executor = Create(hardware);
        executor.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            int executed = 0;
            var pending = executor.Submit(_ => Interlocked.Increment(ref executed));
            Task<CoolingView?>? shutdown = null;
            if (suspend) executor.PowerChanged(true);
            else shutdown = executor.ShutdownAsync();
            Assert.Equal("Команда отменена.", await pending.WaitAsync(TimeSpan.FromSeconds(1)));
            resume.Set();
            if (suspend)
            {
                await WaitFor(() => executor.Latest?.PollIssue?.Contains("пробуждения", StringComparison.Ordinal) == true);
                Assert.Empty(executor.Latest!.Telemetry.Readings);
                executor.PowerChanged(false);
                await WaitFor(() => executor.Latest is { PollIssue: null, Telemetry.Readings.Length: > 0 });
            }
            else await shutdown!;
            Assert.Equal(0, executed);
        }
        finally { resume.Set(); await executor.ShutdownAsync(); }
        Assert.Equal(1, hardware.Disposals);
    }

    [Fact]
    public async Task PollFailureDuringControlRestoresAndRevokesAdmission()
    {
        var hardware = new Hardware();
        var store = new Store { Saved = new(Channel, "Hub", "PWM", SignalMode.Pwm, 45) { RangeVerified = true } };
        var executor = Create(hardware, store: store, monitorOnly: false);
        executor.Start();
        try
        {
            await WaitFor(() => executor.Latest?.Cooling.CanConfirm == true);
            Assert.Empty(await executor.Submit(cooling => cooling.ConfirmUnchangedTopology()));
            Assert.Empty(await executor.Submit(cooling => cooling.Enable(FanProfile.Default with { Kind = ProfileKind.Manual, Manual = 60 })));
            hardware.Polling = _ => throw new IOException("poll while controlling");
            await WaitFor(() => Volatile.Read(ref hardware.Restores) == 1 && executor.Latest?.PollIssue != null);
            Assert.Equal(new[] { 60f, 100f }, hardware.Writes.ToArray());
            Assert.False(store.RecoveryPending);
            Assert.False(executor.Latest!.Cooling.CanEnable);
            Assert.NotEmpty(await executor.Submit(cooling => cooling.Enable(FanProfile.Default)));
        }
        finally { await executor.ShutdownAsync(); }
        Assert.Equal(1, hardware.Restores);
    }

    [Fact]
    public async Task SuspendDuringHardwareSamplingDoesNotOfferTheDiscardedReading()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var hardware = new Hardware { Sampling = () =>
        {
            entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        } };
        var executor = Create(hardware, monitorOnly: false);
        int samples = 0;
        executor.Sampled = (_, _) => Interlocked.Increment(ref samples);
        executor.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            executor.PowerChanged(true);
            resume.Set();
            await WaitFor(() => executor.Latest?.PollIssue?.Contains("пробуждения", StringComparison.Ordinal) == true);
            Assert.Empty(executor.Latest!.Telemetry.Readings);
            Assert.Equal(0, Volatile.Read(ref samples));
            executor.PowerChanged(false);
            await WaitFor(() => Volatile.Read(ref samples) > 0);
        }
        finally { resume.Set(); await executor.ShutdownAsync(); }
    }

    [Fact]
    public async Task SustainedConflictScannerFailureStillRestoresDuringShutdown()
    {
        var hardware = new Hardware();
        var store = new Store { Saved = new(Channel, "Hub", "PWM", SignalMode.Pwm, 45) { RangeVerified = true } };
        int broken = 0;
        var executor = Create(hardware, store: store, monitorOnly: false, scan: () =>
            Volatile.Read(ref broken) == 0 ? null : throw new IOException("conflict scanner fixture"));
        executor.Start();
        try
        {
            await WaitFor(() => executor.Latest?.Cooling.CanConfirm == true);
            Assert.Empty(await executor.Submit(cooling => cooling.ConfirmUnchangedTopology()));
            Assert.Empty(await executor.Submit(cooling => cooling.Enable(FanProfile.Default with { Kind = ProfileKind.Manual, Manual = 60 })));
            Volatile.Write(ref broken, 1);
            await WaitFor(() => executor.Failure != null);
        }
        finally { await executor.ShutdownAsync(); }
        Assert.Equal(1, hardware.Restores);
        Assert.Equal(new[] { 60f }, hardware.Writes.ToArray());
        Assert.False(store.RecoveryPending);
    }

    static Executor Create(Hardware hardware, Action<string>? log = null, Store? store = null, bool monitorOnly = true,
        Func<string?>? scan = null) =>
        new(() => hardware, () => store ?? new Store(), scan ?? (() => null), monitorOnly, log ?? (_ => { }));

    static SensorPoll Readings() => new(new([new("CPU", "/cpu", "Cpu", "CPU Total", "Load", "%", 10,
        "/cpu/load/0", ReadingQuality.Fresh, DateTimeOffset.UtcNow)]), 1, 0, 0);

    static async Task WaitFor(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while (!condition()) await Task.Delay(20, limit.Token);
    }

    sealed class Hardware : IMonitorHardware
    {
        public Action? Opening { get; init; }
        public Action? Sampling { get; init; }
        public volatile Func<int, SensorPoll>? Polling;
        int polls;
        volatile float duty = 55;
        public int Opens, Disposals, Restores;
        public ConcurrentQueue<float> Writes { get; } = new();
        public bool RetryDiscovery => false;
        public void Open() { Opens++; Opening?.Invoke(); }
        public SensorPoll Poll(TimeSpan elapsed, bool discover)
        {
            int number = ++polls;
            return Polling?.Invoke(number) ?? Readings();
        }
        public FanSample Sample(Telemetry telemetry)
        {
            Sampling?.Invoke();
            return new(Channel, 40, 1200, duty, true, "");
        }
        public void Acquire() { }
        public void AcquireRecoveryLease() { }
        public float? Write(float percent, Action<float?> guard) { guard(duty); duty = percent; Writes.Enqueue(percent); return percent; }
        public RestoreResult Restore() { duty = 55; Interlocked.Increment(ref Restores); return new(true, true, "confirmed"); }
        public void ReleaseLease() { }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    sealed class Store : ICalibrationStore
    {
        public Calibration? Saved { get; set; }
        public bool RecoveryPending { get; private set; }
        public void Save(Calibration value) => Saved = value;
        public void ClearBinding() => Saved = null;
        public void MarkCapture(ChannelIdentity identity) => RecoveryPending = true;
        public void ClearCapture() => RecoveryPending = false;
    }
}
