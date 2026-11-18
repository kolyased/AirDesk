using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AirDesk;

public sealed partial class Dashboard
{
    readonly TextBlock channelStatus = Text("Обнаружение оборудования…", 16), banner = Text("", 14),
        values = Text("", 13), actionStatus = Text("", 13), restoreStatus = Text("", 13), requestError = Text("", 13);
    readonly ComboBox profileChoice = new() { Width = 155 }, primaryChoice = new() { Width = 235 }, secondaryChoice = new() { Width = 235 };
    readonly Slider speed = new() { Minimum = 0, Maximum = 100, Width = 155, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly TextBlock speedLabel = Text("", 14);
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(230) };
    Button calibrate = null!, saveCalibration = null!, confirm = null!, enable = null!, release = null!, recovery = null!,
        editCurve = null!, rename = null!, previous = null!;
    FanProfile selected = FanProfile.Default;
    ChannelIdentity? channel;
    TemperatureInput[] sources = [];
    bool editingControls, dragging, commandPending;

    FrameworkElement BuildCooling()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Управление охлаждением", 19));
        banner.Foreground = Brush("#FFD197");
        requestError.Foreground = banner.Foreground;
        foreach (var line in new[] { banner, channelStatus, values, actionStatus, restoreStatus, requestError })
        {
            line.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(line);
        }
        var tools = new WrapPanel { Margin = new(0, 8, 0, 0) };
        calibrate = Button("Калибровка канала", Calibrate);
        saveCalibration = Button("Подтвердить назначение", () =>
        {
            if (MessageBox.Show(this, "По тесту и осмотру вы подтвердили, какие вентиляторы подключены, и проверили выбранный минимум для всей группы?",
                "Сохранить привязку", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _ = Execute(safety => safety.SaveCalibration());
        });
        confirm = Button("Подтвердить прежнее подключение", () =>
        {
            if (MessageBox.Show(this, "Хаб, порт, PWM/DC и состав вентиляторов совпадают с сохранённой привязкой? При изменениях выполните калибровку заново.",
                "Проверка подключения", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _ = Execute(safety => safety.ConfirmUnchangedTopology());
        });
        enable = Button("Включить управление", () => _ = EnableAsync());
        release = Button("Остановить · вернуть BIOS/EC", () => { debounce.Stop(); monitor.Release(); requestError.Text = "Запрошен возврат BIOS/EC."; });
        recovery = Button("Подтвердить проверку BIOS/EC", () =>
        {
            if (MessageBox.Show(this, "Подтвердите, что после перезагрузки вы проверили в BIOS/EC работу вентиляторов и автономный режим.",
                "Восстановление после сбоя", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _ = Execute(safety => safety.AcknowledgeRecovery());
        });
        rename = Button("Переименовать группу", () =>
        {
            string? name = Prompt("Название группы", latest?.Cooling.Group ?? "", 48);
            if (name != null) _ = Execute(safety => safety.Rename(name));
        });
        foreach (var button in new[] { calibrate, saveCalibration, confirm, enable, release, recovery, rename }) tools.Children.Add(button);
        panel.Children.Add(tools);

        var profiles = new WrapPanel();
        profileChoice.ItemsSource = new[] { "Тихий", "Сбалансированный", "Производительный", "Пользовательский", "Вручную" };
        foreach (var element in new FrameworkElement[] { profileChoice, speed, speedLabel }) { element.Margin = new(0, 0, 10, 8); profiles.Children.Add(element); }
        editCurve = Button("Редактор кривой", EditCurve);
        previous = Button("Предыдущий профиль", () =>
        {
            if (channel == null || preferences.Find(channel)?.Previous is not { } prior) return;
            _ = ChangeProfile(prior);
        });
        profiles.Children.Add(editCurve); profiles.Children.Add(previous); panel.Children.Add(profiles);
        var inputs = new WrapPanel();
        foreach (var element in new FrameworkElement[] { primaryChoice, secondaryChoice })
        {
            element.Margin = new(0, 0, 10, 6); inputs.Children.Add(element);
        }
        panel.Children.Add(inputs);
        profileChoice.SelectionChanged += (_, _) =>
        {
            if (editingControls || profileChoice.SelectedIndex < 0) return;
            debounce.Stop();
            _ = ChangeProfile(selected with { Kind = (ProfileKind)profileChoice.SelectedIndex });
        };
        primaryChoice.SelectionChanged += (_, _) => ChangeSources();
        secondaryChoice.SelectionChanged += (_, _) => ChangeSources();
        speed.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { dragging = true; debounce.Stop(); }));
        speed.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; QueueSpeed(); }));
        speed.ValueChanged += (_, _) => { speedLabel.Text = $"{speed.Value:0}%"; QueueSpeed(); };
        debounce.Tick += (_, _) => { debounce.Stop(); _ = ChangeProfile(selected with { Manual = (float)speed.Value }); };
        SetProfileFields();
        return new Border { Background = Panel, CornerRadius = new(12), Padding = new(16), Margin = new(0, 0, 8, 0), Child = panel };
    }

