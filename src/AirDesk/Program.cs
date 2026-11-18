using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AirDesk;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Keep the legacy guard while installations of 0.2 may still be in use.
        using var legacy = new Mutex(true, @"Local\AirDesk-0.1", out bool first);
        using var single = new Mutex(true, @"Local\AirDesk", out bool unique);
        if (!first || !unique) { MessageBox.Show("AirDesk уже работает. Откройте значок в трее."); return; }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Dashboard();
        app.MainWindow = window;
        app.DispatcherUnhandledException += async (_, error) =>
        {
            error.Handled = true;
            Monitor.Log(error.Exception);
            await window.ExitAsync("Ошибка интерфейса. AirDesk завершает управление.");
        };
        app.SessionEnding += (_, _) => window.EndSession();
        if (!args.Contains("--tray")) window.Show();
        window.Start(args.Contains("--smoke"));
        app.Run();
    }
}

public sealed partial class Dashboard : Window
{
    static readonly SolidColorBrush Bg = Brush("#10151E"), Panel = Brush("#19212E"), Muted = Brush("#9CAFC5"), Accent = Brush("#6DE0BD");
    readonly Monitor monitor = new();
    readonly UserPreferences preferences = UserPreferences.Load();
    readonly Forms.NotifyIcon tray;
    readonly TextBlock status = Text("Подключение к датчикам…", 12), flyText = Text("Подключение…", 15);
    readonly TextBlock cpuValue = Text("—", 34), ramValue = Text("—", 34), gpuValue = Text("—", 34), diskValue = Text("—", 34);
    readonly TextBlock cpuSub = Text("Процессор", 12), ramSub = Text("Память", 12), gpuSub = Text("Видеокарта", 12), diskSub = Text("Накопители", 12);
    readonly Queue<(DateTimeOffset Time, double Value)> history = new();
    readonly Polyline graph = new() { Stroke = Accent, StrokeThickness = 2 };
    readonly Canvas graphCanvas = new() { Margin = new(16, 28, 16, 10), ClipToBounds = true };
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Snapshot? latest;
    Telemetry? displayed;
    bool quitting, warnedStalled;
    Window? flyout;

    public Dashboard()
    {
        Title = "AirDesk — температура и охлаждение";
        Width = 1040; Height = 820; MinWidth = 760; MinHeight = 580;
        Background = Bg; Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new(24) };
        Content = root;
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var head = new DockPanel { Margin = new(0, 0, 0, 14) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var hide = Button("Свернуть в трей", Hide);
        DockPanel.SetDock(hide, Dock.Right); head.Children.Add(hide);
        head.Children.Add(Text("◉  AirDesk", 28));

        var tabs = new TabControl { Background = Bg, BorderThickness = new(0) };
        root.Children.Add(tabs);
        var body = new StackPanel { Margin = new(0, 14, 0, 8) };
        var overview = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        tabs.Items.Add(new TabItem { Header = "Обзор", Content = overview });
        var cards = GridCards();
        cards.Children.Add(Card("CPU", cpuValue, cpuSub)); cards.Children.Add(Card("RAM", ramValue, ramSub));
        cards.Children.Add(Card("GPU", gpuValue, gpuSub)); cards.Children.Add(Card("ДИСК", diskValue, diskSub));
        body.Children.Add(cards);
        var chart = new Grid { Height = 70, Background = Panel, Margin = new(0, 0, 8, 14) };
        chart.Children.Add(new TextBlock { Text = "ЗАГРУЗКА CPU · ПОСЛЕДНИЕ 60 СЕКУНД", FontSize = 10, Foreground = Muted, Margin = new(16, 8, 0, 0) });
        graphCanvas.Children.Add(graph); chart.Children.Add(graphCanvas); body.Children.Add(chart);
        graphCanvas.SizeChanged += (_, _) => DrawGraph();
        body.Children.Add(BuildCooling());
        tabs.Items.Add(new TabItem { Header = "Устройства", Content = BuildDevices() });
        tabs.Items.Add(new TabItem { Header = "Диагностика", Content = BuildDiagnostics() });

        tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Visible = true, Text = "AirDesk — мониторинг" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть AirDesk", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add("Вернуть управление BIOS/EC", null, (_, _) => monitor.Release());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(() => _ = ExitAsync()));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ToggleFlyout); };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
        Closing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        refresh.Tick += (_, _) => Render();
    }

