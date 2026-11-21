using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Microsoft.Win32;

namespace AirDesk;

public sealed partial class Dashboard
{
    const string FramesDisabled = "Источник FPS отключён.";
    const string PresentMonReleases = "https://github.com/GameTechDev/PresentMon/releases";
    readonly TextBlock fpsStatus = Text(FramesDisabled, 14);
    readonly TextBlock fpsReading = Text("", 15);
    readonly TextBlock osdStatus = Text("", 13);
    readonly Button disconnectFrames = Button("Отключить FPS");
    string? fpsInputError;
    FrameCapture? frames;
    OverlayWindow? overlay;
    TextBlock? overlayPreview;
    Border? overlayPreviewSurface;
    long updateFramesAt;

    FrameworkElement BuildFrameSettings()
    {
        var body = new StackPanel();
        body.Children.Add(Text("FPS и экранная панель", 19));
        BuildFrameSource(body);
        BuildOverlaySettings(body);
        return Surface(body, new Thickness(14), new Thickness(0, 0, 0, 12));
    }

    void BuildFrameSource(Panel body)
    {
        body.Children.Add(Text("Источник FPS · Intel PresentMon", 15));
        fpsStatus.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(fpsStatus, AutomationLiveSetting.Polite);
        body.Children.Add(fpsStatus);
        fpsReading.TextWrapping = TextWrapping.Wrap;
        fpsReading.Visibility = Visibility.Collapsed;
        body.Children.Add(fpsReading);

        AddWrapped(body, "1. Найдите PID игры или приложения в диспетчере задач на вкладке «Подробности».");
        var pid = new TextBox { Width = 106, MaxLength = 10, Margin = new Thickness(8, 0, 0, 7) };
        NameForAutomation(pid, "PID для измерения FPS");
        var pidRow = new WrapPanel();
        pidRow.Children.Add(Text("PID", 14));
        pidRow.Children.Add(pid);
        body.Children.Add(pidRow);
        body.Children.Add(BuildCaptureInstructions(pid));
        AddWrapped(body, "3. Когда начнётся запись, выберите локальный CSV. AirDesk начнёт измерять FPS по новым строкам.");
        var buttons = new WrapPanel();
        buttons.Children.Add(Button("Выбрать CSV…", () =>
        {
            if (!TryReadPid(pid, out int target))
            {
                ShowInputError("Укажите положительный PID из диспетчера задач.");
                pid.Focus();
                return;
            }
            try
            {
                var file = new OpenFileDialog { Filter = "CSV PresentMon|*.csv", CheckFileExists = true };
                if (file.ShowDialog(this) != true) return;
                var reader = new FrameCapture(file.FileName, target);
                frames?.Dispose();
                frames = reader;
                fpsInputError = null;
                RenderFrames(force: true);
            }
            catch (ArgumentException)
            {
                ShowInputError("Выберите CSV PresentMon на локальном диске.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
            {
                Monitor.Log(error);
                ShowInputError("Не удалось подключить CSV PresentMon. Проверьте путь и разрешение на чтение файла.");
            }
        }));
        disconnectFrames.IsEnabled = false;
        disconnectFrames.Click += (_, _) =>
        {
            frames?.Dispose();
            frames = null;
            fpsInputError = null;
            RenderFrames(force: true);
        };
        buttons.Children.Add(disconnectFrames);
        body.Children.Add(buttons);

        void ShowInputError(string message)
        {
            fpsInputError = message;
            RenderFrames(force: true);
        }
    }

    Expander BuildCaptureInstructions(TextBox pid)
    {
        var instructions = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        AddWrapped(instructions, "Скачайте консольную версию PresentMon x64 и проверьте её подпись. Откройте Windows PowerShell 5.1 в папке PresentMon и выполните команды. Если имя EXE отличается, замените его в первой строке.");
        instructions.Children.Add(Button("Выпуски Intel PresentMon", () => OpenReleaseLink(PresentMonReleases)));
        var command = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"),
            FontSize = 12, Margin = new Thickness(0, 0, 0, 6)
        };
        NameForAutomation(command, "Команды записи CSV для Windows PowerShell");
        instructions.Children.Add(command);
        var feedback = Text("", 13);
        feedback.TextWrapping = TextWrapping.Wrap;
        var copy = Button("Скопировать команды", () =>
        {
            try
            {
                Clipboard.SetText(command.Text);
                feedback.Text = "Команды скопированы. Вставьте их в PowerShell.";
            }
            catch (ExternalException error)
            {
                Monitor.Log(error);
                feedback.Text = "Не удалось скопировать. Выделите команды и нажмите Ctrl+C.";
            }
        });
        instructions.Children.Add(copy);
        instructions.Children.Add(feedback);
        pid.TextChanged += (_, _) => UpdateCommand();
        UpdateCommand();
        return new Expander { Header = "2. Запишите CSV в PresentMon", Content = instructions, Margin = new Thickness(0, 1, 0, 8) };

        void UpdateCommand()
        {
            bool valid = TryReadPid(pid, out int target);
            copy.IsEnabled = valid;
            command.Text = CaptureCommand(valid ? target : 1234);
            feedback.Text = valid ? "" : "Укажите PID выше, чтобы скопировать команды для своего процесса.";
        }
    }

    static bool TryReadPid(TextBox input, out int pid) =>
        int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0;

    static string CaptureCommand(int pid) => $$"""
        $presentMonPath = (Resolve-Path -LiteralPath '.\PresentMon-2.6.0-x64.exe' -ErrorAction Stop).Path
        $csv = Join-Path $PWD ('airdesk-frames-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.csv')
        $file = [IO.File]::Open($csv, 'CreateNew', 'Write', 'Read')
        $writer = [IO.StreamWriter]::new($file, [Text.UTF8Encoding]::new($false))
        try {
            $writer.AutoFlush = $true
            Write-Host $csv
            & $presentMonPath --process_id {{pid.ToString(CultureInfo.InvariantCulture)}} --v1_metrics --no_track_input --output_stdout |
                ForEach-Object { $writer.WriteLine($_) }
            if ($LASTEXITCODE -ne 0) { Write-Error "PresentMon завершился с кодом $LASTEXITCODE." }
        } finally {
            $writer.Dispose()
        }
        """;

    void BuildOverlaySettings(Panel body)
    {
        var heading = Text("Экранная панель (OSD)", 15);
        heading.Margin = new Thickness(0, 10, 0, 5);
        body.Children.Add(heading);
        var enabled = new CheckBox { Content = "Показывать экранную панель", IsChecked = preferences.Overlay.Enabled,
            Margin = new Thickness(0, 3, 0, 6) };
        body.Children.Add(enabled);
        osdStatus.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(osdStatus);
        var layout = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        AddWrapped(layout, "Панель видна поверх окон на основном мониторе и пропускает щелчки. В эксклюзивном полноэкранном режиме она может быть скрыта.");
        layout.Children.Add(Text("Угол экрана", 13));
        var corner = new ComboBox { ItemsSource = new[] { "Сверху слева", "Сверху справа", "Снизу слева", "Снизу справа" },
            SelectedIndex = (int)preferences.Overlay.Corner, MinWidth = 165, HorizontalAlignment = HorizontalAlignment.Left };
        NameForAutomation(corner, "Положение экранной панели");
        layout.Children.Add(corner);
        var scale = AddSlider("Масштаб", .75, 2, .05, preferences.Overlay.Scale);
        var opacity = AddSlider("Непрозрачность", .25, 1, .05, preferences.Overlay.Opacity);
        layout.Children.Add(Text("Показатели", 13));
        var metrics = new WrapPanel();
        layout.Children.Add(metrics);
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
        corner.SelectionChanged += (_, _) =>
        {
            if (corner.SelectedIndex >= 0) Set(preferences.Overlay with { Corner = (OverlayCorner)corner.SelectedIndex });
        };
        scale.ValueChanged += (_, _) => { if (!scale.IsMouseCaptureWithin) Set(preferences.Overlay with { Scale = scale.Value }); };
        scale.LostMouseCapture += (_, _) => Set(preferences.Overlay with { Scale = scale.Value });
        opacity.ValueChanged += (_, _) => { if (!opacity.IsMouseCaptureWithin) Set(preferences.Overlay with { Opacity = opacity.Value }); };
        opacity.LostMouseCapture += (_, _) => Set(preferences.Overlay with { Opacity = opacity.Value });
        layout.Children.Add(Text("Предпросмотр панели", 13));
        overlayPreview = OverlayWindow.CreateText(FontFamily);
        overlayPreviewSurface = OverlayWindow.CreateSurface(overlayPreview);
        overlayPreviewSurface.HorizontalAlignment = HorizontalAlignment.Left;
        layout.Children.Add(overlayPreviewSurface);
        var advanced = new Expander { Header = "Внешний вид и показатели", Content = layout };
        advanced.Expanded += (_, _) => RenderFrames(force: true);
        body.Children.Add(advanced);
        UpdateOverlaySettings();

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
            layout.Children.Add(row);
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
        UpdateOverlaySettings();
        if (!options.Enabled || options.Metrics == OverlayMetric.None)
        {
            overlay?.Close();
            overlay = null;
        }
        else
        {
            overlay ??= new OverlayWindow(FontFamily);
            overlay.ApplyOptions(options);
        }
        RenderFrames(force: true);
        if (overlay is { IsVisible: false } window) window.Show();
        overlay?.Position();
    }

    void UpdateOverlaySettings()
    {
        var options = preferences.Overlay;
        osdStatus.Text = !options.Enabled ? "Экранная панель выключена." : options.Metrics == OverlayMetric.None
            ? "Выберите хотя бы один показатель панели." : "Экранная панель включена.";
        if (overlayPreview != null) overlayPreview.FontSize = 14 * options.Scale;
        if (overlayPreviewSurface != null) overlayPreviewSurface.Opacity = options.Opacity;
    }

    void RenderFrames(bool force = false)
    {
        if (!force && ((frames == null && overlay == null && overlayPreview?.IsVisible != true) || Environment.TickCount64 < updateFramesAt)) return;
        updateFramesAt = Environment.TickCount64 + 950;
        var (rate, status) = frames?.Snapshot ?? (null, FramesDisabled);
        string detail = frames == null ? status : $"{Path.GetFileName(frames.FilePath)} · PID {frames.ProcessId}\n{status}";
        if (fpsInputError != null) detail = fpsInputError + "\n" + detail;
        if (fpsStatus.Text != detail)
        {
            fpsStatus.Text = detail;
            UIElementAutomationPeer.FromElement(fpsStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        fpsStatus.ToolTip = frames?.FilePath;
        disconnectFrames.IsEnabled = frames != null;
        fpsReading.Visibility = rate == null ? Visibility.Collapsed : Visibility.Visible;
        fpsReading.Text = rate == null ? "" : $"{rate.FramesPerSecond:0.#} кадр/с · медиана {rate.MedianMilliseconds:0.0} мс";
        if (overlay == null && overlayPreview == null) return;
        var rows = new List<string>(4);
        var timing = new List<string>(2);
        var flags = preferences.Overlay.Metrics;
        var telemetry = previousStale || viewModel.Stalled ? null : latest?.Telemetry;
        var gpu = telemetry == null ? null : preferences.SelectedGpu(telemetry);
        if (flags.HasFlag(OverlayMetric.Frames)) timing.Add(rate == null ? "FPS —" : $"FPS {rate.FramesPerSecond:0}");
        if (flags.HasFlag(OverlayMetric.FrameTime)) timing.Add(rate == null ? "Кадр —" : $"Кадр {rate.MedianMilliseconds:0.0} мс");
        if (timing.Count > 0) rows.Add(string.Join(" · ", timing));
        if (flags.HasFlag(OverlayMetric.Cpu)) rows.Add("CPU " + Metric(telemetry?.Cpu));
        if (flags.HasFlag(OverlayMetric.Gpu)) rows.Add("GPU " + Metric(gpu));
        if (flags.HasFlag(OverlayMetric.Ram)) rows.Add($"RAM {Format(LiveValue(telemetry?.Ram?.Load, false), "%")}");
        string text = string.Join("\n", rows);
        if (overlayPreview != null) overlayPreview.Text = text.Length == 0 ? "Показатели не выбраны" : text;
        if (overlay != null) overlay.Metrics = text;

        static string Metric(DeviceReadings? device) =>
            $"{Format(LiveValue(device?.Load, false), "%")} · {Format(LiveValue(device?.Temperature, false), "°C")}";
    }

    void DisposeFrames()
    {
        frames?.Dispose();
        frames = null;
        overlay?.Close();
        overlay = null;
    }
}
