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
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AirDesk;

public sealed partial class Dashboard : Window
{
    const int OverviewTab = 0, CoolingTab = 1, DevicesTab = 2, DiagnosticsTab = 3, SettingsTab = 4, HistoryTab = 5;
    readonly MonitoringSession viewModel;
    readonly DisplayMetric cpuMetric = new(), gpuMetric = new(), memoryMetric = new(), storageMetric = new();
    readonly UserPreferences preferences;
    readonly bool autostartSession;
    readonly DriverStatus startupDriver;
    readonly TabControl tabs = new() { BorderThickness = new(0) };
    readonly TextBlock status = Text("Подключение к датчикам…", 13), statusHeading = Text("Мониторинг", 15),
        statusTime = Text("", 12), setupEquipment = Text("Оборудование опрашивается…", 13), activeSummary = Text("Управление BIOS/EC", 14);
    readonly StackPanel systemDetails = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock cpuValue = Text("—", 32), ramValue = Text("—", 32), gpuValue = Text("—", 32), diskValue = Text("—", 32);
    readonly TextBlock cpuSub = Text("Процессор", 12), ramSub = Text("Память", 12), gpuSub = Text("Видеокарта", 12), diskSub = Text("Накопители", 12);
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly UniformGrid overviewCards = new() { Columns = 4, Margin = new(0, 0, 0, 14) };
    readonly Dictionary<OverviewCard, Border> cards = new();
    Border welcome = null!;
    Snapshot? latest;
    Telemetry? displayed;
    Telemetry? renderedTelemetry;
    bool renderedStale;
    bool quitting, warnedStalled, shellDisposed;
    bool compactLayout;
    bool? warningColor;
    public bool StartMinimized => preferences.StartMinimized;

