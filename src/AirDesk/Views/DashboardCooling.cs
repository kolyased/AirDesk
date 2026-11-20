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
        values = Text("", 13), actionStatus = Text("", 13), restoreStatus = Text("", 13), requestError = Text("", 13),
        requestStatus = Text("", 13), requestDetail = Text("", 12), restoreDetail = Text("", 12),
        curveTitle = Text("", 15), curveSource = Text("", 13), curveFloor = Text("", 12);
    readonly Expander actionDetails = new() { Header = "Подробности операции", Visibility = Visibility.Collapsed };
    readonly DashboardCurvePlot curvePlot = new();
    readonly ComboBox profileChoice = new() { Width = 185 }, primaryChoice = new() { Width = 246 }, secondaryChoice = new() { Width = 246 };
    readonly Slider speed = new() { Minimum = 0, Maximum = 100, Width = 155, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly TextBlock speedLabel = Text("", 14);
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(230) };
    Button saveCalibration = null!, confirm = null!, enable = null!, recovery = null!, emergencyRelease = null!,
        editCurve = null!, previous = null!, startup = null!, lastConfirmed = null!;
    MenuItem calibrate = null!, release = null!, rename = null!;
    FanProfile selected = FanProfile.Default;
    FanProfile? plottedProfile;
    CurvePoint[]? plottedPoints;
    Action<CoolingView, bool>? refreshCurveEditor;
    ChannelIdentity? channel;
    TemperatureInput[] sources = [];
    bool editingControls, dragging, commandPending;

    FrameworkElement BuildCooling()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Управление охлаждением", 19));
        banner.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.Warning);
        requestError.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.Warning);
        values.ToolTip = "Заданный процент — цель профиля. Отправленный процент — команда разъёму. Обороты измеряются тахометром и могут изменяться с задержкой или оставаться прежними на пределе вентилятора.";
        foreach (var line in new[] { banner, channelStatus, values })
        {
            line.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(line);
        }
        var tools = new WrapPanel { Margin = new(0, 8, 0, 0) };
        calibrate = MenuAction("Калибровка разъёма…", Calibrate, "Подтверждение подключения и проверка отклика вентиляторов.");
        saveCalibration = Button("Подтвердить назначение", () =>
        {
            if (MessageBox.Show(this, "Вы подтвердили подключение и устойчивое вращение всей группы на минимуме? BIOS/EC снова регулирует обороты после теста?",
                "Сохранить привязку", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _ = Execute(viewModel.SaveCalibration, "Не удалось сохранить калибровку. Проверьте результат теста и возврат BIOS/EC.");
        });
        confirm = Button("Подтвердить прежнее подключение", () =>
        {
            if (MessageBox.Show(this, "Хаб, порт, PWM/DC и состав вентиляторов совпадают с сохранённой привязкой? При изменениях выполните калибровку заново.",
                "Проверка подключения", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _ = Execute(viewModel.ConfirmTopology, "Не удалось подтвердить подключение. Проверьте привязку и доступность датчиков.");
        });
        enable = Button("Включить управление", () => _ = EnableAsync());
        void Restore()
        {
            debounce.Stop();
            viewModel.Release();
            ShowCoolingAction("Возврат BIOS/EC запрошен. Дождитесь подтверждения ниже.");
        }
        release = MenuAction("Вернуть управление BIOS/EC", Restore, "Прекращает регулировку AirDesk или отменяет текущую калибровку.");
        emergencyRelease = Button("Остановить управление", Restore, "Возвращает разъём к контролю BIOS/EC.");
        recovery = Button("Подтвердить проверку BIOS/EC", () =>
        {
            if (MessageBox.Show(this, "Подтвердите, что после перезагрузки вы проверили в BIOS/EC работу вентиляторов и автономный режим.",
                "Восстановление после сбоя", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _ = Execute(viewModel.AcknowledgeRecovery, "Не удалось снять блокировку. Проверьте состояние BIOS/EC и разъёма.");
        });
        rename = MenuAction("Переименовать группу…", () =>
        {
            string? name = Prompt("Название группы", latest?.Cooling.Group ?? "", 48);
            if (name != null) _ = Execute(() => viewModel.Rename(name), "Не удалось переименовать группу. Проверьте доступность сохранённой привязки.");
        }, "Меняет название группы в обзоре, трее и диагностике.");
        var actions = new ContextMenu { Placement = PlacementMode.Bottom };
        var extra = Button("Дополнительно ▾", () => actions.IsOpen = true, "Калибровка, имя группы, возврат BIOS/EC и отчёт оборудования.");
        actions.PlacementTarget = extra;
        foreach (var entry in new[] { calibrate, rename, release, MenuAction("Отчёт оборудования…", Export, "Открывает редактируемый отчёт перед сохранением.") })
            actions.Items.Add(entry);
        extra.ContextMenu = actions;
        foreach (var button in new[] { enable, saveCalibration, confirm, recovery, emergencyRelease, extra }) tools.Children.Add(button);
        panel.Children.Add(tools);

        var profiles = new WrapPanel();
        profileChoice.ItemsSource = Enum.GetValues<ProfileKind>().Select(ProfileLabel).ToArray();
        NameForAutomation(profileChoice, "Профиль охлаждения");
        NameForAutomation(speed, "Ручная скорость вентилятора в процентах");
        NameForAutomation(primaryChoice, "Основной датчик температуры");
        NameForAutomation(secondaryChoice, "Дополнительный датчик температуры");
        foreach (var element in new FrameworkElement[] { profileChoice, speed, speedLabel }) { element.Margin = new(0, 0, 10, 8); profiles.Children.Add(element); }
        editCurve = Button("Редактор кривой", EditCurve);
        previous = Button("Предыдущий профиль", () =>
        {
            if (channel == null || preferences.Find(channel)?.Previous is not { } prior) return;
            _ = ChangeProfile(prior);
        });
        lastConfirmed = Button("Последний подтверждённый", () =>
        {
            if (channel != null && preferences.Find(channel)?.LastConfirmed is { } confirmed) _ = ChangeProfile(confirmed);
        });
        startup = Button("Сделать профилем по умолчанию", () =>
        {
            if (channel == null) return;
            try
            {
                preferences.SetStartupProfile(channel, selected);
                ShowCoolingAction("Профиль по умолчанию сохранён. Следующий сеанс начнётся с мониторинга.");
            }
            catch (Exception error)
            {
                Monitor.Log(error);
                ShowCoolingAction("Не удалось сохранить профиль по умолчанию. Проверьте доступ к папке настроек.", error: true, error.Message);
            }
        });
        foreach (var button in new[] { editCurve, previous, lastConfirmed, startup }) profiles.Children.Add(button);
        panel.Children.Add(profiles);
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
        var result = new StackPanel { Margin = new(0, 10, 0, 0) };
        result.Children.Add(Text("Последнее действие", 15));
        foreach (var line in new[] { requestStatus, requestError, actionStatus, restoreStatus, requestDetail, restoreDetail })
        {
            line.TextWrapping = TextWrapping.Wrap;
            line.Visibility = Visibility.Collapsed;
        }
        foreach (var line in new[] { requestStatus, requestError, actionStatus, restoreStatus }) result.Children.Add(line);
        var details = new StackPanel();
        details.Children.Add(requestDetail);
        details.Children.Add(restoreDetail);
        actionDetails.Content = details;
        result.Children.Add(actionDetails);
        panel.Children.Add(result);
        var curve = new StackPanel { Margin = new(0, 12, 0, 0) };
        curve.Children.Add(curveTitle);
        curveSource.TextWrapping = curveFloor.TextWrapping = TextWrapping.Wrap;
        curve.Children.Add(curveSource);
        NameForAutomation(curvePlot, "График профиля охлаждения");
        curve.Children.Add(curvePlot);
        curve.Children.Add(CurveLegend());
        curve.Children.Add(curveFloor);
        panel.Children.Add(curve);
        SetProfileFields();
        RenderCurvePreview(null, stale: false);
        return Surface(panel, new Thickness(16), new Thickness(0, 12, 0, 0));
    }

    static void SetCoolingLine(TextBlock line, string next)
    {
        if (line.Text != next) line.Text = next;
        line.Visibility = next.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowCoolingAction(string status, bool error = false, string detail = "")
    {
        SetCoolingLine(requestStatus, error ? "" : status);
        SetCoolingLine(requestError, error ? status : "");
        SetCoolingLine(requestDetail, detail);
        actionDetails.IsExpanded = false;
        actionDetails.Visibility = (requestDetail.Text.Length > 0 || restoreDetail.Text.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task<bool> Execute(Func<Task<string>> command, string failure)
    {
        if (commandPending || quitting) return false;
        commandPending = true;
        ShowCoolingAction("Обработка команды…");
        try
        {
            string result = await command().WaitAsync(TimeSpan.FromSeconds(5));
            if (result.Length == 0) ShowCoolingAction("");
            else ShowCoolingAction(failure, error: true, result);
            return result.Length == 0;
        }
        catch (Exception error)
        {
            viewModel.Release();
            Monitor.Log(error);
            ShowCoolingAction(error is TimeoutException
                ? "Контроллер не ответил вовремя. Запрошен возврат BIOS/EC. Проверьте подтверждение ниже."
                : "Команда прервана. Запрошен возврат BIOS/EC. Проверьте подтверждение ниже.", error: true, error.Message);
            return false;
        }
        finally { commandPending = false; }
    }

    async Task EnableAsync()
    {
        if (channel is not { } key) return;
        FanProfile requested = selected;
        if (await Execute(() => viewModel.Enable(requested),
            "Не удалось включить управление. Проверьте состояние разъёма, выбранные датчики и возможный конфликт.")) SaveSelection(key, requested, confirmed: true);
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
        if (channel is not { } key || latest?.Cooling is not { } cooling || !CanEditCooling(cooling, previousStale))
        {
            SetProfileFields();
            return;
        }
        if (!Curve.Valid(profile))
        {
            ShowCoolingAction("Профиль не изменён. Проверьте порядок точек и выберите разные датчики.", error: true);
            SetProfileFields();
            return;
        }
        if (cooling.CanAdjust && !await Execute(() => viewModel.Adjust(profile),
            "Профиль не применён. Проверьте выбранные датчики и состояние управления."))
        {
            SetProfileFields();
            return;
        }
        if (!cooling.CanAdjust) ShowCoolingAction("Профиль выбран. Он начнёт действовать после включения управления.");
        SaveSelection(key, profile, confirmed: cooling.CanAdjust);
        SetProfileFields();
    }

    void SaveSelection(ChannelIdentity key, FanProfile profile, bool confirmed)
    {
        try { preferences.SetProfile(key, profile, confirmed); if (channel == key) selected = profile; }
        catch (Exception error)
        {
            Monitor.Log(error);
            if (confirmed) viewModel.Release();
            if (channel == key) selected = preferences.Find(key)?.Current ?? FanProfile.Default;
            ShowCoolingAction(confirmed
                ? "Профиль не сохранён. Запрошен возврат BIOS/EC. Проверьте подтверждение ниже."
                : "Профиль не сохранён. Проверьте доступ к папке настроек.", error: true, error.Message);
        }
    }

    record SourceOption(SensorReference? Reference, string Label) { public override string ToString() => Label; }

    void SetProfileFields()
    {
        editingControls = true;
        profileChoice.SelectedIndex = (int)selected.Kind;
        speed.Value = selected.Manual;
        speedLabel.Text = $"{speed.Value:0}%";
        LoadSources(primaryChoice, "Температура процессора · авто", selected.Primary);
        LoadSources(secondaryChoice, "Один датчик", selected.Secondary);
        editingControls = false;
        RenderCurvePreview(latest?.Cooling, previousStale);
    }

    void LoadSources(ComboBox box, string defaultLabel, SensorReference? selectedSource)
    {
        var choices = new List<SourceOption> { new(null, defaultLabel) };
        choices.AddRange(sources.Select(source => new SourceOption(source.Source,
            preferences.Label(source.Source.Id, preferences.Label(source.ReadingId ?? source.Source.Id, FriendlySource(source.Label))) +
            (source.Celsius.HasValue ? "" : " · нет данных"))));
        if (selectedSource != null && choices.All(choice => choice.Reference != selectedSource))
        {
            string label = selectedSource.DeviceId?.StartsWith("/gpu-", StringComparison.Ordinal) == true
                ? "Выберите датчик GPU заново" : "Сохранённый датчик недоступен";
            choices.Add(new(selectedSource, label));
        }
        box.ItemsSource = choices;
        box.SelectedItem = choices.First(choice => choice.Reference == selectedSource);
    }

    void RenderCooling(CoolingView cooling, bool stale)
    {
        if (channel != cooling.Identity)
        {
            channel = cooling.Identity;
            selected = channel == null ? FanProfile.Default : preferences.Find(channel)?.StartupSelection ?? FanProfile.Default;
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
        channelStatus.Text = $"{cooling.Group ?? "Разъём охлаждения"} · {mode}\n{cooling.Reason}";
        channelStatus.ToolTip = cooling.Channel ?? cooling.Reason;
        string confirmation = !stale && cooling.LastWriteUtc is { } stamp && DateTimeOffset.TryParse(stamp, out var time)
            ? time.ToLocalTime().ToString("HH:mm:ss") : "—";
        values.Text = $"Тахометр хаба: {Format(stale ? null : cooling.Rpm, "об/мин")} (один сигнал) · минимум: {Format(cooling.Floor, "%")}\n" +
            $"Задано: {Format(cooling.Requested, "%")} · отправлено: {Format(cooling.Sent, "%")} · подтверждение команды: {confirmation}" +
            (cooling.Source == null ? "" : $"\nИсточник кривой: {FriendlySource(cooling.Source)}");
        SetCoolingLine(actionStatus, cooling.Action);
        SetCoolingLine(restoreStatus, cooling.State == ChannelState.Restoring ? "Проверяется возврат управления BIOS/EC…" :
            cooling.BiosRestoreConfirmed switch
            {
                true => "Проверка возврата BIOS/EC завершена.",
                false => "Возврат BIOS/EC не подтверждён. Проверьте охлаждение в BIOS/EC.",
                _ => ""
            });
        SetCoolingLine(restoreDetail, cooling.Restore);
        actionDetails.Visibility = requestDetail.Text.Length > 0 || restoreDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        calibrate.IsEnabled = !stale && !commandPending && cooling.CanCalibrate;
        Toggle(saveCalibration, !stale && cooling.CanSave);
        Toggle(confirm, !stale && cooling.CanConfirm && cooling.State != ChannelState.Verified);
        Toggle(recovery, !stale && cooling.CanRecover);
        rename.IsEnabled = !stale && cooling.CanRename;
        enable.IsEnabled = !stale && !commandPending && cooling.CanEnable;
        release.IsEnabled = cooling.CanRelease || stale;
        Toggle(emergencyRelease, cooling.CanRelease || stale);
        bool canEdit = CanEditCooling(cooling, stale);
        profileChoice.IsEnabled = canEdit;
        editCurve.Content = canEdit ? "Редактор кривой…" : "Посмотреть кривые…";
        primaryChoice.IsEnabled = secondaryChoice.IsEnabled = canEdit && selected.Kind != ProfileKind.Manual;
        speed.IsEnabled = canEdit && selected.Kind == ProfileKind.Manual;
        speed.Visibility = speedLabel.Visibility = selected.Kind == ProfileKind.Manual ? Visibility.Visible : Visibility.Collapsed;
        var saved = channel == null ? null : preferences.Find(channel);
        previous.IsEnabled = canEdit && saved?.Previous != null;
        lastConfirmed.IsEnabled = canEdit && saved?.LastConfirmed is { } confirmed && !selected.SameSettings(confirmed);
        startup.IsEnabled = canEdit && !selected.SameSettings(saved?.Startup);
        RenderCurvePreview(cooling, stale);
        refreshCurveEditor?.Invoke(cooling, stale);
    }

    bool CanEditCooling(CoolingView cooling, bool stale) => !stale && !quitting && !commandPending && (cooling.CanEnable || cooling.CanAdjust);

    static TextBlock CurveLegend()
    {
        var line = Text("Пунктир — профиль. Сплошная линия учитывает проверенный минимум и тепловую защиту. Кружок — подтверждённая команда.", 12);
        line.TextWrapping = TextWrapping.Wrap;
        return line;
    }

    void RenderCurvePreview(CoolingView? cooling, bool stale)
    {
        var shown = cooling?.ActiveProfile ?? selected;
        if (!ReferenceEquals(shown, plottedProfile))
        {
            plottedProfile = shown;
            plottedPoints = CurvePreview.ProfilePoints(shown);
        }
        curveTitle.Text = $"{(cooling?.State == ChannelState.Controlled ? "Действует" : "Выбран профиль")} · {ProfileLabel(shown.Kind)}";
        float? floor = cooling?.VerifiedFloor;
        curvePlot.SetCurve(plottedPoints, floor);
        CurvePoint? live = !stale && cooling is { CurveCommandConfirmed: true, CurveTemperature: float temperature, Command: float output }
            ? new(temperature, output) : null;
        curvePlot.SetLivePoint(live, cooling?.TemperatureProtection == true);
        var input = !stale && displayed is { } data ? Curve.Resolve(shown, LiveValue(data.Cpu?.Temperature, false), data.CurveSources) : (null, "");
        if (live != null)
        {
            string protection = cooling!.TemperatureProtection ? $" · тепловая защита (CPU {Format(cooling.CpuTemperature, "°C")})" : "";
            curveSource.Text = $"{FriendlySource(cooling.Source ?? input.Item2)} · {Format(live.Celsius, "°C")} · команда {Format(live.Percent, "%")}{protection}";
        }
        else if (stale) curveSource.Text = "Показания источника устарели.";
        else if (shown.Kind == ProfileKind.Manual) curveSource.Text = "Ручная скорость · температура CPU используется для защиты.";
        else if (input.Item1.HasValue) curveSource.Text = $"{FriendlySource(input.Item2)} · {Format(input.Item1, "°C")}";
        else curveSource.Text = shown.Primary == null && shown.Secondary == null ? "Температура процессора · нет показаний" :
            "Выбранный источник температуры недоступен. Проверьте датчики профиля.";
        curveFloor.Text = floor.HasValue ? $"Проверенный минимум {Format(floor, "%")} · тепловая защита от {FanPolicy.EmergencyTemperature:0} °C."
            : $"Минимум группы появится после калибровки. Порог тепловой защиты {FanPolicy.EmergencyTemperature:0} °C.";
    }

    static void Toggle(Button button, bool visible) { button.IsEnabled = visible; button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }

    static MenuItem MenuAction(string label, Action handler, string tip)
    {
        var entry = new MenuItem { Header = label, ToolTip = tip };
        ToolTipService.SetShowOnDisabled(entry, true);
        entry.Click += (_, _) => handler();
        return entry;
    }

    static string FriendlySource(string source) => source.Replace("CPU Package", "Температура процессора", StringComparison.Ordinal)
        .Replace("Core Max", "Самое горячее ядро", StringComparison.Ordinal).Replace("Core Average", "Средняя температура ядер", StringComparison.Ordinal)
        .Replace("GPU Core", "Ядро видеокарты", StringComparison.Ordinal).Replace("GPU Hot Spot", "Горячая точка видеокарты", StringComparison.Ordinal);

    void Calibrate()
    {
        if (latest?.Cooling is not { CanCalibrate: true, Identity: { } identity }) return;
        var window = Dialog("Калибровка охлаждения", 585, 470, out StackPanel content);
        AddWrapped(content, $"Подтвердите подключение вентиляторов корпуса или радиатора. Помпа и неизвестные цепи остаются под контролем BIOS/EC. На измерения отведено до {CoolingSafety.TestDuration.TotalSeconds:0} секунд. Дождитесь результата возврата BIOS/EC.");
        var group = Field(content, "Название группы", "", 48);
        var topology = Field(content, "Разъём, модель хаба и подключённые вентиляторы", "", 150);
        var signal = new ComboBox { ItemsSource = new[] { "Неизвестен", "PWM", "DC" }, SelectedIndex = 0, Margin = new(0, 0, 0, 7) };
        content.Children.Add(Text("Тип сигнала по настройкам BIOS или документации", 12)); content.Children.Add(signal);
        var floor = Field(content, "Минимум запуска и устойчивого вращения всей группы в %", "", 3);
        var check = new CheckBox { Content = Text("Подключение проверено. Все вентиляторы запускаются и держат обороты на указанном минимуме.", 13),
            Margin = new(0, 5, 0, 12) };
        ((TextBlock)check.Content).TextWrapping = TextWrapping.Wrap;
        content.Children.Add(check);
        var error = Text("", 12); content.Children.Add(error);
        Calibration? result = null;
        content.Children.Add(Button("Начать контролируемый тест", () =>
        {
            if (check.IsChecked != true || signal.SelectedIndex < 1 || string.IsNullOrWhiteSpace(group.Text) || string.IsNullOrWhiteSpace(topology.Text) ||
                !float.TryParse(floor.Text, out float min) || min is < 40 or >= 100 || !float.IsFinite(min))
            { error.Text = "Подтвердите подключение, заполните поля и укажите проверенный минимум от 40% до значения ниже 100%."; return; }
            result = new(identity, group.Text.Trim(), topology.Text.Trim(), (SignalMode)signal.SelectedIndex, min);
            window.DialogResult = true;
        }));
        if (window.ShowDialog() == true && result is { } proposal) _ = Execute(() => viewModel.Calibrate(proposal),
            "Калибровка не началась. Проверьте, что датчики отвечают, а охлаждение сейчас регулирует BIOS/EC.");
    }

    void EditCurve()
    {
        bool canEdit = latest?.Cooling is { } cooling && CanEditCooling(cooling, previousStale);
        var window = BuildCurveEditor(canEdit, out var readResult);
        if (window.ShowDialog() == true && readResult() is { } result)
            _ = ChangeProfile(selected with { Kind = ProfileKind.Custom, Custom = result });
    }

    Window BuildCurveEditor(bool allowEditing, out Func<CurvePoint[]?> readResult)
    {
        var window = Dialog(allowEditing ? "Редактор кривой" : "Кривые охлаждения", 645, 640, out StackPanel content);
        AddWrapped(content, allowEditing ? "Выберите базовую кривую. Перетащите точку или измените её значения." :
            "Выберите профиль для просмотра. Управление можно настроить после проверки разъёма.");
        ProfileKind[] kinds = [ProfileKind.Quiet, ProfileKind.Balanced, ProfileKind.Performance, ProfileKind.Custom];
        var preset = new ComboBox { ItemsSource = kinds.Select(ProfileLabel).ToArray(), HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 155, Margin = new(0, 4, 0, 4) };
        preset.SelectedIndex = Math.Max(0, Array.IndexOf(kinds, selected.Kind == ProfileKind.Manual ? ProfileKind.Balanced : selected.Kind));
        NameForAutomation(preset, "Кривая для просмотра");
        content.Children.Add(preset);

        var plot = new DashboardCurvePlot { Height = 220 };
        NameForAutomation(plot, "Сигнал разъёма по температуре");
        content.Children.Add(plot);
        content.Children.Add(CurveLegend());
        var points = new StackPanel();
        content.Children.Add(new ScrollViewer { Content = points, MaxHeight = 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var validation = Text("", 13);
        validation.TextWrapping = TextWrapping.Wrap;
        validation.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.Warning);
        content.Children.Add(validation);
        var actions = new WrapPanel();
        content.Children.Add(actions);
        var rows = new List<(TextBox Celsius, TextBox Percent)>();
        int? draggedPoint = null;
        bool draggingUpdate = false;
        float? floor = latest?.Cooling.VerifiedFloor;
        bool editingAvailable = allowEditing;
        var add = Button("Добавить точку");
        var save = Button("Сохранить");
        if (allowEditing) { actions.Children.Add(add); actions.Children.Add(save); }

        CurvePoint[]? Read() => rows.All(row => float.TryParse(row.Celsius.Text, out _) && float.TryParse(row.Percent.Text, out _))
            ? rows.Select(row => new CurvePoint(float.Parse(row.Celsius.Text), float.Parse(row.Percent.Text))).ToArray() : null;
        readResult = Read;

        void Preview()
        {
            var working = Read();
            bool valid = Curve.ValidPoints(working);
            validation.Text = !valid ? "Задайте 2–12 точек с возрастающей температурой и неубывающим сигналом от 0 до 100%." :
                allowEditing && !editingAvailable ? "Редактор временно недоступен. Проверьте состояние охлаждения в основном окне." : "";
            validation.Visibility = validation.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            save.IsEnabled = editingAvailable && valid;
            add.IsEnabled = editingAvailable && valid && rows.Count < 12;
            plot.SetCurve(valid ? working : null, floor, showHandles: allowEditing);
        }

        void Populate(CurvePoint[] curve)
        {
            points.Children.Clear();
            rows.Clear();
            for (int i = 0; i < curve.Length; i++)
            {
                var point = curve[i];
                var row = new WrapPanel { Margin = new(0, 0, 0, 4) };
                var temperature = new TextBox { Width = 74, Text = point.Celsius.ToString(CultureInfo.CurrentCulture), IsReadOnly = !allowEditing };
                var percent = new TextBox { Width = 74, Text = point.Percent.ToString(CultureInfo.CurrentCulture), IsReadOnly = !allowEditing };
                NameForAutomation(temperature, $"Температура точки {i + 1}");
                NameForAutomation(percent, $"Сигнал точки {i + 1}");
                row.Children.Add(temperature); row.Children.Add(Text(" °C     ", 13));
                row.Children.Add(percent); row.Children.Add(Text(" %    ", 13));
                if (allowEditing) row.Children.Add(Button("Удалить", () =>
                {
                    if (!editingAvailable || rows.Count <= 2) return;
                    rows.Remove((temperature, percent));
                    points.Children.Remove(row);
                    Preview();
                }));
                rows.Add((temperature, percent));
                points.Children.Add(row);
                temperature.TextChanged += (_, _) => { if (!draggingUpdate) Preview(); };
                percent.TextChanged += (_, _) => { if (!draggingUpdate) Preview(); };
            }
            Preview();
        }

        void LoadPreset()
        {
            if (preset.SelectedIndex < 0) return;
            var kind = kinds[preset.SelectedIndex];
            Populate(kind == ProfileKind.Custom ? selected.Custom : Curve.DefaultPoints(kind));
        }
        preset.SelectionChanged += (_, _) => LoadPreset();
        LoadPreset();

        if (allowEditing)
        {
            add.Click += (_, _) =>
            {
                if (!editingAvailable || Read() is not { } data || !Curve.ValidPoints(data) || data.Length >= 12) return;
                int index = Enumerable.Range(0, data.Length - 1).MaxBy(i => data[i + 1].Celsius - data[i].Celsius);
                var middle = new CurvePoint((data[index].Celsius + data[index + 1].Celsius) / 2,
                    (data[index].Percent + data[index + 1].Percent) / 2);
                Populate(data.Take(index + 1).Append(middle).Concat(data.Skip(index + 1)).ToArray());
            };
            save.Click += (_, _) => { if (editingAvailable && Curve.ValidPoints(Read())) window.DialogResult = true; };
            plot.MouseLeftButtonDown += (_, e) =>
            {
                if (!editingAvailable || Read() is not { } data || !Curve.ValidPoints(data)) return;
                Point location = e.GetPosition(plot);
                int index = Enumerable.Range(0, data.Length).MinBy(i => (plot.Position(data[i]) - location).LengthSquared);
                if ((plot.Position(data[index]) - location).Length <= 12 && plot.CaptureMouse())
                {
                    draggedPoint = index;
                    e.Handled = true;
                }
            };
            plot.MouseMove += (_, e) =>
            {
                if (!editingAvailable || draggedPoint is not int index || Read() is not { } data) return;
                var position = plot.ValueAt(e.GetPosition(plot));
                var point = Curve.ConstrainPoint(data, index, position.Celsius, position.Percent);
                draggingUpdate = true;
                rows[index].Celsius.Text = point.Celsius.ToString("G", CultureInfo.CurrentCulture);
                rows[index].Percent.Text = point.Percent.ToString("G", CultureInfo.CurrentCulture);
                draggingUpdate = false;
                Preview();
            };
            plot.MouseLeftButtonUp += (_, _) => { draggedPoint = null; if (plot.IsMouseCaptured) plot.ReleaseMouseCapture(); };
            plot.LostMouseCapture += (_, _) => draggedPoint = null;
        }
        else actions.Children.Add(Button("Закрыть", window.Close));

        void RefreshEditor(CoolingView state, bool stale)
        {
            bool available = allowEditing && CanEditCooling(state, stale);
            if (available == editingAvailable && floor == state.VerifiedFloor) return;
            editingAvailable = available;
            floor = state.VerifiedFloor;
            Preview();
        }
        refreshCurveEditor = RefreshEditor;
        window.Closed += (_, _) => refreshCurveEditor = null;
        return window;
    }
}
