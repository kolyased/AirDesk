using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AirDesk;

public sealed partial class Dashboard
{
    DispatcherTimer? soakTimer;
    Stopwatch? soakTime;
    TimeSpan previousCpu, previousElapsed;
    int soakErrors;
    bool lastSoakError;
    string SoakPath => Path.Combine(AppContext.BaseDirectory, "soak.jsonl");

    void StartSoak()
    {
        soakTime = Stopwatch.StartNew();
        soakTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        SystemEvents.PowerModeChanged += SoakPowerChange;
        WriteSoak("started");
        soakTimer.Tick += async (_, _) =>
        {
            bool hadError = viewModel.Stalled || latest?.PollIssue != null;
            if (hadError && !lastSoakError) soakErrors++;
            lastSoakError = hadError;
            WriteSoak("sample");
            if (soakTime.Elapsed >= TimeSpan.FromHours(2)) await ExitAsync();
        };
        soakTimer.Start();
    }

    void SoakPowerChange(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode is PowerModes.Suspend or PowerModes.Resume)
            Dispatcher.BeginInvoke(() => WriteSoak(args.Mode.ToString()));
    }

    void StopSoak()
    {
        if (soakTimer == null) return;
        soakTimer.Stop();
        SystemEvents.PowerModeChanged -= SoakPowerChange;
        WriteSoak("finished");
    }

    void WriteSoak(string kind)
    {
        if (soakTime == null) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var elapsed = soakTime.Elapsed;
            var cpu = process.TotalProcessorTime;
            double seconds = (elapsed - previousElapsed).TotalSeconds;
            var row = new
            {
                At = DateTimeOffset.UtcNow, Event = kind, ElapsedMinutes = Math.Round(elapsed.TotalMinutes, 2),
                CpuPercent = seconds > .1 ? Math.Round((cpu - previousCpu).TotalSeconds * 100 / seconds / Environment.ProcessorCount, 2) : (double?)null,
                WorkingMiB = Math.Round(process.WorkingSet64 / 1048576d, 1),
                PrivateMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1),
                Errors = soakErrors, Stalled = viewModel.Stalled, Polling = latest?.Polling
            };
            File.AppendAllText(SoakPath, JsonSerializer.Serialize(row) + Environment.NewLine);
            previousCpu = cpu;
            previousElapsed = elapsed;
        }
        catch (Exception error) { Monitor.Log(error); }
    }
}
