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
        welcome.Visibility = Visibility.Collapsed;
        foreach (var (name, appearance, width, height, dpi, columns) in new[]
        {
            ("dark-100", Appearance.Dark, 1040d, 760d, 96, 4),
            ("light-125", Appearance.Light, 950d, 620d, 120, 4),
            ("dark-150", Appearance.Dark, 690d, 530d, 144, 2),
            ("light-200", Appearance.Light, 430d, 410d, 192, 1),
            ("compact-100", Appearance.Dark, 360d, 415d, 96, 1),
            ("wide-150", Appearance.Light, 1280d, 720d, 144, 4)
        })
        {
            AppTheme.Apply(appearance);
            Navigate(OverviewTab);
            Width = width;
            Height = height;
            UpdateLayout();
            if (overviewCards.Columns != columns)
                throw new InvalidOperationException($"{name}: expected {columns} overview columns, got {overviewCards.Columns}.");
            SaveImage(this, name, dpi);
            results.Add(new { name, dpi, WidthDip = ActualWidth, HeightDip = ActualHeight, OverviewDip = overviewCards.ActualWidth, Columns = overviewCards.Columns });
        }
        AppTheme.Apply(Appearance.Dark);
        Width = 1040;
        Height = 760;
        VerifyLiveGraphs();
        Navigate(CoolingTab);
        SaveImage(this, "cooling", 96);
        await VerifyFailureMessagesAsync();
        var editor = BuildCurveEditor(false, out var readPoints);
        editor.Show();
        try
        {
            if (!Curve.ValidPoints(readPoints())) throw new InvalidOperationException("A preset curve cannot be previewed in monitor-only mode.");
            SaveImage(editor, "curve-preview", 144);
        }
        finally { editor.Close(); }
        Navigate(SettingsTab);
        SaveImage(this, "settings", 96);
        Navigate(HistoryTab);
        SaveImage(this, "history", 96);
        if (viewModel.History.Retention != HistoryRetention.Off && visibleHistory.Length == 0)
            throw new InvalidOperationException("Recording was enabled, but the history view has no readings.");
        VerifyGpuSelection();
        SaveImage(this, "devices", 125);
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
            if (!fpsStatus.Text.Contains("ui-smoke-frames.csv", StringComparison.Ordinal) ||
                !fpsStatus.Text.Contains("PID 42", StringComparison.Ordinal))
                throw new InvalidOperationException("FPS settings did not identify the connected CSV and PID.");
            if (overlay?.Metrics.Contains("FPS —", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("The OSD did not mark FPS as unavailable before new frames arrived.");
            for (int i = 0; i < 6 && frames.Current == null; i++)
            {
                await Task.Delay(500);
                File.AppendAllText(frameFixture, "42,A,20\n42,A,20\n");
            }
            if (frames.Current is not { FramesPerSecond: 50 } || overlay is not { IsVisible: true, ShowActivated: false, PassiveStyleApplied: true })
                throw new InvalidOperationException("OSD did not display the isolated frame capture.");
            RenderFrames(force: true);
            if (!overlay.Metrics.Contains("FPS 50", StringComparison.Ordinal))
                throw new InvalidOperationException("OSD did not render the measured rate.");
            if (!overlay.Metrics.Contains('\n'))
                throw new InvalidOperationException("The OSD did not group metrics across separate lines.");
            SaveImage(overlay, "osd", 96);
            Navigate(SettingsTab);
            UpdateLayout();
            frameSettingsPanel?.BringIntoView();
            SaveImage(this, "fps-settings", 125);
        }
        finally { DisposeFrames(); File.Delete(frameFixture); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-smoke.json"), JsonSerializer.Serialize(results));
    }

    async Task VerifyFailureMessagesAsync()
    {
        const string refusal = "Начальный уровень калибровки не подтверждён.";
        if (await ExecuteCalibration(() => Task.FromResult(refusal)) ||
            requestError.Visibility != Visibility.Visible || !requestError.Text.Contains(refusal, StringComparison.Ordinal) ||
            requestStatus.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("The calibration refusal was not shown as a visible error.");
        ShowCoolingAction("");

        const string saveError = "Нет доступа к файлу истории.";
        var warning = ExitWarning(new(null, "Драйвер не ответил вовремя.", new(saveError)));
        if (warning == null || !warning.Contains(saveError, StringComparison.Ordinal) ||
            !warning.Contains("Возврат BIOS/EC", StringComparison.Ordinal) || ExitWarning(new(null, null, new())) != null)
            throw new InvalidOperationException("The exit warning did not report both outcomes separately.");
    }

    void VerifyLiveGraphs()
    {
        if (latest is not { } snapshot) throw new InvalidOperationException("No monitoring snapshot.");
        var oldFan = preferences.OverviewFan;
        var oldGpu = (preferences.Gpu, preferences.GpuInstance, preferences.GpuName);
        var oldCharts = preferences.OverviewCharts;
        var end = DateTimeOffset.UtcNow;
        try
        {
            (preferences.Gpu, preferences.GpuInstance, preferences.GpuName) = (null, null, null);
            preferences.OverviewCharts = OverviewChart.Default;
            Navigate(OverviewTab);
            ApplyLiveGraphPreferences();
            UpdateLayout();
            for (int i = 0; i <= 150; i++)
            {
                var time = end.AddSeconds((i - 150) * 2);
                var cpu = new Reading("CPU", "/cpu/0", "Cpu", "CPU Package", "Temperature", "°C", 47 + 8 * MathF.Sin(i / 14f),
                    "/cpu/0/temperature/0", i is >= 63 and <= 69 ? ReadingQuality.Stale : ReadingQuality.Fresh, time);
                var gpu = new Reading("GPU", "/gpu-nvidia/0", "GpuNvidia", "GPU Core", "Temperature", "°C", 62 + 5 * MathF.Cos(i / 12f),
                    "/gpu-nvidia/0/temperature/0", ReadingQuality.Fresh, time);
                var fan = new Reading("Хаб", "/lpc/demo", "SuperIO", "Вентилятор", "Fan", "об/мин", 1180 + 200 * MathF.Sin(i / 20f),
                    "/lpc/demo/fan/0", ReadingQuality.Fresh, time);
                preferences.OverviewFan = fan.Reference;
                RenderLiveGraphs(snapshot with { Time = time, Telemetry = new([cpu, gpu, fan]) }, false);
            }
            if (!fanNow.Text.Contains("об/мин", StringComparison.Ordinal) || !tempNow.Text.Contains("GPU", StringComparison.Ordinal) ||
                graphFan.SelectedItem is not FanOption { Sensor: not null, Missing: false })
                throw new InvalidOperationException("Expected current readings and a selected tachometer on the graphs.");
            SaveImage(this, "live-graphs", 96);
        }
        finally
        {
            (preferences.Gpu, preferences.GpuInstance, preferences.GpuName) = oldGpu;
            preferences.OverviewFan = oldFan;
            preferences.OverviewCharts = oldCharts;
            foreach (var trace in new[] { cpuLoadTrace, cpuTemperatureTrace, gpuTemperatureTrace, fanTrace }) trace.Clear();
            ApplyLiveGraphPreferences();
        }
    }

    void VerifyGpuSelection()
    {
        const string first = @"PCI\VEN_10DE&DEV_2882\4&00001&0&0008";
        const string second = @"PCI\VEN_10DE&DEV_2882\4&00002&0&0008";
        var previous = (displayed, preferences.Gpu, preferences.GpuInstance, preferences.GpuName);
        try
        {
            Navigate(DevicesTab);
            UpdateLayout();
            var initial = new Telemetry([Gpu(0, 42, first), Gpu(1, 65, second)]);
            Show(initial);
            if (deviceChoice.Items.Groups?.Count != 1 || deviceTable.Items.Groups?.Count is not >= 2)
                throw new InvalidOperationException($"Missing groups. Devices: {deviceChoice.Items.Groups?.Count}. Readings: {deviceTable.Items.Groups?.Count}.");
            var options = (DeviceOption[])gpuChoice.ItemsSource;
            if (options.Length != 3 || options[1].Label == options[2].Label)
                throw new InvalidOperationException("The selector did not distinguish identical graphics cards.");
            gpuChoice.SelectedItem = options[2];
            if (preferences.GpuInstance != second)
                throw new InvalidOperationException("The selected graphics card was not saved by device instance.");
            Show(new Telemetry([Gpu(0, 66, second), Gpu(1, 43, first)]));
            if (gpuChoice.SelectedItem is not DeviceOption { Id: "/gpu-nvidia/0", InstanceId: second })
                throw new InvalidOperationException("The selected graphics card changed after adapter reordering.");
            Show(new Telemetry([Gpu(1, 43, first)]));
            if (gpuChoice.SelectedItem is not DeviceOption { Missing: true })
                throw new InvalidOperationException("The selected graphics card was silently replaced after disconnect.");
        }
        finally
        {
            (displayed, preferences.Gpu, preferences.GpuInstance, preferences.GpuName) = previous;
            preferences.Save();
            if (displayed is { } current) RenderTelemetry(current, previousStale);
        }

        void Show(Telemetry data)
        {
            displayed = data;
            RenderTelemetry(data, stale: false);
        }

        static Reading Gpu(int index, float temperature, string instance) => new("Example GPU", $"/gpu-nvidia/{index}",
            "GpuNvidia", "GPU Core", "Temperature", "°C", temperature, $"/gpu-nvidia/{index}/temperature/0", ReadingQuality.Fresh,
            DateTimeOffset.UtcNow) { DeviceInstance = instance };
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
