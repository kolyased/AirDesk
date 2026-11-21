using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AirDesk;

internal sealed class MonitoringSession
{
    readonly Monitor executor;
    readonly ThresholdEvaluator thresholds = new();
    readonly ConcurrentQueue<string> alerts = new();
    volatile ThresholdRule[] rules = [];
    public HistoryStore History { get; }
    public Snapshot? Latest => executor.Latest;
    public bool Stalled => executor.Stalled;
    public string? Failure => executor.Failure;

    public MonitoringSession(Monitor executor, string? historyFolder = null)
    {
        this.executor = executor;
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

    public void ConfigureHistory(HistoryRetention retention, IEnumerable<SensorReference>? sensors = null) => History.Configure(retention, sensors);
    public void ConfigureRules(ThresholdRule[] definitions) => rules = definitions;
    public string[] TakeAlerts()
    {
        var batch = new List<string>();
        while (batch.Count < 16 && alerts.TryDequeue(out string? alert)) batch.Add(alert);
        return batch.ToArray();
    }

    public void Start() => executor.Start();
    public void Release() => executor.Release();
    public void PowerChanged(bool isSuspending) => executor.PowerChanged(isSuspending);
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

}
