using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AirDesk;

public sealed partial class Dashboard
{
    Forms.NotifyIcon tray = null!;
    Forms.ContextMenuStrip trayMenu = null!;
    Drawing.Icon? customIcon;
    Window? flyout;
    Drawing.Point flyoutAnchor;
    readonly DispatcherTimer trayClick = new(), flyoutDismiss = new();
    long lastDoubleClick;
    TextBlock flyMetrics = null!, flyState = null!, flyCooling = null!;
    WrapPanel flyProfiles = null!;
    Button flyLastConfirmed = null!;
    bool flyoutPositioning;
    bool? flyWarningColor;

    void InitializeTray()
    {
        customIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Открыть AirDesk", null, (_, _) => Dispatcher.BeginInvoke(ShowMain));
        trayMenu.Items.Add("Диагностика", null, (_, _) => Dispatcher.BeginInvoke(() => ShowPage(DiagnosticsTab)));
        var restore = trayMenu.Items.Add("Вернуть управление BIOS/EC", null, (_, _) => viewModel.Release());
        trayMenu.Opening += (_, _) => restore.Enabled = latest?.Cooling.CanRelease == true || viewModel.Stalled;
        trayMenu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(() => _ = ExitAsync()));

        trayClick.Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime);
        trayClick.Tick += (_, _) => { trayClick.Stop(); ToggleFlyout(); };
        flyoutDismiss.Interval = trayClick.Interval + TimeSpan.FromMilliseconds(100);
        flyoutDismiss.Tick += (_, _) => { flyoutDismiss.Stop(); flyout?.Hide(); };
        tray = new Forms.NotifyIcon { Icon = customIcon ?? Drawing.SystemIcons.Application, ContextMenuStrip = trayMenu, Text = "AirDesk" };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left || e.Clicks > 1) return;
            Drawing.Point cursor = Forms.Cursor.Position;
            Dispatcher.BeginInvoke(() =>
            {
                if (shellDisposed || lastDoubleClick > 0 && Environment.TickCount64 - lastDoubleClick < 100) return;
                flyoutAnchor = cursor;
                flyoutDismiss.Stop();
                if (!trayClick.IsEnabled) trayClick.Start();
            });
        };
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            Dispatcher.BeginInvoke(() =>
            {
                lastDoubleClick = Environment.TickCount64;
                trayClick.Stop(); flyoutDismiss.Stop();
                if (!shellDisposed) ShowMain();
            });
        };
        tray.Visible = true;
    }

    void ToggleFlyout()
    {
        if (flyout?.IsVisible == true) { flyout.Hide(); return; }
        flyout ??= CreateFlyout();
        if (latest is { } current) RenderTray(current.Telemetry, current.Cooling, previousStale);
        flyout.Opacity = 0;
        flyout.Show();
        PositionFlyout();
        flyout.Opacity = 1;
        flyout.Activate();
    }

    Window CreateFlyout()
    {
        var window = new Window { Title = "AirDesk · краткий обзор", Width = 360, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            UseLayoutRounding = true, FontFamily = FontFamily, FontSize = 14 };
        window.SetResourceReference(BackgroundProperty, AppTheme.Background);
        window.SetResourceReference(ForegroundProperty, AppTheme.Text);
        var content = new StackPanel();
        content.Children.Add(Text("AirDesk", 21));
        flyMetrics = Text("Ожидание показаний…", 15);
        flyState = Text("Мониторинг", 13); flyState.TextWrapping = TextWrapping.Wrap;
        flyCooling = Text("", 13); flyCooling.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(flyMetrics); content.Children.Add(flyCooling); content.Children.Add(flyState);

        flyProfiles = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new(0, 5, 0, 0) };
        foreach (var kind in new[] { ProfileKind.Quiet, ProfileKind.Balanced, ProfileKind.Performance })
        {
            flyProfiles.Children.Add(Button(ProfileLabel(kind), () => SwitchTrayProfile(kind),
                "Применяет стандартную кривую по температуре процессора к активной группе."));
        }
        flyLastConfirmed = Button("Последний подтверждённый", () =>
        {
            if (CanSwitchTrayProfile() && channel is { } id && preferences.Find(id)?.LastConfirmed is { } profile)
                _ = ChangeProfile(profile);
        });
        flyProfiles.Children.Add(flyLastConfirmed);
        content.Children.Add(flyProfiles);
        var links = new WrapPanel { Margin = new(0, 7, 0, 0) };
        links.Children.Add(Button("Открыть AirDesk", ShowMain));
        links.Children.Add(Button("Подробности", () => ShowPage(latest?.Cooling.Conflict != null ? CoolingTab : DiagnosticsTab)));
        content.Children.Add(links);
        window.Content = Surface(Scroll(content), new Thickness(14), new Thickness(0));
        window.Deactivated += (_, _) => { if (window.IsVisible) { flyoutDismiss.Stop(); flyoutDismiss.Start(); } };
        window.Activated += (_, _) => flyoutDismiss.Stop();
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Hide(); e.Handled = true; } };
        window.SizeChanged += (_, _) => { if (window.IsVisible && !flyoutPositioning) Dispatcher.BeginInvoke(PositionFlyout, DispatcherPriority.Loaded); };
        return window;
    }

    bool CanSwitchTrayProfile() => !autostartSession && !previousStale && !commandPending && !viewModel.Stalled &&
        latest?.Cooling is { State: ChannelState.Controlled, CanAdjust: true, Conflict: null };

    void SwitchTrayProfile(ProfileKind kind)
    {
        if (CanSwitchTrayProfile()) _ = ChangeProfile(selected with { Kind = kind, Primary = null, Secondary = null });
    }

    void RenderTray(Telemetry data, CoolingView cooling, bool stale)
    {
        var gpu = preferences.SelectedGpu(data);
        float? Value(Reading? reading) => !stale && reading?.Available == true ? reading.Value : null;
        float? cpuTemp = Value(data.Cpu?.Temperature), cpuLoad = Value(data.Cpu?.Load), gpuTemp = Value(gpu?.Temperature), gpuLoad = Value(gpu?.Load),
            ramLoad = Value(data.Ram?.Load), rpm = stale ? null : cooling.Rpm;
        static string? Tip(string label, float? value, string unit) => value.HasValue ? $"{label} {value:0} {unit}".TrimEnd() : null;
        string tip = DesktopLayout.Tooltip(preferences.Tooltip.Select(metric => metric switch
        {
            TrayMetric.CpuTemperature => Tip("CPU", cpuTemp, "°C"), TrayMetric.CpuLoad => Tip("CPU", cpuLoad, "%"),
            TrayMetric.GpuTemperature => Tip("GPU", gpuTemp, "°C"), TrayMetric.GpuLoad => Tip("GPU", gpuLoad, "%"),
            TrayMetric.MemoryLoad => Tip("RAM", ramLoad, "%"), TrayMetric.FanRpm => Tip("RPM", rpm, ""), _ => null
        }).OfType<string>());
        if (tray.Text != tip) tray.Text = tip;
        if (flyout is not { IsVisible: true } && flyState == null) return;

        flyMetrics.Text = $"Процессор   {Format(cpuTemp, "°C")} · {Format(cpuLoad, "%")}\nВидеокарта   {Format(gpuTemp, "°C")} · {Format(gpuLoad, "%")}\nПамять   {Format(ramLoad, "%")}";
        string owner = cooling.State == ChannelState.Controlled ? ProfileLabel(selected.Kind) : "Наблюдение";
        flyCooling.Text = $"{cooling.Group ?? "Охлаждение"} · {owner}\nОбороты: {Format(rpm, "об/мин")}";
        flyState.Text = stale ? "Данные устарели. Откройте диагностику." : cooling.Conflict ??
            (requestError.Text.Length != 0 ? requestError.Text : cooling.State == ChannelState.Error ? FriendlyStatus(cooling) : statusHeading.Text);
        bool warning = stale || cooling.State is ChannelState.Conflict or ChannelState.Error || requestError.Text.Length != 0;
        if (flyWarningColor != warning)
        {
            flyState.SetResourceReference(TextBlock.ForegroundProperty, warning ? AppTheme.Warning : AppTheme.Accent);
            flyWarningColor = warning;
        }
        bool canSwitch = CanSwitchTrayProfile();
        flyProfiles.Visibility = canSwitch ? Visibility.Visible : Visibility.Collapsed;
        flyLastConfirmed.Visibility = canSwitch && channel != null && preferences.Find(channel)?.LastConfirmed is { } saved && !selected.SameSettings(saved)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    static string ProfileLabel(ProfileKind kind) => kind switch
    {
        ProfileKind.Quiet => "Тихий", ProfileKind.Balanced => "Сбалансированный", ProfileKind.Performance => "Производительный",
        ProfileKind.Custom => "Пользовательский", _ => "Вручную"
    };

    static string TrayMetricName(TrayMetric metric) => metric switch
    {
        TrayMetric.CpuTemperature => "Температура CPU", TrayMetric.CpuLoad => "Загрузка CPU", TrayMetric.GpuTemperature => "Температура GPU",
        TrayMetric.GpuLoad => "Загрузка GPU", TrayMetric.MemoryLoad => "Память", _ => "Обороты вентилятора"
    };

    void PositionFlyout()
    {
        if (flyout is not { IsVisible: true } || flyoutPositioning) return;
        flyoutPositioning = true;
        try
        {
            var screen = Forms.Screen.FromPoint(flyoutAnchor);
            var area = screen.WorkingArea;
            IntPtr hwnd = new WindowInteropHelper(flyout).Handle;
            if (Forms.Screen.FromHandle(hwnd).DeviceName != screen.DeviceName)
                SetWindowPos(hwnd, IntPtr.Zero, area.Left, area.Top, 0, 0, PositionOnly);
            double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
            flyout.MaxWidth = area.Width / scale;
            flyout.MaxHeight = area.Height / scale;
            flyout.UpdateLayout();
            if (!GetWindowRect(hwnd, out var rect)) return;
            static DesktopLayout.Area Box(Drawing.Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);
            var position = DesktopLayout.Anchor(Box(screen.Bounds), Box(area), flyoutAnchor.X, flyoutAnchor.Y,
                rect.Right - rect.Left, rect.Bottom - rect.Top, (int)Math.Round(10 * scale));
            SetWindowPos(hwnd, IntPtr.Zero, position.X, position.Y, 0, 0, PositionOnly);
        }
        finally { flyoutPositioning = false; }
    }

    void DisposeTray()
    {
        trayClick.Stop(); flyoutDismiss.Stop();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        trayMenu?.Dispose(); customIcon?.Dispose(); flyout?.Close();
    }

    const uint PositionOnly = 0x0001 | 0x0004 | 0x0010; // NOSIZE | NOZORDER | NOACTIVATE
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
