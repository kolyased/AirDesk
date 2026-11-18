using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AirDesk;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        using var legacy = new Mutex(true, @"Local\AirDesk-0.1", out bool first);
        using var single = new Mutex(true, @"Local\AirDesk", out bool unique);
        if (!first || !unique)
        {
            if (!args.Contains("--autostart")) MessageBox.Show("AirDesk уже работает. Откройте значок в трее.");
            return;
        }

        bool autostart = args.Contains("--autostart");
        bool smoke = args.Contains("--smoke") || args.Contains("--ui-smoke");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Dashboard(autostart);
        app.MainWindow = window;
        app.DispatcherUnhandledException += async (_, error) =>
        {
            error.Handled = true;
            Monitor.Log(error.Exception);
            await window.ExitAsync("Ошибка интерфейса. AirDesk завершает управление.");
        };
        app.SessionEnding += (_, _) => window.EndSession();
        app.Exit += (_, _) => window.DisposeShell();
        if (smoke || !args.Contains("--tray") && !window.StartMinimized) window.Show();
        window.Start(smoke, args.Contains("--ui-smoke"));
        app.Run();
    }
}

public sealed partial class Dashboard : Window
{
    const int OverviewTab = 0, CoolingTab = 1, DevicesTab = 2, DiagnosticsTab = 3, SettingsTab = 4;
    readonly Monitor monitor;
    readonly UserPreferences preferences = UserPreferences.Load();
    readonly bool autostartSession;
    readonly TabControl tabs = new() { BorderThickness = new(0) };
    readonly TextBlock status = Text("Подключение к датчикам…", 13), statusHeading = Text("Мониторинг", 15),
        statusTime = Text("", 12), setupEquipment = Text("Оборудование опрашивается…", 13), activeSummary = Text("Управление BIOS/EC", 14);
    readonly TextBlock cpuValue = Text("—", 32), ramValue = Text("—", 32), gpuValue = Text("—", 32), diskValue = Text("—", 32);
    readonly TextBlock cpuSub = Text("Процессор", 12), ramSub = Text("Память", 12), gpuSub = Text("Видеокарта", 12), diskSub = Text("Накопители", 12);
    readonly Queue<(DateTimeOffset Time, double Value)> history = new();
    readonly Polyline graph = new() { StrokeThickness = 2 };
    readonly Canvas graphCanvas = new() { Margin = new(16, 24, 16, 10), ClipToBounds = true };
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly UniformGrid overviewCards = new() { Columns = 4, Margin = new(0, 0, 0, 14) };
    readonly Dictionary<OverviewCard, Border> cards = new();
    Border welcome = null!;
    Snapshot? latest;
    Telemetry? displayed;
    bool quitting, warnedStalled, shellDisposed;
    bool? warningColor;
    public bool StartMinimized => preferences.StartMinimized;

