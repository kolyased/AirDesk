using System;
using System.Collections.Generic;
using System.Linq;
using AirDesk;

static class SafetyTests
{
    static readonly ChannelIdentity Id = new("Intel", "X99", "2", "5.11", "Nuvoton", "/control/1", "/fan/1", "CPU", "PawnIO 2.2", "0.9.6", 1);
    static readonly Calibration Profile = new(Id, "Радиатор", "SYS_FAN, PWM hub", SignalMode.Pwm, 65);
    static readonly FanProfile Manual = FanProfile.Default with { Kind = ProfileKind.Manual, Manual = 42 };
    static readonly SensorReference Gpu = new("gpu-temperature", "gpu0", "Card 1");

    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Reject(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { checks++; return; }
            throw new Exception("Expected rejection.");
        }

        var rig = new Rig();
        rig.Safety.Observe(new(null, 35, 1300, 60, false, "unknown board"));
        Check(rig.Safety.View().State == ChannelState.MonitorOnly, "Unknown board is monitor-only");
        Reject(() => rig.Safety.Enable(Manual));
        Reject(() => rig.Safety.BeginCalibration(Profile));
        rig.Observe();
        Check(rig.Safety.View().State == ChannelState.Unverified, "Discovery never permits normal writes");
        Reject(() => rig.Safety.Enable(Manual));
        Check(rig.Output.Writes.Count == 0, "Forbidden requests never reach output");
        rig.Seed();
        Reject(() => rig.Safety.BeginCalibration(Profile with { Signal = SignalMode.Unknown }));
        Reject(() => rig.Safety.BeginCalibration(Profile with { Floor = 59 }));
        rig.Safety.BeginCalibration(Profile);
        Check(rig.Output.Writes.SequenceEqual([65f]), "Calibration starts at a user-confirmed floor above the autonomous output");
        for (int i = 0; i < 3; ++i) rig.Advance(1, rpm: 1370, duty: 65);
        Check(rig.Output.Writes.SequenceEqual([65f, 100]), "Calibration raises to 100% after floor checks");
        for (int i = 0; i < 4; ++i) rig.Advance(1, rpm: 1800, duty: 100);
        rig.Advance(1);
        Check(rig.Output.Restores == 1 && rig.Safety.View().CanSave && !rig.Store.RecoveryPending, "Successful timed test restores and awaits mapping confirmation");
        rig.Safety.SaveCalibration();
        Check(rig.Store.Saved == Profile && rig.Safety.View().State == ChannelState.Verified, "Verified binding persisted");
        rig.Safety.Enable(Manual);
        Check(rig.Output.Writes.Last() == 65 && rig.Safety.View().Requested == 42 && rig.Safety.View().Sent == 65, "Floor and display semantics");
        rig.Safety.Adjust(Manual with { Manual = 80 });
        Check(rig.Output.Writes.Last() == 80, "Manual change applies through same executor");
        rig.Conflict = "FanControl";
        rig.Safety.Tick();
        Check(rig.Output.Restores == 2 && rig.Safety.View().State == ChannelState.Conflict, "Conflict restores on first observation");
        rig.Conflict = null;
        rig.Safety.Tick();
        Check(rig.Safety.View().State == ChannelState.Verified && rig.Output.Writes.Last() == 80, "No automatic reacquisition after conflict");
        rig.Safety.Stop("repeat restore");
        rig.Safety.Tick();
        Check(rig.Output.Restores == 2, "Restore is idempotent");

        rig = new Rig(Profile);
        Reject(() => rig.Safety.Enable(Manual));
        rig.Safety.ConfirmUnchangedTopology();
        rig.Output.BeforeGuard = () => rig.Conflict = "FanControl.Service";
        rig.Safety.Enable(Manual);
        rig.Safety.Tick();
        Check(rig.Output.Writes.Count == 0 && rig.Output.Restores == 1, "Process appearing immediately before write blocks it");

        rig = Rig.Active();
        rig.Output.RestoreFailures = 2;
        rig.Safety.Stop("test of undo");
        rig.Safety.Tick();
        Check(rig.Safety.View().State == ChannelState.Restoring && rig.Store.RecoveryPending, "Pending restore is not reported as success");
        rig.Advance(.25); rig.Advance(.25);
        Check(rig.Output.Restores == 3 && !rig.Store.RecoveryPending, "Bounded restore retries succeed");

        rig = Rig.Active();
        rig.Output.RestoreFailures = 10;
        rig.Safety.Close();
        rig.Safety.Tick(); rig.Advance(.25); rig.Advance(.25);
        Check(rig.Safety.ShutdownComplete && rig.Output.Restores == 3 && rig.Store.RecoveryPending &&
            rig.Safety.View().Reason.Contains("не подтверждён"), "Unconfirmed restore persists across restart; bounded close");
        var restarted = new Rig(Profile, recovery: true);
        Reject(() => restarted.Safety.ConfirmUnchangedTopology());
        restarted.Safety.AcknowledgeRecovery();
        Reject(() => restarted.Safety.Enable(Manual));
        Reject(() => restarted.Safety.ConfirmUnchangedTopology());

        rig = Rig.Active();
        rig.Observe(cpu: null);
        rig.Safety.Tick();
        Check(rig.Output.Writes.Last() == 100 && rig.Output.Restores == 1, "Lost CPU requests full speed, then restores");