    public void Start(bool smoke)
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
            if (monitor.Stalled) status.Text = "Мониторинг не отвечает. Проверьте драйвер и журнал AirDesk.";
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
        string lastPoll = DateTimeOffset.TryParse(latest.Cooling.LastPollUtc, out var time) ? time.ToLocalTime().ToString("HH:mm:ss") : "—";
        status.Text = stale ? "Данные устарели. Аппаратный исполнитель не отвечает." : latest.PollIssue ?? $"Данные обновлены в {lastPoll}";
        RenderCooling(latest.Cooling, stale);
        if (!ReferenceEquals(displayed, latest.Telemetry) || stale != previousStale)
        {
            bool newData = !ReferenceEquals(displayed, latest.Telemetry);
            displayed = latest.Telemetry;
            previousStale = stale;
            RenderTelemetry(displayed, stale, newData);
        }
    }

    void DrawGraph()
    {
        DateTimeOffset left = DateTimeOffset.Now.AddSeconds(-60);
        graph.Points = new PointCollection(history.Where(item => item.Time >= left).Select(item =>
            new Point((item.Time - left).TotalSeconds * graphCanvas.ActualWidth / 60, graphCanvas.ActualHeight * (1 - Math.Clamp(item.Value, 0, 100) / 100))));
    }

    void ShowMain() { flyout?.Hide(); Show(); WindowState = WindowState.Normal; Activate(); }

    void ToggleFlyout()
    {
        if (flyout?.IsVisible == true) { flyout.Hide(); return; }
        if (flyout == null)
        {
            flyout = new Window { Title = "AirDesk · кратко", Width = 330, Height = 285, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = Bg, Foreground = Brushes.White };
            var content = new StackPanel { Margin = new(22) };
            content.Children.Add(Text("◉  AirDesk", 22));
            flyText.TextWrapping = TextWrapping.Wrap; flyText.Margin = new(0, 12, 0, 12);
            content.Children.Add(flyText); content.Children.Add(Button("Открыть панель", ShowMain));
            flyout.Content = content; flyout.Deactivated += (_, _) => flyout.Hide();
        }
        var area = SystemParameters.WorkArea;
        flyout.Left = area.Right - flyout.Width - 12; flyout.Top = area.Bottom - flyout.Height - 12;
        flyout.Show(); flyout.Activate();
    }

    internal void EndSession()
    {
        try { monitor.ShutdownAsync().GetAwaiter().GetResult(); }
        catch (Exception error) { Monitor.Log(error); }
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
        refresh.Stop();
        if (warning != null) MessageBox.Show(warning, "AirDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        tray.Dispose(); flyout?.Close(); Application.Current.Shutdown();
    }

    static string Format(float? value, string unit) => value.HasValue ? $"{value:0.#} {unit}" : "—";
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static TextBlock Text(string text, double size) => new() { Text = text, FontSize = size, Foreground = size <= 12 ? Muted : Brushes.White, Margin = new(0, 0, 0, 5) };
    static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(12, 7, 12, 7), Margin = new(0, 0, 8, 8),
            Background = Brush("#29394A"), Foreground = Brushes.White, BorderThickness = new(0) };
        button.Click += (_, _) => action();
        return button;
    }
    static System.Windows.Controls.Primitives.UniformGrid GridCards() => new() { Columns = 4, Margin = new(0, 0, 0, 14) };
    static Border Card(string title, TextBlock value, TextBlock sub)
    {
        var content = new StackPanel(); content.Children.Add(Text(title, 11)); value.Foreground = Accent;
        content.Children.Add(value); sub.TextWrapping = TextWrapping.Wrap; content.Children.Add(sub);
        return new Border { Background = Panel, CornerRadius = new(12), Padding = new(15), Margin = new(0, 0, 8, 0), Child = content };
    }
}
