using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AirDesk;

public sealed partial class Dashboard
{
    readonly TextBlock fpsStatus = Text("Источник FPS отключён. Обычные датчики работают независимо.", 12);
    FrameCapture? frames;
    Window? overlay;
    TextBlock? overlayText;
    long updateFramesAt;

    FrameworkElement BuildFrameSettings()
    {
        var body = new StackPanel();
        body.Children.Add(Text("FPS и экранная панель", 19));
        AddWrapped(body, "Для FPS запустите PresentMon отдельно с параметром --v1_metrics. Здесь укажите его CSV и PID процесса.");
        var source = new WrapPanel();
        var pid = new TextBox { Width = 92, MaxLength = 10, Margin = new Thickness(0, 0, 8, 5) };
        NameForAutomation(pid, "PID для измерения FPS");
        source.Children.Add(Text("PID  ", 14));
        source.Children.Add(pid);
        source.Children.Add(Button("Подключить CSV…", () =>
        {
            if (!int.TryParse(pid.Text, out int target) || target <= 0)
            {
                fpsStatus.Text = "Укажите положительный PID из диспетчера задач.";
                return;
            }
            var file = new OpenFileDialog { Filter = "CSV PresentMon|*.csv", CheckFileExists = true };
            if (file.ShowDialog(this) != true) return;
            try
            {
                var reader = new FrameCapture(file.FileName, target);
                frames?.Dispose();
                frames = reader;
                RenderFrames(force: true);
            }
            catch (Exception error) { fpsStatus.Text = error.Message; }
        }));
        source.Children.Add(Button("Отключить FPS", () =>
        {
            frames?.Dispose();
            frames = null;
            fpsStatus.Text = "Источник FPS отключён. Обычные датчики работают независимо.";
            RenderFrames(force: true);
        }));
        body.Children.Add(source);
        fpsStatus.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(fpsStatus);

        var enabled = new CheckBox { Content = "Показывать экранную панель", IsChecked = preferences.Overlay.Enabled };
        enabled.Margin = new Thickness(0, 6, 0, 8);
        body.Children.Add(enabled);
        AddWrapped(body, "Панель располагается на основном мониторе. В эксклюзивном полноэкранном режиме она может быть скрыта.");
        var corner = new ComboBox { ItemsSource = new[] { "Сверху слева", "Сверху справа", "Снизу слева", "Снизу справа" },
            SelectedIndex = (int)preferences.Overlay.Corner, MinWidth = 165, HorizontalAlignment = HorizontalAlignment.Left };
        NameForAutomation(corner, "Положение экранной панели");
        body.Children.Add(corner);
        var scale = AddSlider("Масштаб", .75, 2, .05, preferences.Overlay.Scale);
        var opacity = AddSlider("Непрозрачность", .25, 1, .05, preferences.Overlay.Opacity);
        var metrics = new WrapPanel();
        body.Children.Add(metrics);
        var choices = new[] { (OverlayMetric.Frames, "FPS"), (OverlayMetric.FrameTime, "Время кадра"),
            (OverlayMetric.Cpu, "CPU"), (OverlayMetric.Gpu, "GPU"), (OverlayMetric.Ram, "RAM") };
        foreach (var (metric, label) in choices)
        {
            var choice = new CheckBox { Content = label, IsChecked = preferences.Overlay.Metrics.HasFlag(metric), Margin = new Thickness(0, 4, 13, 5) };
            choice.Checked += (_, _) => Set(preferences.Overlay with { Metrics = preferences.Overlay.Metrics | metric });
            choice.Unchecked += (_, _) => Set(preferences.Overlay with { Metrics = preferences.Overlay.Metrics & ~metric });
            metrics.Children.Add(choice);
        }
        enabled.Checked += (_, _) => Set(preferences.Overlay with { Enabled = true });
        enabled.Unchecked += (_, _) => Set(preferences.Overlay with { Enabled = false });
        corner.SelectionChanged += (_, _) => Set(preferences.Overlay with { Corner = (OverlayCorner)corner.SelectedIndex });
        scale.ValueChanged += (_, _) => { if (!scale.IsMouseCaptureWithin) Set(preferences.Overlay with { Scale = scale.Value }); };
        scale.LostMouseCapture += (_, _) => Set(preferences.Overlay with { Scale = scale.Value });
        opacity.ValueChanged += (_, _) => { if (!opacity.IsMouseCaptureWithin) Set(preferences.Overlay with { Opacity = opacity.Value }); };
        opacity.LostMouseCapture += (_, _) => Set(preferences.Overlay with { Opacity = opacity.Value });
        return Surface(body, new Thickness(14), new Thickness(0, 0, 0, 12));

        Slider AddSlider(string name, double min, double max, double step, double value)
        {
            var row = new WrapPanel();
            var label = Text(name, 13);
            label.Width = 123;
            row.Children.Add(label);
            var input = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = value,
                SmallChange = step, LargeChange = .1, Width = 150, Margin = new Thickness(4, 4, 8, 5) };
            NameForAutomation(input, name);
            row.Children.Add(input);
            var valueLabel = Text(value.ToString("P0", CultureInfo.CurrentCulture), 13);
            row.Children.Add(valueLabel);
            input.ValueChanged += (_, _) => valueLabel.Text = input.Value.ToString("P0", CultureInfo.CurrentCulture);
            body.Children.Add(row);
            return input;
        }