        rig = Rig.Active();
        rig.Output.ReadbackMatches = false;
        Reject(() => rig.Safety.Adjust(Manual with { Manual = 99 }));
        rig.Safety.Tick();
        Check(rig.Output.Restores == 1 && rig.Safety.View().State == ChannelState.Error, "Silent backend failure is detected");

        rig = new Rig(); rig.Seed(); rig.Safety.BeginCalibration(Profile);
        rig.Advance(9);
        Check(rig.Output.Restores == 1 && !rig.Safety.View().CanSave, "Timeout sends restore without a further sensor update");
        rig = new Rig(); rig.Seed(); rig.Safety.BeginCalibration(Profile);
        for (int i = 0; i < 3; ++i) rig.Advance(1, rpm: 650, duty: 65);
        Check(rig.Output.Writes.SequenceEqual([65f]) && rig.Output.Restores == 1 && !rig.Safety.View().CanSave, "Unsafe floor aborts and restores");
        Check(!CoolingSafety.HasResponse([1000, 1100, 950], [1210, 1065, 1120]), "Noisy distinction is inconclusive");
        Check(!CoolingSafety.HasResponse([1000, 1000, 1000], [1500, 1500]), "Two samples are insufficient");

        rig = Rig.Active();
        rig.Observe(identity: Id with { Firmware = "6.0" });
        rig.Safety.Tick();
        Check(rig.Output.Writes.Count == 1 && rig.Output.Restores == 1, "Changed identity revokes binding without speed command");

        rig = Rig.Active();
        rig.Safety.Adjust(FanProfile.Default with { Secondary = Gpu });
        rig.Observe(gpu: null, duty: rig.Output.Writes.Last());
        rig.Safety.Tick();
        Check(rig.Output.Writes.Last() == 100 && rig.Output.Restores == 1, "Missing one source of a maximum fails safe");

        var points = Curve.DefaultPoints(ProfileKind.Balanced);
        Check(Curve.Interpolate(points, 50) == 52.5f && !Curve.ValidPoints([new(45, 60), new(65, 45)]), "Interpolation and monotonic curve validation");
        var response = new CurveResponse();
        Check(response.Target(FanProfile.Default, 70, 40, 40, TimeSpan.Zero) == 70, "Curve never drops below floor");
        Check(response.Target(FanProfile.Default, 70, 85, 40, TimeSpan.FromSeconds(.1)) == 100, "CPU emergency bypasses slew");
        Check(response.Target(FanProfile.Default, 70, 70, 39, TimeSpan.FromSeconds(1)) == 100, "Brief decrease does not reduce speed");
        var first = new Reading("RTX", "/gpu-nvidia/1", "GpuNvidia", "GPU Core", "Temperature", "°C", 76, "/gpu-nvidia/1/temperature/0", ReadingQuality.Fresh, DateTimeOffset.UtcNow);
        var second = first with { DeviceId = "/gpu-nvidia/0", Id = "/gpu-nvidia/0/temperature/0", Device = "GTX", Value = 47 };
        var catalog = new Telemetry([first, second]);
        Check(catalog.Gpus[0].Name == "GTX" && catalog.Gpus[1].Temperature == first, "GPUs are indexed by identity");
        var ambiguous = new Telemetry([first, second with { Device = first.Device }]);
        Check(ambiguous.CurveSources.Length == 0, "Ambiguous adapter identity cannot drive the curve");
        return checks;
    }

    sealed class Rig
    {
        TimeSpan time;
        public string? Conflict;
        public Output Output { get; } = new();
        public Store Store { get; }
        public CoolingSafety Safety { get; }
        public Rig(Calibration? saved = null, bool recovery = false)
        {
            Store = new() { Saved = saved, RecoveryPending = recovery };
            Safety = new(Output, Store, () => Conflict, () => time, _ => { }, () => false);
            if (saved != null) Seed();
        }
        public static Rig Active()
        {
            var rig = new Rig(Profile);
            rig.Safety.ConfirmUnchangedTopology(); rig.Safety.Enable(Manual);
            return rig;
        }
        public void Observe(float? cpu = 35, float rpm = 1350, float duty = 60, ChannelIdentity? identity = null, float? gpu = 55)
            => Safety.Observe(new(identity ?? Id, cpu, rpm, duty, true, "", [new(Gpu, "GPU", gpu)]));
        public void Seed() { for (int i = 0; i < 4; i++) { Observe(); time += TimeSpan.FromMilliseconds(250); } }
        public void Advance(double seconds, float? rpm = null, float duty = 60)
        {
            time += TimeSpan.FromSeconds(seconds);
            if (rpm.HasValue) Observe(rpm: rpm.Value, duty: duty);
            Safety.Tick();
        }
    }

    sealed class Output : ICoolingOutput
    {
        public List<float> Writes { get; } = new();
        public int Restores, RestoreFailures;
        public bool ReadbackMatches = true;
        public Action? BeforeGuard;
        public void Acquire() { }
        public void ReleaseLease() { }
        public float? Write(float percent, Action guard) { BeforeGuard?.Invoke(); guard(); Writes.Add(percent); return ReadbackMatches ? percent : null; }
        public RestoreResult Restore() => ++Restores <= RestoreFailures ? new(false, false, "fault") : new(true, true, "confirmed");
    }

    sealed class Store : ICalibrationStore
    {
        public Calibration? Saved { get; set; }
        public bool RecoveryPending { get; set; }
        public void Save(Calibration value) => Saved = value;
        public void ClearBinding() => Saved = null;
        public void MarkCapture(ChannelIdentity identity) => RecoveryPending = true;
        public void ClearCapture() => RecoveryPending = false;
    }
}
