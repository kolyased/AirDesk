using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AirDesk;

public static class Program
{
    const string ActivationEvent = @"Local\AirDesk.Activate";
    const string SetupMutex = @"Local\AirDesk.Setup";
    [STAThread]
    public static void Main(string[] args)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        bool soak = args.Contains("--soak");
        using var legacy = new Mutex(true, @"Local\AirDesk-0.1", out bool first);
        using var single = new Mutex(true, @"Local\AirDesk", out bool unique);
        if (!first || !unique)
        {
            if (!soak && !args.Contains("--autostart")) ActivateExisting();
            return;
        }

        bool autostart = args.Contains("--autostart");
        if (SetupRunning())
        {
            if (!autostart) MessageBox.Show("Завершите установку или удаление AirDesk, затем запустите программу.");
            return;
        }
        bool smoke = args.Contains("--smoke") || args.Contains("--ui-smoke");
        bool benchmark = args.Contains("--benchmark");
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEvent);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        bool fixture = args.Contains("--ui-smoke");
        bool isolated = benchmark || soak || fixture;
        bool autostartSession = autostart || isolated;
        var session = new MonitoringSession(WindowsMonitoring.Create(autostartSession || args.Contains("--monitor-only")),
            isolated ? System.IO.Path.Combine(AppContext.BaseDirectory, "benchmark-history") : null);
        var window = new Dashboard(session, DriverStatus.Read(), autostartSession, isolated, args.Contains("--record-history"));
        var activation = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(window.ActivateFromSecondLaunch);
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        app.MainWindow = window;
        app.DispatcherUnhandledException += async (_, error) =>
        {
            error.Handled = true;
            Monitor.Log(error.Exception);
            await window.ExitAsync("Ошибка интерфейса. AirDesk завершает управление.");
        };
        app.SessionEnding += (_, _) => window.EndSession();
        app.Exit += (_, _) => window.DisposeShell();
        if (!soak && (smoke || !args.Contains("--tray") && (benchmark || !window.StartMinimized))) window.Show();
        window.Start(smoke, fixture, benchmark, soak);
        try { app.Run(); }
        finally { activation.Unregister(null); }
    }

    static bool SetupRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(SetupMutex, out var setup)) return false;
            setup.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    static void ActivateExisting()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ActivationEvent);
            if (existing.Set()) return;
        }
        catch (UnauthorizedAccessException) { } // Windows can reject activation across privilege boundaries.
        catch (WaitHandleCannotBeOpenedException) { }
        MessageBox.Show("AirDesk уже работает. Откройте значок в трее.");
    }
}