        void Set(FrameOverlayOptions options)
        {
            if (options == preferences.Overlay) return;
            preferences.Overlay = options.Normalize();
            ApplyOverlay();
            SavePreference();
        }
    }

    void ApplyOverlay()
    {
        var options = preferences.Overlay;
        if (!options.Enabled || options.Metrics == OverlayMetric.None)
        {
            overlay?.Close();
            overlay = null;
            overlayText = null;
            return;
        }
        if (overlay == null)
        {
            overlayText = new TextBlock { Foreground = Brushes.White, FontFamily = FontFamily };
            var background = new Border { Child = overlayText, Background = new SolidColorBrush(Color.FromRgb(15, 21, 30)),
                Padding = new Thickness(11, 7, 11, 7), CornerRadius = new CornerRadius(5) };
            overlay = new Window
            {
                Title = "AirDesk OSD", WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                AllowsTransparency = true, Background = Brushes.Transparent, IsHitTestVisible = false, Content = background
            };
            overlay.SourceInitialized += (_, _) => OverlayWindow.SetPassive(new WindowInteropHelper(overlay).Handle);
            overlay.SizeChanged += (_, _) => PositionOverlay();
        }
        overlayText!.FontSize = 14 * options.Scale;
        overlay.Opacity = options.Opacity;
        RenderFrames(force: true);
        if (!overlay.IsVisible) overlay.Show();
        PositionOverlay();
    }

    void RenderFrames(bool force = false)
    {
        if (frames == null && overlay == null || !force && Environment.TickCount64 < updateFramesAt) return;
        updateFramesAt = Environment.TickCount64 + 950;
        var rate = frames?.Current;
        if (frames != null)
        {
            string detail = rate == null ? frames.Status : $"{rate.FramesPerSecond:0.#} кадр/с · медиана {rate.MedianMilliseconds:0.0} мс";
            if (fpsStatus.Text != detail) fpsStatus.Text = detail;
        }
        if (overlayText == null) return;
        var values = new System.Collections.Generic.List<string>(5);
        var flags = preferences.Overlay.Metrics;
        var telemetry = viewModel.Stalled ? null : latest?.Telemetry;
        var gpu = telemetry?.PrimaryGpu(preferences.Gpu);
        if (flags.HasFlag(OverlayMetric.Frames)) values.Add(rate == null ? "FPS —" : $"FPS {rate.FramesPerSecond:0}");
        if (flags.HasFlag(OverlayMetric.FrameTime)) values.Add(rate == null ? "Кадр —" : $"Кадр {rate.MedianMilliseconds:0.0} мс");
        if (flags.HasFlag(OverlayMetric.Cpu)) values.Add("CPU " + Metric(telemetry?.Cpu));
        if (flags.HasFlag(OverlayMetric.Gpu)) values.Add("GPU " + Metric(gpu));
        if (flags.HasFlag(OverlayMetric.Ram)) values.Add($"RAM {Format(Available(telemetry?.Ram?.Load), "%")}");
        string text = string.Join("  ·  ", values);
        if (overlayText.Text != text) overlayText.Text = text;

        static float? Available(Reading? reading) => reading?.Available == true ? reading.Value : null;
        static string Metric(DeviceReadings? device) =>
            $"{Format(Available(device?.Load), "%")} / {Format(Available(device?.Temperature), "°C")}";
    }

    void PositionOverlay()
    {
        if (overlay is not { IsVisible: true } window || Forms.Screen.PrimaryScreen is not { } screen ||
            PresentationSource.FromVisual(window)?.CompositionTarget is not { } target) return;
        var area = screen.WorkingArea;
        Point first = target.TransformFromDevice.Transform(new Point(area.Left, area.Top));
        Point last = target.TransformFromDevice.Transform(new Point(area.Right, area.Bottom));
        var corner = preferences.Overlay.Corner;
        window.Left = corner is OverlayCorner.TopRight or OverlayCorner.BottomRight ? Math.Max(first.X, last.X - window.ActualWidth - 12) : first.X + 12;
        window.Top = corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight ? Math.Max(first.Y, last.Y - window.ActualHeight - 12) : first.Y + 12;
    }

    void DisposeFrames()
    {
        frames?.Dispose();
        frames = null;
        overlay?.Close();
        overlay = null;
    }
}