    public Dashboard(bool autostart)
    {
        autostartSession = autostart;
        monitor = new Monitor(monitorOnly: autostart);
        AppTheme.Apply(preferences.Theme);
        Title = "AirDesk";
        Width = 1040; Height = 760; MinWidth = 330; MinHeight = 345;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, AppTheme.Background);
        SetResourceReference(ForegroundProperty, AppTheme.Text);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new(18) };
        Content = root;

        var head = new DockPanel { Margin = new(0, 0, 0, 10) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var hide = Button("Свернуть в трей", Hide);
        DockPanel.SetDock(hide, Dock.Right); head.Children.Add(hide);
        var name = Text("AirDesk", 28); name.FontWeight = FontWeights.SemiBold; head.Children.Add(name);

        var health = new StackPanel();
        var healthHeader = new DockPanel();
        var inspect = Button("Подробности", () => statusTime.Visibility = statusTime.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        inspect.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(inspect, Dock.Right); healthHeader.Children.Add(inspect);
        statusHeading.VerticalAlignment = VerticalAlignment.Center; statusHeading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(statusHeading, AutomationLiveSetting.Polite);
        healthHeader.Children.Add(statusHeading);
        health.Children.Add(healthHeader); health.Children.Add(status);
        status.TextWrapping = TextWrapping.Wrap;
        statusTime.TextWrapping = TextWrapping.Wrap; statusTime.Visibility = Visibility.Collapsed; health.Children.Add(statusTime);
        var healthPanel = Surface(health, new Thickness(12, 7, 12, 7), new Thickness(0, 8, 0, 0));
        DockPanel.SetDock(healthPanel, Dock.Bottom); root.Children.Add(healthPanel);

        tabs.SetResourceReference(BackgroundProperty, AppTheme.Background);
        root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "_Обзор", Content = Scroll(BuildOverview()) });
        tabs.Items.Add(new TabItem { Header = "_Охлаждение", Content = Scroll(BuildCooling()) });
        tabs.Items.Add(new TabItem { Header = "_Устройства", Content = BuildDevices() });
        tabs.Items.Add(new TabItem { Header = "_Диагностика", Content = BuildDiagnostics() });
        tabs.Items.Add(new TabItem { Header = "_Настройки", Content = Scroll(BuildSettings()) });
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || e.Key < Key.D1 || e.Key > Key.D5) return;
            Navigate(e.Key - Key.D1); e.Handled = true;
        };
        InitializeTray();
        Closing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        refresh.Tick += (_, _) => Render();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    FrameworkElement BuildOverview()
    {
        var body = new StackPanel { Margin = new(0, 12, 0, 0) };
        var welcomeBody = new StackPanel();
        welcomeBody.Children.Add(Text("Первый запуск", 20));
        AddWrapped(welcomeBody, "AirDesk начал с мониторинга. Калибровка доступна на поддерживаемой плате и запускается вручную.");
        setupEquipment.TextWrapping = TextWrapping.Wrap; welcomeBody.Children.Add(setupEquipment);
        var welcomeButtons = new WrapPanel(); welcomeBody.Children.Add(welcomeButtons);
        welcomeButtons.Children.Add(Button("Настроить охлаждение", () => Navigate(CoolingTab)));
        welcomeButtons.Children.Add(Button("Скрыть подсказку", () =>
        {
            preferences.WelcomeDismissed = true; SavePreference(); welcome.Visibility = Visibility.Collapsed;
        }));
        welcome = Surface(welcomeBody, new Thickness(16), new Thickness(0, 0, 0, 14));
        welcome.Visibility = preferences.WelcomeDismissed ? Visibility.Collapsed : Visibility.Visible;
        body.Children.Add(welcome);

        cards[OverviewCard.Cpu] = Card("Процессор", cpuValue, cpuSub);
        cards[OverviewCard.Gpu] = Card("Видеокарта", gpuValue, gpuSub);
        cards[OverviewCard.Memory] = Card("Память", ramValue, ramSub);
        cards[OverviewCard.Storage] = Card("Накопители", diskValue, diskSub);
        ReorderCards();
        overviewCards.SizeChanged += (_, e) => overviewCards.Columns = DesktopLayout.Columns(e.NewSize.Width);
        body.Children.Add(overviewCards);

        var cooling = new StackPanel();
        cooling.Children.Add(Text("Охлаждение", 17));
        activeSummary.TextWrapping = TextWrapping.Wrap; cooling.Children.Add(activeSummary);
        cooling.Children.Add(Button("Открыть охлаждение", () => Navigate(CoolingTab)));
        body.Children.Add(Surface(cooling, new Thickness(14), new Thickness(0, 0, 0, 10)));

        var chart = new Grid { Height = 76, Margin = new(0, 0, 0, 4) };
        var chartLabel = Text("Загрузка процессора за 60 секунд", 12); chartLabel.Margin = new(16, 8, 0, 0);
        chart.Children.Add(chartLabel);
        graph.SetResourceReference(Shape.StrokeProperty, AppTheme.Accent);
        graphCanvas.Children.Add(graph); chart.Children.Add(graphCanvas);
        graphCanvas.SizeChanged += (_, _) => DrawGraph();
        body.Children.Add(Surface(chart, new Thickness(0), new Thickness(0, 0, 0, 14)));

        return body;
    }

    void ReorderCards()
    {
        overviewCards.Children.Clear();
        foreach (var item in preferences.Cards.Where(item => item.Visible)) overviewCards.Children.Add(cards[item.Card]);
    }

    void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(() => { if (!shellDisposed) AppTheme.Apply(preferences.Theme); });
    }

    public void Start(bool smoke, bool uiSmoke)
    {
        monitor.Start();
        refresh.Start();
        if (!smoke) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "smoke.json"), ReportText());
                if (uiSmoke) CaptureInterface();
            }
            finally { await ExitAsync(); }
        };
        timer.Start();
    }

    void Render()
    {
        latest = monitor.Latest;
        if (latest == null)
        {
            if (monitor.Stalled)
            {
                statusHeading.Text = "Сбой датчиков";
                status.Text = "Оборудование не отвечает. Откройте диагностику и проверьте доступ к драйверу.";
            }
            return;
        }
        bool stale = monitor.Stalled || DateTimeOffset.Now - latest.Time >= TimeSpan.FromSeconds(4) ||
            DateTimeOffset.TryParse(latest.Cooling.LastPollUtc, out var polled) && DateTimeOffset.Now - polled >= TimeSpan.FromSeconds(4);
        if (stale && !warnedStalled && (latest.Cooling.CanRelease || latest.Cooling.State is ChannelState.Controlled or ChannelState.Restoring))
        {
            warnedStalled = true;
            monitor.Release();
            tray.ShowBalloonTip(4500, "AirDesk", "Драйвер не отвечает. Возврат BIOS/EC запрошен, результат пока не подтверждён.", Forms.ToolTipIcon.Warning);
        }
        if (!stale) warnedStalled = false;
        if (!ReferenceEquals(displayed, latest.Telemetry) || stale != previousStale)
        {
            bool newData = !ReferenceEquals(displayed, latest.Telemetry);
            displayed = latest.Telemetry;
            previousStale = stale;
            RenderTelemetry(displayed, stale, newData);
            setupEquipment.Text = $"Найдено: {string.Join(", ", displayed.Devices.Where(device => device.Type != "Memory").Select(device => device.Name).Distinct().Take(6))}.\n{driverStatus}";
        }
        RenderSystem(latest, stale);
        RenderCooling(latest.Cooling, stale);
        RenderTray(latest.Telemetry, latest.Cooling, stale);
    }

    void RenderSystem(Snapshot snapshot, bool stale)
    {
        var state = snapshot.Cooling.State;
        string heading = stale || snapshot.PollIssue != null ? "Сбой датчиков" : state switch
        {
            ChannelState.Conflict => "Конфликт управления",
            ChannelState.Controlled => "Управление активно",
            ChannelState.Unverified or ChannelState.Unavailable => "Требуется настройка",
            ChannelState.Restoring => "Возврат управления",
            ChannelState.Error => "Ошибка управления",
            _ => "Мониторинг"
        };
        if (statusHeading.Text != heading)
        {
            statusHeading.Text = heading;
            UIElementAutomationPeer.FromElement(statusHeading)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        bool warning = state is ChannelState.Conflict or ChannelState.Error || stale || snapshot.PollIssue != null;
        if (warningColor != warning)
        {
            statusHeading.SetResourceReference(TextBlock.ForegroundProperty, warning ? AppTheme.Warning : AppTheme.Accent);
            warningColor = warning;
        }
        status.Text = stale ? "Показания устарели. Откройте диагностику, чтобы проверить драйвер." :
            snapshot.PollIssue != null ? "Часть показаний недоступна. Сведения об ошибке есть в диагностике." :
            autostartSession && state == ChannelState.MonitorOnly ? "Автозапуск работает в режиме мониторинга. Для настройки охлаждения откройте AirDesk вручную." : FriendlyStatus(snapshot.Cooling);
        string lastPoll = DateTimeOffset.TryParse(snapshot.Cooling.LastPollUtc, out var time) ? time.ToLocalTime().ToString("HH:mm:ss") : "—";
        statusTime.Text = $"Последний опрос: {lastPoll}\n{snapshot.PollIssue ?? snapshot.Cooling.Reason}";
        string owner = state == ChannelState.Controlled ? ProfileLabel(selected.Kind) : heading;
        activeSummary.Text = $"{snapshot.Cooling.Group ?? "Разъём охлаждения"} · {owner}\nОбороты: {Format(stale ? null : snapshot.Cooling.Rpm, "об/мин")}";
    }

    static string FriendlyStatus(CoolingView cooling) => cooling.State switch
    {
        ChannelState.Controlled => "AirDesk управляет подтверждённой группой охлаждения.",
        ChannelState.Unverified => "Для управления настройте привязку вентиляторов на вкладке «Охлаждение».",
        ChannelState.MonitorOnly => "Доступно наблюдение. Причину ограничения можно посмотреть в диагностике.",
        ChannelState.Verified => "Группа проверена. Наблюдение работает, управление включается вручную.",
        ChannelState.Error when cooling.NeedsRecovery => "Проверьте охлаждение в BIOS/EC и подтвердите восстановление на вкладке «Охлаждение».",
        ChannelState.Error => "Управление остановлено. Причина и результат возврата доступны в подробностях.",
        _ => cooling.Reason
    };

    void DrawGraph()
    {
        DateTimeOffset left = DateTimeOffset.Now.AddSeconds(-60);
        graph.Points = new PointCollection(history.Where(item => item.Time >= left).Select(item =>
            new Point((item.Time - left).TotalSeconds * graphCanvas.ActualWidth / 60, graphCanvas.ActualHeight * (1 - Math.Clamp(item.Value, 0, 100) / 100))));
    }

    void Navigate(int index) => tabs.SelectedIndex = index;
    void ShowMain() { flyout?.Hide(); Show(); WindowState = WindowState.Normal; Activate(); }
    void ShowPage(int index) { ShowMain(); Navigate(index); }

    internal void EndSession()
    {
        try { monitor.ShutdownAsync().GetAwaiter().GetResult(); }
        catch (Exception error) { Monitor.Log(error); }
        finally { DisposeShell(); }
    }

    internal async Task ExitAsync(string? reason = null)
    {
        if (quitting) return;
        quitting = true;
        string? warning = reason;
        try
        {
            CoolingView? result = await monitor.ShutdownAsync();
            if (result is { NeedsRecovery: true })
                warning = "Возврат BIOS/EC не подтверждён. Проверьте охлаждение. Подробности в %LOCALAPPDATA%\\AirDesk\\cooling.log.";
        }
        catch (Exception error)
        {
            Monitor.Log(error);
            warning = "Драйвер не ответил вовремя. Возврат BIOS/EC не подтверждён. Проверьте охлаждение.";
        }
        if (warning != null) MessageBox.Show(warning, "AirDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        DisposeShell(); Application.Current.Shutdown();
    }

    internal void DisposeShell()
    {
        if (shellDisposed) return;
        shellDisposed = true;
        refresh.Stop(); debounce.Stop();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        DisposeTray();
    }

    static string Format(float? value, string unit) => value.HasValue ? $"{value:0.#} {unit}" : "—";
    static TextBlock Text(string text, double size)
    {
        var label = new TextBlock { Text = text, FontSize = size, Margin = new(0, 0, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, size <= 12 ? AppTheme.Muted : AppTheme.Text);
        return label;
    }
    static Button Button(string text, Action action, string? tip = null)
    {
        var button = new Button { Content = text, Padding = new(10, 6, 10, 6), MinHeight = 32, Margin = new(0, 0, 8, 6), ToolTip = tip };
        button.Click += (_, _) => action();
        return button;
    }
    static void NameForAutomation(DependencyObject element, string name) => AutomationProperties.SetName(element, name);
    static ScrollViewer Scroll(FrameworkElement element) => new() { Content = element, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    static Border Surface(UIElement element, Thickness padding, Thickness margin)
    {
        var border = new Border { Child = element, CornerRadius = new(10), Padding = padding, Margin = margin, BorderThickness = new(1) };
        border.SetResourceReference(Border.BackgroundProperty, AppTheme.Surface);
        border.SetResourceReference(Border.BorderBrushProperty, AppTheme.Edge);
        return border;
    }
    static Border Card(string title, TextBlock value, TextBlock sub)
    {
        var content = new StackPanel(); content.Children.Add(Text(title, 13));
        value.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.Accent);
        value.FontWeight = FontWeights.SemiBold;
        content.Children.Add(value); sub.TextWrapping = TextWrapping.Wrap; content.Children.Add(sub);
        return Surface(content, new Thickness(14), new Thickness(0, 0, 8, 8));
    }
}