    async Task<bool> Execute(Action<CoolingSafety> command)
    {
        if (commandPending || quitting) return false;
        commandPending = true;
        requestError.Text = "Обработка команды…";
        try
        {
            string result = await monitor.Submit(command).WaitAsync(TimeSpan.FromSeconds(5));
            requestError.Text = result;
            return result.Length == 0;
        }
        catch (Exception error)
        {
            monitor.Release();
            Monitor.Log(error);
            requestError.Text = "Команда прервана. Запрошен возврат BIOS/EC.";
            return false;
        }
        finally { commandPending = false; }
    }

    async Task EnableAsync()
    {
        FanProfile requested = selected;
        await Execute(safety => safety.Enable(requested));
    }

    void QueueSpeed()
    {
        if (editingControls || dragging || !speed.IsEnabled || selected.Kind != ProfileKind.Manual) return;
        debounce.Stop(); debounce.Start();
    }

    void ChangeSources()
    {
        if (editingControls) return;
        _ = ChangeProfile(selected with { Primary = (primaryChoice.SelectedItem as SourceOption)?.Reference,
            Secondary = (secondaryChoice.SelectedItem as SourceOption)?.Reference });
    }

    async Task ChangeProfile(FanProfile profile)
    {
        if (channel == null || latest?.Cooling is not { } cooling || commandPending || monitor.Stalled ||
            (!cooling.CanEnable && !cooling.CanAdjust)) { SetProfileFields(); return; }
        if (!Curve.Valid(profile)) { requestError.Text = "Недопустимая кривая или повторяющиеся датчики."; SetProfileFields(); return; }
        if (cooling.CanAdjust && !await Execute(safety => safety.Adjust(profile))) { SetProfileFields(); return; }
        try { preferences.SetProfile(channel, profile); selected = profile; }
        catch (Exception error)
        {
            Monitor.Log(error);
            monitor.Release();
            selected = preferences.Find(channel)?.Current ?? FanProfile.Default;
            requestError.Text = "Не удалось сохранить профиль. Запрошен возврат BIOS/EC.";
        }
        SetProfileFields();
    }

    record SourceOption(SensorReference? Reference, string Label) { public override string ToString() => Label; }

    void SetProfileFields()
    {
        editingControls = true;
        profileChoice.SelectedIndex = (int)selected.Kind;
        speed.Value = selected.Manual;
        speedLabel.Text = $"{speed.Value:0}%";
        LoadSources(primaryChoice, "CPU Package · авто", selected.Primary);
        LoadSources(secondaryChoice, "Один датчик", selected.Secondary);
        editingControls = false;
    }

    void LoadSources(ComboBox box, string defaultLabel, SensorReference? selectedSource)
    {
        var choices = new List<SourceOption> { new(null, defaultLabel) };
        choices.AddRange(sources.Select(source => new SourceOption(source.Source,
            preferences.Label(source.Source.Id, source.Label) + (source.Celsius.HasValue ? "" : " · нет данных"))));
        if (selectedSource != null && choices.All(choice => choice.Reference != selectedSource)) choices.Add(new(selectedSource, "Сохранённый датчик недоступен"));
        box.ItemsSource = choices;
        box.SelectedItem = choices.First(choice => choice.Reference == selectedSource);
    }

