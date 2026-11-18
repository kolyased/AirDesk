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
        using var legacy = new Mutex(true, @"Local\AirDesk-0.1", out bool first);
        using var single = new Mutex(true, @"Local\AirDesk", out bool unique);
        if (!first || !unique)
        {
            if (!args.Contains("--autostart")) ActivateExisting();
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
        var window = new Dashboard(autostart || benchmark, benchmark, args.Contains("--record-history"), args.Contains("--monitor-only"));
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
        if (smoke || !args.Contains("--tray") && (benchmark || !window.StartMinimized)) window.Show();
        window.Start(smoke, args.Contains("--ui-smoke"), benchmark);
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
