using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AirDesk;

internal sealed class DisplayMetric : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Value { get; private set; } = "—";
    public string Detail { get; private set; } = "Ожидание показаний";

    public void Set(string value, string detail)
    {
        if (Value != value) { Value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        if (Detail != detail) { Detail = detail; PropertyChanged?.Invoke(this, new(nameof(Detail))); }
    }
}

internal sealed class DashboardViewModel
{
    readonly Monitor executor;
    readonly ThresholdEvaluator thresholds = new();
    readonly ConcurrentQueue<string> alerts = new();
    volatile ThresholdRule[] rules = [];
    public HistoryStore History { get; }
    public ICommand ReleaseCommand { get; }
    public Snapshot? Latest => executor.Latest;
    public bool Stalled => executor.Stalled;
    public DisplayMetric Cpu { get; } = new();
    public DisplayMetric Gpu { get; } = new();
    public DisplayMetric Memory { get; } = new();
    public DisplayMetric Storage { get; } = new();

    public DashboardViewModel(bool monitorOnly, string? historyFolder = null)
    {
        executor = new Monitor(monitorOnly);
        ReleaseCommand = new ReleaseRequest(executor.Release);
        History = new HistoryStore(historyFolder);
        executor.Sampled = (telemetry, now) =>
        {
            History.Offer(telemetry, now);
            foreach (var (rule, reading) in thresholds.Observe(rules, telemetry))
            {
                if (alerts.Count >= 16) alerts.TryDequeue(out _);
                alerts.Enqueue($"{rule.Label}: {reading.Value:0.#} {reading.Unit}");
            }
        };
    }

    public void ConfigureHistory(HistoryRetention retention) => History.Configure(retention);
    public void ConfigureRules(ThresholdRule[] definitions) => rules = definitions;
    public string[] TakeAlerts()
    {
        var batch = new List<string>();
        while (batch.Count < 16 && alerts.TryDequeue(out string? alert)) batch.Add(alert);
        return batch.ToArray();
    }

    public void Start() => executor.Start();
    public void Release() => ReleaseCommand.Execute(null);
    public Task<string> SaveCalibration() => executor.Submit(safety => safety.SaveCalibration());
    public Task<string> ConfirmTopology() => executor.Submit(safety => safety.ConfirmUnchangedTopology());
    public Task<string> AcknowledgeRecovery() => executor.Submit(safety => safety.AcknowledgeRecovery());
    public Task<string> Rename(string name) => executor.Submit(safety => safety.Rename(name));
    public Task<string> Calibrate(Calibration binding) => executor.Submit(safety => safety.BeginCalibration(binding));
    public Task<string> Enable(FanProfile profile) => executor.Submit(safety => safety.Enable(profile));
    public Task<string> Adjust(FanProfile profile) => executor.Submit(safety => safety.Adjust(profile));

    public async Task<CoolingView?> ShutdownAsync()
    {
        try { return await executor.ShutdownAsync().ConfigureAwait(false); }
        finally
        {
            try { await History.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception error) { Monitor.Log(error); }
        }
    }

    sealed class ReleaseRequest(Action release) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => release();
    }
}