    internal Dashboard(MonitoringSession session, DriverStatus startupDriver, bool autostart, bool benchmark, bool recordHistory)
    {
        viewModel = session;
        this.startupDriver = startupDriver;
        autostartSession = autostart;
        preferences = UserPreferences.Load(benchmark ? System.IO.Path.Combine(AppContext.BaseDirectory, "ui-test-preferences.json") : null);
        viewModel.ConfigureHistory(benchmark ? recordHistory ? HistoryRetention.Hour : HistoryRetention.Off : preferences.Retention,
            benchmark ? null : preferences.HistorySensors);
        if (!benchmark) viewModel.ConfigureRules(preferences.Rules);
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
        var inspect = Button("Подробности", () =>
        {
            systemDetails.Visibility = systemDetails.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            status.Visibility = compactLayout && systemDetails.Visibility == Visibility.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        });
        inspect.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(inspect, Dock.Right); healthHeader.Children.Add(inspect);
        statusHeading.VerticalAlignment = VerticalAlignment.Center; statusHeading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(statusHeading, AutomationLiveSetting.Polite);
        healthHeader.Children.Add(statusHeading);
        health.Children.Add(healthHeader); health.Children.Add(status);
        status.TextWrapping = TextWrapping.Wrap;
        statusTime.TextWrapping = TextWrapping.Wrap;
        systemDetails.Children.Add(statusTime);
        var diagnostics = Button("Открыть диагностику", () => Navigate(DiagnosticsTab));
        diagnostics.HorizontalAlignment = HorizontalAlignment.Left;
        systemDetails.Children.Add(diagnostics);
        health.Children.Add(systemDetails);
        var healthPanel = Surface(health, new Thickness(12, 7, 12, 7), new Thickness(0, 8, 0, 0));
        DockPanel.SetDock(healthPanel, Dock.Bottom); root.Children.Add(healthPanel);

        tabs.SetResourceReference(BackgroundProperty, AppTheme.Background);
        root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "_Обзор", Content = Scroll(BuildOverview()) });
        tabs.Items.Add(new TabItem { Header = "_Охлаждение", Content = Scroll(BuildCooling()) });
        tabs.Items.Add(new TabItem { Header = "_Устройства", Content = BuildDevices() });
        tabs.Items.Add(new TabItem { Header = "_Диагностика", Content = BuildDiagnostics() });
        tabs.Items.Add(new TabItem { Header = "_Настройки", Content = Scroll(BuildSettings()) });
        tabs.Items.Add(new TabItem { Header = "_История", Content = Scroll(BuildHistory()) });
        SizeChanged += (_, size) =>
        {
            bool compact = size.NewSize.Width < 510 || size.NewSize.Height < 500;
            if (compact == compactLayout) return;
            compactLayout = compact;
            root.Margin = new(compact ? 10 : 18);
            name.FontSize = compact ? 23 : 28;
            hide.Content = compact ? "В трей" : "Свернуть в трей";
            status.Visibility = compact && systemDetails.Visibility == Visibility.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            foreach (var value in new[] { cpuValue, gpuValue, ramValue, diskValue }) value.FontSize = compact ? 25 : 32;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || e.Key < Key.D1 || e.Key > Key.D6) return;
            Navigate(e.Key - Key.D1); e.Handled = true;
        };
        InitializeTray();
        Closing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        refresh.Tick += (_, _) => Render();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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

        var heading = new DockPanel { Margin = new(0, 0, 8, 4) };
        var choose = Button("Показатели…", () =>
        {
            Navigate(SettingsTab);
            Dispatcher.BeginInvoke(() => monitorSettingsHeading.BringIntoView(), DispatcherPriority.Loaded);
        });
        choose.Margin = new(0, 0, 0, 6);
        DockPanel.SetDock(choose, Dock.Right);
        heading.Children.Add(choose);
        heading.Children.Add(Text("Мониторинг", 19));
        body.Children.Add(heading);

        cards[OverviewCard.Cpu] = MonitorCard(OverviewCard.Cpu, cpuValue, cpuSub, "Загрузка");
        cards[OverviewCard.Gpu] = MonitorCard(OverviewCard.Gpu, gpuValue, gpuSub, "Загрузка");
        cards[OverviewCard.Memory] = MonitorCard(OverviewCard.Memory, ramValue, ramSub, "Занято");
        cards[OverviewCard.Storage] = MonitorCard(OverviewCard.Storage, diskValue, diskSub, "Макс. активность");
        BindMetric(cpuValue, cpuSub, cpuMetric); BindMetric(gpuValue, gpuSub, gpuMetric);
        BindMetric(ramValue, ramSub, memoryMetric); BindMetric(diskValue, diskSub, storageMetric);
        ReorderCards();
        overviewCards.SizeChanged += (_, e) => overviewCards.Columns = DesktopLayout.Columns(e.NewSize.Width);
        body.Children.Add(overviewCards);
        body.Children.Add(BuildLiveGraphs());

        var cooling = new StackPanel();
        cooling.Children.Add(Text("Охлаждение", 17));
        activeSummary.TextWrapping = TextWrapping.Wrap; cooling.Children.Add(activeSummary);
        cooling.Children.Add(Button("Открыть охлаждение", () => Navigate(CoolingTab)));
        body.Children.Add(Surface(cooling, new Thickness(14), new Thickness(0, 0, 0, 10)));

        return body;
    }

    static void BindMetric(TextBlock value, TextBlock detail, DisplayMetric metric)
    {
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(DisplayMetric.Value)) { Source = metric });
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(DisplayMetric.Detail)) { Source = metric });
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

    public void Start(bool smoke, bool uiSmoke, bool benchmark, bool soak)
    {
        viewModel.Start();
        refresh.Start();
        if (soak)
        {
            StartSoak();
            return;
        }
        if (!smoke && preferences.Overlay.Enabled) ApplyOverlay();
        if (!smoke && !benchmark) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(benchmark ? 24 : 12) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, benchmark ? "benchmark.json" : "smoke.json"), ReportText());
                if (uiSmoke) await CaptureInterfaceAsync();
            }
            catch (Exception error) { Monitor.Log(error); throw; }
            finally { await ExitAsync(); }
        };
        timer.Start();
    }

    void Render()
    {
        latest = viewModel.Latest;
        if (latest == null)
        {
            RenderFrames();
            if (viewModel.Failure != null || viewModel.Stalled)
            {
                statusHeading.Text = "Сбой датчиков";
                statusHeading.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.Warning);
                warningColor = true;
                status.Text = viewModel.Failure ?? "Оборудование не отвечает. Откройте диагностику и проверьте доступ к драйверу.";
                if (diagnosticTable.IsVisible) RefreshDiagnostics();
            }
            return;
        }
        bool stale = viewModel.Failure != null || viewModel.Stalled || DateTimeOffset.Now - latest.Time >= TimeSpan.FromSeconds(4) ||
            DateTimeOffset.TryParse(latest.Cooling.LastPollUtc, out var polled) && DateTimeOffset.Now - polled >= TimeSpan.FromSeconds(4);
        if (stale && !warnedStalled && (latest.Cooling.CanRelease || latest.Cooling.State is ChannelState.Controlled or ChannelState.Restoring))
        {
            warnedStalled = true;
            viewModel.Release();
            tray.ShowBalloonTip(4500, "AirDesk", "Драйвер не отвечает. Возврат BIOS/EC запрошен, результат пока не подтверждён.", Forms.ToolTipIcon.Warning);
        }
        if (!stale) warnedStalled = false;
        displayed = latest.Telemetry;
        previousStale = stale;
        RenderFrames();
        if (IsVisible && (!ReferenceEquals(renderedTelemetry, displayed) || stale != renderedStale))
        {
            renderedTelemetry = displayed;
            renderedStale = stale;
            RenderTelemetry(displayed, stale);
            setupEquipment.Text = displayed.Devices.Length == 0 ? "Ожидание показаний оборудования…" :
                $"{displayed.Devices.Length} устройств · {displayed.Readings.Count(reading => reading.Available)} датчиков с показаниями.\n{startupDriver.Description}";
        }
        RenderLiveGraphs(latest, stale);
        RenderSystem(latest, stale);
        if (IsVisible) RenderCooling(latest.Cooling, stale);
        RenderTray(latest.Telemetry, latest.Cooling, stale);
        ShowAlerts(latest);
        RefreshHistory();
    }

    void RenderSystem(Snapshot snapshot, bool stale)
    {
        var state = snapshot.Cooling.State;
        if (driverAccess.IsVisible)
        {
            string access = snapshot.Telemetry.Devices.Any(device => device.Type == "SuperIO") ? "Датчики Super I/O обнаружены." : "Датчики Super I/O не обнаружены.";
            string detail = $"{access} {snapshot.Cooling.Reason}";
            if (driverAccess.Text != detail) driverAccess.Text = detail;
        }
        string heading = state switch
        {
            ChannelState.Conflict => "Конфликт управления",
            ChannelState.Restoring => "Возврат BIOS/EC",
            ChannelState.Error when snapshot.Cooling.NeedsRecovery => "Нужно проверить охлаждение",
            ChannelState.Error => "Ошибка управления",
            _ when viewModel.Failure != null => "Опрос остановлен",
            _ when stale => "Опрос не отвечает",
            _ when snapshot.PollIssue != null => "Часть датчиков недоступна",
            ChannelState.Controlled => "Управление активно",
            ChannelState.Unverified or ChannelState.Unavailable => "Требуется настройка",
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
        status.Text = state is ChannelState.Conflict or ChannelState.Restoring or ChannelState.Error ? FriendlyStatus(snapshot.Cooling) :
            viewModel.Failure is { } failure ? failure :
            stale ? "Свежих показаний нет. Проверьте время опроса и состояние драйвера в диагностике." :
            snapshot.PollIssue != null ? "Доступные показатели продолжают обновляться. Откройте диагностику, чтобы найти проблемный датчик." :
            autostartSession && state == ChannelState.MonitorOnly ? "Автозапуск работает в режиме мониторинга. Для настройки охлаждения откройте AirDesk вручную." : FriendlyStatus(snapshot.Cooling);
        statusHeading.ToolTip = status.Text;
        string lastPoll = DateTimeOffset.TryParse(snapshot.Cooling.LastPollUtc, out var time) ? time.ToLocalTime().ToString("HH:mm:ss") : "—";
        statusTime.Text = $"Последний опрос: {lastPoll}\n{snapshot.Cooling.Reason}" +
            (snapshot.PollIssue is { } issue ? $"\n{issue}" : "");
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
        ChannelState.Error => "AirDesk остановил регулирование. На вкладке «Охлаждение» указан результат возврата BIOS/EC.",
        _ => cooling.Reason
    };

    void Navigate(int index) => tabs.SelectedIndex = index;
    void ShowMain() { flyout?.Hide(); Show(); WindowState = WindowState.Normal; Activate(); Render(); }
    internal void ActivateFromSecondLaunch() { if (!quitting) ShowMain(); }
    void ShowPage(int index) { ShowMain(); Navigate(index); }

    internal void EndSession()
    {
        try { viewModel.ShutdownAsync().GetAwaiter().GetResult(); }
        catch (Exception error) { Monitor.Log(error); }
        finally { DisposeShell(); }
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode is PowerModes.Suspend or PowerModes.Resume)
            viewModel.PowerChanged(args.Mode == PowerModes.Suspend);
    }

    internal async Task ExitAsync(string? reason = null)
    {
        if (quitting) return;
        quitting = true;
        string? warning = reason;
        try
        {
            CoolingView? result = await viewModel.ShutdownAsync();
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
        DisposeFrames();
        StopSoak();
        updateRequest?.Cancel();
        refresh.Stop(); debounce.Stop();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        DisposeTray();
    }

    static string Format(float? value, string unit) => ReadingDisplay.Value(value, unit);
    static TextBlock Text(string text, double size)
    {
        var label = new TextBlock { Text = text, FontSize = size, Margin = new(0, 0, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, size <= 12 ? AppTheme.Muted : AppTheme.Text);
        return label;
    }
    static Button Button(string text, Action? action = null, string? tip = null)
    {
        var button = new Button { Content = text, Padding = new(10, 6, 10, 6), MinHeight = 32, Margin = new(0, 0, 8, 6), ToolTip = tip };
        if (action != null) button.Click += (_, _) => action();
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
}
