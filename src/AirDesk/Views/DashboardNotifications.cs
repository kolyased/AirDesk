using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AirDesk;

public sealed partial class Dashboard
{
    readonly ListBox thresholdList = new() { MaxHeight = 225, Margin = new(0, 4, 0, 7) };
    readonly TextBlock notificationLog = Text("", 12);
    DateTimeOffset nextNotification;
    string? lastCritical;
    bool hotCpu;
    sealed record RuleChoice(ThresholdRule Rule)
    {
        public override string ToString() => $"{Rule.Label} · {(Rule.Direction == ThresholdDirection.Above ? "≥" : "≤")} {Rule.Threshold:0.#} · {Rule.HoldSeconds} с";
    }
    sealed record RuleSensor(Reading Reading, string Label) { public override string ToString() => Label; }

    FrameworkElement BuildHistorySettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("История и уведомления", 19));
        AddWrapped(panel, "История хранится локально. При лимите 16 МиБ или 96 000 записей удаляются самые старые данные. Выключение записи сохраняет уже собранную историю.");
        settingsHistoryRecorded.TextWrapping = TextWrapping.Wrap;
        settingsHistoryRecorded.IsVisibleChanged += (_, _) => { if (settingsHistoryRecorded.IsVisible) RefreshHistory(force: true); };
        panel.Children.Add(settingsHistoryRecorded);
        panel.Children.Add(Text("Срок хранения", 13));
        var retention = new ComboBox { ItemsSource = new[] { "Выключено", "1 час", "1 день", "7 дней", "30 дней" },
            SelectedIndex = (int)preferences.Retention, Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 3, 0, 6) };
        NameForAutomation(retention, "Срок хранения истории");
        retention.SelectionChanged += (_, _) =>
        {
            if (retention.SelectedIndex < 0 || retention.SelectedIndex == (int)preferences.Retention) return;
            if (!SaveHistorySettings((HistoryRetention)retention.SelectedIndex, preferences.HistorySensors))
                retention.SelectedIndex = (int)preferences.Retention;
        };
        panel.Children.Add(retention);
        historySelectionInfo.TextWrapping = TextWrapping.Wrap;
        UpdateHistorySelectionInfo();
        panel.Children.Add(historySelectionInfo);
        var sensorSettings = Button("Датчики записи…", EditHistorySensors);
        sensorSettings.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(sensorSettings);
        panel.Children.Add(Text("Пороговые уведомления", 15));
        NameForAutomation(thresholdList, "Правила уведомлений");
        panel.Children.Add(thresholdList); LoadRuleChoices();
        var actions = new WrapPanel(); panel.Children.Add(actions);
        actions.Children.Add(Button("Добавить правило…", () => EditRule(null)));
        actions.Children.Add(Button("Изменить…", () => { if (thresholdList.SelectedItem is RuleChoice item) EditRule(item.Rule); }));
        actions.Children.Add(Button("Удалить правило", () =>
        {
            if (thresholdList.SelectedItem is RuleChoice item) SaveRules(preferences.Rules.Where(rule => rule.Id != item.Rule.Id).ToArray());
        }));
        notificationLog.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(notificationLog);
        AddWrapped(panel, "Пороговые правила управляют только уведомлениями. Возврат BIOS/EC и предупреждения охлаждения действуют независимо от этих правил.");
        return Surface(panel, new Thickness(14), new Thickness(0, 0, 0, 12));
    }

    void UpdateHistorySelectionInfo() => historySelectionInfo.Text = preferences.HistorySensors switch
    {
        null => "Датчики записи: по умолчанию",
        { Length: 0 } => "Датчики записи: не выбраны",
        var selection => $"Датчики записи: выбрано {selection.Length:N0}"
    };

    bool SaveHistorySettings(HistoryRetention retention, SensorReference[]? selection)
    {
        var previousRetention = preferences.Retention;
        var previousSelection = preferences.HistorySensors;
        preferences.Retention = retention;
        preferences.HistorySensors = selection;
        try { preferences.Save(); }
        catch (Exception error)
        {
            preferences.Retention = previousRetention;
            preferences.HistorySensors = previousSelection;
            Monitor.Log(error);
            MessageBox.Show(this, "Не удалось сохранить настройки истории: " + error.Message);
            return false;
        }
        viewModel.ConfigureHistory(retention, selection);
        UpdateHistorySelectionInfo();
        RefreshHistory(force: true);
        return true;
    }

    void EditHistorySensors()
    {
        var telemetry = latest?.Telemetry ?? Telemetry.Empty;
        var selection = (preferences.HistorySensors ?? telemetry.HistorySources.Select(telemetry.PersistentReference).ToArray()).ToHashSet();
        var labels = telemetry.HistoryCandidates.ToDictionary(telemetry.PersistentReference, reading =>
            HistorySensorLabel(telemetry.PersistentReference(reading), reading.Name, reading.Unit, reading.DeviceInstance) +
            (reading.Available ? "" : " · нет показаний"));
        foreach (var sensor in selection)
            labels.TryAdd(sensor, HistorySensorLabel(sensor, sensor.Id, "", null) + " · не обнаружен");
        var duplicates = labels.Values.GroupBy(label => label).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var window = Dialog("Датчики записи истории", 650, 570, out var panel);
        if (labels.Count == 0) AddWrapped(panel, "Датчики ещё не обнаружены. Список появится после опроса оборудования.");
        var list = new StackPanel();
        var checkboxes = new List<CheckBox>();
        var search = Field(panel, "Поиск по датчикам", "", 120);
        foreach (var item in labels.OrderBy(item => item.Value, StringComparer.CurrentCulture).ThenBy(item => item.Key.Id, StringComparer.Ordinal))
        {
            string label = duplicates.Contains(item.Value) ? $"{item.Value} · {item.Key.Id}" : item.Value;
            var checkbox = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                IsChecked = selection.Contains(item.Key), Tag = item.Key, ToolTip = item.Key.Id, Margin = new(0, 0, 0, 6) };
            NameForAutomation(checkbox, label);
            checkboxes.Add(checkbox);
            list.Children.Add(checkbox);
        }
        search.TextChanged += (_, _) =>
        {
            foreach (var checkbox in checkboxes)
                checkbox.Visibility = ((TextBlock)checkbox.Content).Text.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        var scroll = Scroll(list);
        scroll.Height = 330; scroll.Margin = new(0, 4, 0, 10);
        panel.Children.Add(scroll);
        var actions = new WrapPanel();
        panel.Children.Add(actions);
        actions.Children.Add(Button("Выбрать видимые", () => SetVisible(true)));
        actions.Children.Add(Button("Снять видимые", () => SetVisible(false)));
        actions.Children.Add(Button("По умолчанию", () =>
        {
            if (SaveHistorySettings(preferences.Retention, null)) window.DialogResult = true;
        }));
        panel.Children.Add(Button("Сохранить выбор", () =>
        {
            var selected = checkboxes.Where(item => item.IsChecked == true).Select(item => (SensorReference)item.Tag).ToArray();
            if (SaveHistorySettings(preferences.Retention, selected)) window.DialogResult = true;
        }));
        window.ShowDialog();

        void SetVisible(bool enabled)
        {
            foreach (var checkbox in checkboxes.Where(item => item.Visibility == Visibility.Visible))
                checkbox.IsChecked = enabled;
        }
    }

    void LoadRuleChoices()
    {
        thresholdList.ItemsSource = preferences.Rules.Select(rule => new RuleChoice(rule)).ToArray();
        if (preferences.Rules.Length != 0) thresholdList.SelectedIndex = 0;
    }

    bool SaveRules(ThresholdRule[] rules)
    {
        var previous = preferences.Rules;
        preferences.Rules = rules;
        try { preferences.Save(); viewModel.ConfigureRules(rules); LoadRuleChoices(); return true; }
        catch (Exception error)
        {
            preferences.Rules = previous;
            Monitor.Log(error);
            MessageBox.Show(this, "Не удалось сохранить правила: " + error.Message);
            return false;
        }
    }

    void EditRule(ThresholdRule? existing)
    {
        if (existing == null && preferences.Rules.Length >= 32)
        { MessageBox.Show(this, "Достигнут лимит 32 правил."); return; }
        var options = (latest?.Telemetry.Readings ?? []).Where(r => (r.Kind is "Temperature" or "Load" or "Fan") && r.Available)
            .DistinctBy(reading => reading.Reference).Select(reading => new RuleSensor(reading,
                $"{reading.Device} · {FriendlySource(reading.Name)} ({reading.Unit})")).ToList();
        if (existing != null && options.All(option => option.Reading.Reference != existing.Sensor))
        {
            var key = existing.Sensor;
            options.Add(new RuleSensor(new Reading(key.DeviceName, key.DeviceId, "", existing.Label, "", "", null, key.Id,
                ReadingQuality.NoSensor, null), existing.Label + " · сейчас недоступен"));
        }
        if (options.Count == 0) { MessageBox.Show(this, "Дождитесь показаний подходящего датчика."); return; }
        var window = Dialog("Пороговое уведомление", 545, 540, out var panel);
        panel.Children.Add(Text("Датчик", 12));
        var sensor = new ComboBox { ItemsSource = options, SelectedIndex = 0, Margin = new(0, 0, 0, 8) };
        NameForAutomation(sensor, "Датчик порогового уведомления"); panel.Children.Add(sensor);
        if (existing != null) sensor.SelectedItem = options.First(option => option.Reading.Reference == existing.Sensor);
        var direction = new ComboBox { ItemsSource = new[] { "При достижении или превышении", "При достижении или снижении" },
            SelectedIndex = existing == null ? 0 : (int)existing.Direction, Margin = new(0, 0, 0, 8) };
        NameForAutomation(direction, "Направление порога"); panel.Children.Add(direction);
        var threshold = Field(panel, "Порог в единицах датчика", existing?.Threshold.ToString() ?? "80", 16);
        var hysteresis = Field(panel, "Гистерезис в единицах датчика", existing?.Hysteresis.ToString() ?? "3", 16);
        var hold = Field(panel, "Удержание порога, 0–1800 с", existing?.HoldSeconds.ToString() ?? "5", 4);
        var quiet = Field(panel, "Минимальный интервал между уведомлениями, 0–86400 с", existing?.QuietSeconds.ToString() ?? "120", 5);
        var feedback = Text("", 12); panel.Children.Add(feedback);
        panel.Children.Add(Button("Сохранить правило", () =>
        {
            if (sensor.SelectedItem is not RuleSensor source || !float.TryParse(threshold.Text, out float boundary) ||
                !float.TryParse(hysteresis.Text, out float band) || !int.TryParse(hold.Text, out int sustained) || !int.TryParse(quiet.Text, out int pause))
            { feedback.Text = "Выберите датчик и заполните числовые поля."; return; }
            string label = $"{source.Reading.Device} · {FriendlySource(source.Reading.Name)}";
            if (existing?.Sensor == source.Reading.Reference) label = existing.Label;
            var rule = new ThresholdRule(existing?.Id ?? Guid.NewGuid(), source.Reading.Reference, label[..Math.Min(label.Length, 160)],
                (ThresholdDirection)direction.SelectedIndex, boundary, band, sustained, pause);
            if (!rule.Valid || source.Reading.Kind == "Load" && boundary is < 0 or > 100 || source.Reading.Kind == "Fan" && boundary < 0)
            { feedback.Text = "Проверьте диапазон порога. Гистерезис должен быть положительным."; return; }
            if (SaveRules(preferences.Rules.Where(item => item.Id != rule.Id).Append(rule).ToArray()))
                window.DialogResult = true;
        }));
        window.ShowDialog();
    }

    void ShowAlerts(Snapshot state)
    {
        if (!previousStale && state.Telemetry.Cpu?.Temperature is { Available: true, Value: float cpu })
            hotCpu = cpu >= FanPolicy.EmergencyTemperature || hotCpu && cpu > FanPolicy.EmergencyReleaseTemperature;
        else hotCpu = false;
        string? critical = state.Cooling.NeedsRecovery ? "Возврат BIOS/EC не подтверждён. Проверьте охлаждение." :
            state.Cooling.State == ChannelState.Error ? state.Cooling.Reason : hotCpu ? "Температура процессора достигла порога защиты. Проверьте охлаждение." : null;
        if (critical != null && critical != lastCritical)
            tray.ShowBalloonTip(5000, "AirDesk · охлаждение", critical, System.Windows.Forms.ToolTipIcon.Warning);
        lastCritical = critical;
        if (critical != null || DateTimeOffset.Now < nextNotification) return;
        string[] pending = viewModel.TakeAlerts();
        if (pending.Length == 0) return;
        notificationLog.Text = $"Последнее уведомление в {DateTimeOffset.Now:HH:mm}: {pending[^1]}";
        tray.ShowBalloonTip(5000, "AirDesk · пороги", string.Join("\n", pending.Take(3)), System.Windows.Forms.ToolTipIcon.Info);
        nextNotification = DateTimeOffset.Now.AddSeconds(15);
    }

    async Task ResetPreferences()
    {
        if (latest?.Cooling is { CanRelease: true } or { State: ChannelState.Restoring })
        { MessageBox.Show(this, "Сначала остановите управление и подтвердите возврат BIOS/EC."); return; }
        if (MessageBox.Show(this, "Темы, названия, профили и правила уведомлений будут сброшены. История и привязка оборудования сохранятся. Сначала сохраните копию настроек.",
            "Сброс настроек", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var save = new SaveFileDialog { Filter = "JSON|*.json", FileName = "AirDesk-settings-backup.json" };
        if (save.ShowDialog(this) != true) return;
        if (viewModel.History.OwnsPath(save.FileName)) { MessageBox.Show(this, "Выберите другой файл для копии настроек."); return; }
        try
        {
            preferences.Export(save.FileName);
            preferences.Reset();
            MessageBox.Show(this, "Копия сохранена. AirDesk закроется. При следующем запуске загрузятся настройки по умолчанию.");
            await ExitAsync();
        }
        catch (Exception error) { Monitor.Log(error); MessageBox.Show(this, "Сброс не выполнен: " + error.Message); }
    }
}
