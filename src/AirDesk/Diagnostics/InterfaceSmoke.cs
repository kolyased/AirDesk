using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace AirDesk;

public sealed partial class Dashboard
{
    async Task CaptureInterfaceAsync()
    {
        var results = new System.Collections.Generic.List<object>();
        if (!IsVisible) throw new InvalidOperationException("Main window did not start.");
        Close();
        if (IsVisible || shellDisposed) throw new InvalidOperationException("Close did not hide the window in the notification area.");
        ActivateFromSecondLaunch();
        if (!IsVisible) throw new InvalidOperationException("Hidden window could not be reopened.");
        foreach (var (name, appearance, width, dpi, columns) in new[]
        {
            ("dark-100", Appearance.Dark, 1040d, 96, 4),
            ("light-125", Appearance.Light, 950d, 120, 4),
            ("dark-150", Appearance.Dark, 690d, 144, 2),
            ("light-200", Appearance.Light, 430d, 192, 1)
        })
        {
            AppTheme.Apply(appearance);
            Navigate(OverviewTab);
            Width = width;
            UpdateLayout();
            if (overviewCards.Columns != columns)
                throw new InvalidOperationException($"{name}: expected {columns} overview columns, got {overviewCards.Columns}.");
            SaveImage(this, name, dpi);
            results.Add(new { name, dpi, OverviewDip = overviewCards.ActualWidth, Columns = overviewCards.Columns });
        }
        AppTheme.Apply(Appearance.Dark);
        Width = 1040;
        Navigate(CoolingTab);
        SaveImage(this, "cooling", 96);
        Navigate(SettingsTab);
        SaveImage(this, "settings", 96);
        Navigate(HistoryTab);
        SaveImage(this, "history", 96);
        if (viewModel.History.Retention != HistoryRetention.Off && visibleHistory.Length == 0)
            throw new InvalidOperationException("Recording was enabled, but the history view has no readings.");
        await VerifyPowerTransitionAsync();
        var work = Forms.Screen.PrimaryScreen!.WorkingArea;
        flyoutAnchor = new System.Drawing.Point(work.Right - 12, work.Bottom);
        ToggleFlyout();
        SaveImage(flyout!, "tray", 96);
        flyout?.Hide();
        string frameFixture = Path.Combine(AppContext.BaseDirectory, "ui-smoke-frames.csv");
        File.WriteAllText(frameFixture, "ProcessID,SwapChainAddress,msBetweenPresents\n");
        frames = new FrameCapture(frameFixture, 42);
        preferences.Overlay = new(true, OverlayCorner.BottomRight, 1, .85, OverlayMetric.Frames | OverlayMetric.FrameTime | OverlayMetric.Cpu);
        ApplyOverlay();
        try
        {
            for (int i = 0; i < 6 && frames.Current == null; i++)
            {
                await Task.Delay(500);
                File.AppendAllText(frameFixture, "42,A,20\n42,A,20\n");
            }
            if (frames.Current is not { FramesPerSecond: 50 } || overlay is not { IsVisible: true, ShowActivated: false })
                throw new InvalidOperationException("OSD did not display the isolated frame capture.");
            RenderFrames(force: true);
            if (overlayText?.Text.Contains("FPS 50", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("OSD did not render the measured rate.");
            SaveImage(overlay, "osd", 96);
        }
        finally { DisposeFrames(); File.Delete(frameFixture); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-smoke.json"), JsonSerializer.Serialize(results));
    }

    async Task VerifyPowerTransitionAsync()
    {
        viewModel.PowerChanged(true);
        try
        {
            await WaitFor(() => viewModel.Latest is { Cooling.LastPollUtc: null, Telemetry.Readings.Length: 0 });
            long lastPoll = viewModel.Latest!.Polling.Polls;
            await Task.Delay(1100);
            if (viewModel.Latest!.Polling.Polls != lastPoll)
                throw new InvalidOperationException("Hardware polling continued during the simulated suspend.");
        }
        finally { viewModel.PowerChanged(false); }
        await WaitFor(() => viewModel.Latest is { Cooling.LastPollUtc: not null, Telemetry.Readings.Length: > 0 });

        static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 30; i++)
            {
                if (condition()) return;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Power transition was not reflected in the monitor snapshot.");
        }
    }

    static void SaveImage(Window window, string name, int dpi)
    {
        window.UpdateLayout();
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name + ".png"));
        encoder.Save(file);
    }
}