    void RenderCooling(CoolingView cooling, bool stale)
    {
        if (channel != cooling.Identity)
        {
            channel = cooling.Identity;
            selected = channel == null ? FanProfile.Default : preferences.Find(channel)?.Current ?? FanProfile.Default;
            SetProfileFields();
        }
        banner.Text = cooling.Conflict ?? (stale ? "Управление недоступно. Аппаратный исполнитель не отвечает." : "");
        banner.Visibility = banner.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        string mode = cooling.State switch
        {
            ChannelState.Unavailable => "Недоступен", ChannelState.MonitorOnly => "Наблюдение", ChannelState.Unverified => "Нужна проверка",
            ChannelState.Verified => "Проверен", ChannelState.Controlled => "AirDesk", ChannelState.Error => "Ошибка",
            ChannelState.Conflict => "Конфликт", ChannelState.Restoring => "Возврат BIOS/EC", _ => "—"
        };
        channelStatus.Text = $"{cooling.Group ?? (cooling.Channel == null ? "Охлаждение" : "Fan #2")} · {mode}\n{cooling.Reason}";
        string confirmation = !stale && cooling.LastWriteUtc is { } stamp && DateTimeOffset.TryParse(stamp, out var time)
            ? time.ToLocalTime().ToString("HH:mm:ss") : "—";
        values.Text = $"Тахометр хаба: {Format(stale ? null : cooling.Rpm, "об/мин")} (один сигнал) · минимум: {Format(cooling.Floor, "%")}\n" +
            $"Задано: {Format(cooling.Requested, "%")} · отправлено: {Format(cooling.Sent, "%")} · подтверждение команды: {confirmation}" +
            (cooling.Source == null ? "" : $"\nИсточник кривой: {cooling.Source}");
        actionStatus.Text = cooling.Action;
        restoreStatus.Text = cooling.Restore;
        calibrate.IsEnabled = !stale && !commandPending && cooling.CanCalibrate;
        Toggle(saveCalibration, !stale && cooling.CanSave);
        Toggle(confirm, !stale && cooling.CanConfirm && cooling.State != ChannelState.Verified);
        Toggle(recovery, !stale && cooling.CanRecover);
        Toggle(rename, !stale && cooling.CanRename);
        enable.IsEnabled = !stale && !commandPending && cooling.CanEnable;
        release.IsEnabled = cooling.CanRelease || stale;
        bool canEdit = !stale && !commandPending && (cooling.CanEnable || cooling.CanAdjust);
        profileChoice.IsEnabled = editCurve.IsEnabled = canEdit;
        primaryChoice.IsEnabled = secondaryChoice.IsEnabled = canEdit && selected.Kind != ProfileKind.Manual;
        speed.IsEnabled = canEdit && selected.Kind == ProfileKind.Manual;
        previous.IsEnabled = canEdit && channel != null && preferences.Find(channel)?.Previous != null;
    }

    static void Toggle(Button button, bool visible) { button.IsEnabled = visible; button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }

    void Calibrate()
    {
        if (latest?.Cooling is not { CanCalibrate: true, Identity: { } identity }) return;
        var window = Dialog("Калибровка Fan #2", 585, 470, out StackPanel content);
        AddWrapped(content, "Проверьте порт и подключение. Выберите только вентиляторы корпуса или радиатора. Помпа и неизвестные цепи остаются под контролем BIOS/EC. Тест длится до 8 секунд: проверяет минимум, затем 100% по одному тахометру хаба.");
        var group = Field(content, "Название группы", "", 48);
        var topology = Field(content, "Разъём, модель хаба и подключённые вентиляторы", "", 150);
        var signal = new ComboBox { ItemsSource = new[] { "Неизвестен", "PWM", "DC" }, SelectedIndex = 0, Margin = new(0, 0, 0, 7) };
        content.Children.Add(Text("Тип сигнала по настройкам BIOS или документации", 12)); content.Children.Add(signal);
        var floor = Field(content, "Минимум запуска и устойчивого вращения всей группы в %", "100", 3);
        var check = new CheckBox { Content = "Подключение проверено. Все вентиляторы запускаются и держат обороты на указанном минимуме.",
            Foreground = Brushes.White, Margin = new(0, 5, 0, 12) };
        content.Children.Add(check);
        var error = Text("", 12); content.Children.Add(error);
        Calibration? result = null;
        content.Children.Add(Button("Начать контролируемый тест", () =>
        {
            if (check.IsChecked != true || signal.SelectedIndex < 1 || string.IsNullOrWhiteSpace(group.Text) || string.IsNullOrWhiteSpace(topology.Text) ||
                !float.TryParse(floor.Text, out float min) || min is < 40 or > 100 || !float.IsFinite(min))
            { error.Text = "Подтвердите подключение, заполните поля и укажите проверенный минимум."; return; }
            result = new(identity, group.Text.Trim(), topology.Text.Trim(), (SignalMode)signal.SelectedIndex, min);
            window.DialogResult = true;
        }));
        if (window.ShowDialog() == true && result is { } proposal) _ = Execute(safety => safety.BeginCalibration(proposal));
    }

    void EditCurve()
    {
        var window = Dialog("Кривая охлаждения", 570, 565, out StackPanel content);
        AddWrapped(content, $"Температура по горизонтали, скорость по вертикали. Минимум группы {Format(latest?.Cooling.Floor, "%")}; при 85 °C срабатывает 100%.");
        var canvas = new Canvas { Height = 145, Background = Bg, Margin = new(0, 4, 0, 8) };
        content.Children.Add(canvas);
        var points = new StackPanel();
        var scroll = new ScrollViewer { Content = points, MaxHeight = 215, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(scroll);
        var validation = Text("", 12); content.Children.Add(validation);
        var rows = new List<(TextBox Celsius, TextBox Percent)>();
        CurvePoint[]? Read() => rows.All(row => float.TryParse(row.Celsius.Text, out _) && float.TryParse(row.Percent.Text, out _))
            ? rows.Select(row => new CurvePoint(float.Parse(row.Celsius.Text), float.Parse(row.Percent.Text))).ToArray() : null;
        void Preview()
        {
            canvas.Children.Clear();
            var data = Read();
            if (!Curve.ValidPoints(data)) { validation.Text = "Нужны 2–12 точек, возрастающая температура и неубывающий процент (0–100)."; return; }
            validation.Text = "";
            double width = Math.Max(1, canvas.ActualWidth - 52), height = canvas.Height - 28;
            var graphPoints = Enumerable.Range(0, 101).Select(t =>
            {
                float duty = t >= 85 ? 100 : Math.Max(latest?.Cooling.Floor ?? 0, Curve.Interpolate(data!, t));
                return new Point(35 + t * width / 100, 5 + height * (1 - duty / 100));
            });
            canvas.Children.Add(new Polyline { Stroke = Accent, StrokeThickness = 2, Points = new PointCollection(graphPoints) });
            foreach (var (label, x, y) in new[] { ("100%", 2.0, 0.0), ("0%", 8.0, height), ("0 °C", 35.0, height + 6), ("100 °C", width - 1, height + 6) })
            { var text = Text(label, 10); Canvas.SetLeft(text, x); Canvas.SetTop(text, y); canvas.Children.Add(text); }
        }
        void Add(CurvePoint point)
        {
            if (rows.Count == 12) return;
            var row = new WrapPanel { Margin = new(0, 0, 0, 4) };
            var temperature = new TextBox { Width = 75, Text = point.Celsius.ToString(CultureInfo.CurrentCulture) };
            var percent = new TextBox { Width = 75, Text = point.Percent.ToString(CultureInfo.CurrentCulture) };
            row.Children.Add(temperature); row.Children.Add(Text(" °C     ", 13)); row.Children.Add(percent); row.Children.Add(Text(" %    ", 13));
            row.Children.Add(Button("Удалить", () => { rows.Remove((temperature, percent)); points.Children.Remove(row); Preview(); }));
            rows.Add((temperature, percent)); points.Children.Add(row);
            temperature.TextChanged += (_, _) => Preview(); percent.TextChanged += (_, _) => Preview();
        }
        void Populate(CurvePoint[] data) { points.Children.Clear(); rows.Clear(); foreach (var point in data) Add(point); Preview(); }
        Populate(selected.Custom);
        canvas.SizeChanged += (_, _) => Preview();
        var buttons = new WrapPanel(); content.Children.Add(buttons);
        buttons.Children.Add(Button("Добавить точку", () =>
        {
            var data = Read();
            if (!Curve.ValidPoints(data) || data!.Length >= 12) return;
            int index = Enumerable.Range(0, data.Length - 1).MaxBy(i => data[i + 1].Celsius - data[i].Celsius);
            var middle = new CurvePoint((data[index].Celsius + data[index + 1].Celsius) / 2, (data[index].Percent + data[index + 1].Percent) / 2);
            Populate(data.Take(index + 1).Append(middle).Concat(data.Skip(index + 1)).ToArray());
        }));
        buttons.Children.Add(Button("Сбросить", () => Populate(Curve.DefaultPoints(ProfileKind.Balanced))));
        CurvePoint[]? result = null;
        buttons.Children.Add(Button("Сохранить", () => { result = Read(); if (Curve.ValidPoints(result)) window.DialogResult = true; }));
        if (window.ShowDialog() == true && result != null) _ = ChangeProfile(selected with { Kind = ProfileKind.Custom, Custom = result });
    }
}
