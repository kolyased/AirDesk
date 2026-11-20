using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace AirDesk;

public sealed partial class Dashboard
{
    readonly ComboBox deviceChoice = new() { MinWidth = 175, MaxWidth = 305 }, gpuChoice = new() { MinWidth = 165, MaxWidth = 285 };
    readonly DataGrid deviceTable = new(), diagnosticTable = new();
    readonly TextBlock detailTitle = Text("", 14), diagnosticStatus = Text("", 12);
    DeviceOption? preferredDevice;
    DeviceOption[] detailOptions = [];
    bool previousStale, updatingDeviceChoices;
    record DeviceOption(string? Id, string Label, string? InstanceId = null, bool Missing = false, string Group = "")
    {
        public override string ToString() => Label;
    }
    record ReadingRow(string? Key, string Name, string Value, string Quality, string Time, string Original, string Source,
        string Section = "", string? AliasKey = null, string Advice = "")
    {
        public string Hint => $"Исходное имя: {Original}\nИсточник: {Source}\nКачество: {Quality}" +
            (Key == null ? "" : $"\nID: {Key}") + (Advice.Length == 0 ? "" : $"\n{Advice}");
    }

    FrameworkElement BuildDevices()
    {
        var panel = new DockPanel { Margin = new(0, 12, 0, 8) };
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head);
        var controls = new WrapPanel(); head.Children.Add(controls);
        NameForAutomation(deviceChoice, "Устройство для подробного просмотра");
        deviceChoice.GroupStyle.Add(SectionHeaders());
        NameForAutomation(gpuChoice, "Видеокарта на вкладке обзора");
        controls.Children.Add(deviceChoice);
        controls.Children.Add(Button("Переименовать устройство", RenameDevice));
        controls.Children.Add(Button("Переименовать датчик", () => RenameReading(deviceTable)));
        var gpuRow = new WrapPanel(); head.Children.Add(gpuRow);
        gpuRow.Children.Add(Text("Видеокарта в обзоре    ", 14)); gpuRow.Children.Add(gpuChoice);
        head.Children.Add(detailTitle);
        detailTitle.TextWrapping = TextWrapping.Wrap;
        deviceChoice.SelectionChanged += (_, _) =>
        {
            if (updatingDeviceChoices) return;
            preferredDevice = deviceChoice.SelectedItem as DeviceOption;
            RefreshDevice();
        };
        gpuChoice.SelectionChanged += (_, _) =>
        {
            if (updatingDeviceChoices || displayed is not { } data || gpuChoice.SelectedItem is not DeviceOption { Missing: false } option) return;
            try { preferences.SelectGpu(option.Id == null ? null : data.PrimaryGpu(option.Id, option.InstanceId)); }
            catch (Exception error) { ShowPreferenceError(error); }
            RenderTelemetry(data, previousStale);
        };
        ConfigureReadings(deviceTable, false); panel.Children.Add(deviceTable);
        panel.IsVisibleChanged += (_, _) => { if (panel.IsVisible) RefreshDevice(); };
        return panel;
    }

    FrameworkElement BuildDiagnostics()
    {
        var panel = new DockPanel { Margin = new(0, 12, 0, 8) };
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head);
        head.Children.Add(BuildDriverDiagnostics());
        diagnosticStatus.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(diagnosticStatus);
        var actions = new WrapPanel(); head.Children.Add(actions);
        actions.Children.Add(Button("Отчёт оборудования", Export));
        actions.Children.Add(Button("Переименовать датчик", () => RenameReading(diagnosticTable)));
        ConfigureReadings(diagnosticTable, true);
        panel.Children.Add(diagnosticTable);
        panel.IsVisibleChanged += (_, _) => { if (panel.IsVisible) RefreshDiagnostics(); };
        return panel;
    }

    static void ConfigureReadings(DataGrid table, bool raw)
    {
        table.IsReadOnly = true; table.AutoGenerateColumns = false; table.CanUserAddRows = false;
        table.SelectionMode = DataGridSelectionMode.Single; table.HeadersVisibility = DataGridHeadersVisibility.Column;
        table.SetResourceReference(BackgroundProperty, AppTheme.Surface);
        table.SetResourceReference(DataGrid.RowBackgroundProperty, AppTheme.Surface);
        table.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, AppTheme.Alternate);
        table.BorderThickness = new(0);
        NameForAutomation(table, raw ? "Все показания оборудования" : "Показания выбранного устройства");
        var rows = new Style(typeof(DataGridRow));
        rows.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(ReadingRow.Hint))));
        table.RowStyle = rows;
        table.GridLinesVisibility = DataGridGridLinesVisibility.None;
        table.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        var view = new ListCollectionView(new ObservableCollection<ReadingRow>());
        if (!raw)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ReadingRow.Section)));
            table.GroupStyle.Add(SectionHeaders());
        }
        table.ItemsSource = view;
        foreach (var (title, field, width) in new[] { ("Показатель", "Name", 230), ("Значение", "Value", 125),
            ("Статус", "Quality", 170), ("Обновлено", "Time", 95), ("Исходное имя", "Original", 155), (raw ? "Источник и ID" : "Источник", "Source", raw ? 360 : 175) })
            if (raw || field != "Original") table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(field), Width = width });
    }

    static GroupStyle SectionHeaders()
    {
        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        header.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        header.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 7, 5, 3));
        return new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } };
    }

    void RenderTelemetry(Telemetry data, bool stale)
    {
        var gpu = preferences.SelectedGpu(data);
        UpdateMonitorCards(data, gpu, stale);
        var newSources = data.CurveSources.Select(source => stale ? source with { Celsius = null } : source).ToArray();
        if (!sources.Select(source => (source.Source, source.ReadingId, source.Label, source.Celsius.HasValue))
            .SequenceEqual(newSources.Select(source => (source.Source, source.ReadingId, source.Label, source.Celsius.HasValue))))
        {
            sources = newSources;
            SetProfileFields();
        }
        UpdateDevices(data);
        if (deviceTable.IsVisible) RefreshDevice();
        if (diagnosticTable.IsVisible) RefreshDiagnostics();
    }

    static string CardHint(string first, Reading? firstReading, string second, Reading? secondReading, bool stale)
    {
        static string Describe(Reading? reading, bool stale) => reading == null ? "датчик недоступен" :
            $"{reading.Source} · {(stale ? "устарело" : QualityText(reading.Quality))}\n{reading.Id}";
        return $"{first}: {Describe(firstReading, stale)}\n{second}: {Describe(secondReading, stale)}";
    }

    void UpdateDevices(Telemetry data)
    {
        updatingDeviceChoices = true;
        try
        {
            var details = DeviceOptions(data.Devices);
            var previous = preferredDevice;
            var detailItem = previous?.InstanceId is { } instance
                ? details.FirstOrDefault(option => option.InstanceId == instance)
                : details.FirstOrDefault(option => option.Id == previous?.Id);
            if (detailItem == null && previous != null)
            {
                detailItem = previous with { Missing = true, Label = "Выбранное устройство недоступно" };
                details = [.. details, detailItem];
            }
            ReplaceDeviceOptions(details, detailItem ?? details.FirstOrDefault());

            DeviceOption automatic = new(null, "Автоматически");
            DeviceOption[] options = [automatic, .. DeviceOptions(data.Gpus)];
            DeviceOption gpuItem = automatic;
            if (preferences.Gpu is { } id)
            {
                if (preferences.SelectedGpu(data) is { } gpu) gpuItem = options.First(option => option.Id == gpu.Id);
                else
                {
                    gpuItem = new(id, $"{preferences.GpuName ?? id} · недоступна", preferences.GpuInstance, Missing: true);
                    options = [.. options, gpuItem];
                }
            }
            ReplaceOptions(gpuChoice, options, gpuItem);
        }
        finally { updatingDeviceChoices = false; }
    }

    DeviceOption[] DeviceOptions(DeviceReadings[] devices)
    {
        var names = devices.ToLookup(device => device.Name, StringComparer.CurrentCultureIgnoreCase);
        var labels = devices.ToLookup(device => preferences.DeviceLabel(device.Id, device.Name, device.InstanceId), StringComparer.CurrentCultureIgnoreCase);
        return devices.Select(device =>
        {
            string label = preferences.DeviceLabel(device.Id, device.Name, device.InstanceId);
            if (labels[label].Count() > 1 || names[device.Name].Count() > 1) label = $"{label} ({device.Id})";
            return new DeviceOption(device.Id, label, device.InstanceId, Group: DeviceGroup(device));
        }).ToArray();
    }

    static string DeviceGroup(DeviceReadings device) => device.Type switch
    {
        "Cpu" => "Процессоры", "Memory" => "Память", "Storage" => "Накопители", "Motherboard" or "SuperIO" => "Плата и охлаждение",
        _ when device.Gpu => "Видеокарты", _ => "Другие устройства"
    };

    void ReplaceDeviceOptions(DeviceOption[] options, DeviceOption? selected)
    {
        if (deviceChoice.ItemsSource == null || !detailOptions.SequenceEqual(options))
        {
            detailOptions = options;
            var view = new ListCollectionView(options);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceOption.Group)));
            deviceChoice.ItemsSource = view;
        }
        deviceChoice.SelectedItem = detailOptions.FirstOrDefault(option => option == selected);
    }

    static void ReplaceOptions(ComboBox box, DeviceOption[] options, DeviceOption? selected)
    {
        if (box.ItemsSource is DeviceOption[] previous && previous.SequenceEqual(options))
        {
            box.SelectedItem = previous.FirstOrDefault(option => option == selected);
            return;
        }
        box.ItemsSource = options;
        box.SelectedItem = selected;
    }

    void RefreshDevice()
    {
        var item = displayed?.Devices.FirstOrDefault(device => device.Id == (deviceChoice.SelectedItem as DeviceOption)?.Id);
        if (item == null || (deviceChoice.SelectedItem as DeviceOption)?.Missing == true)
        {
            detailTitle.Text = displayed?.Devices.Length == 0 ? "Оборудование ещё не обнаружено." : "Выбранное устройство сейчас недоступно.";
            UpdateTable(deviceTable, []);
            return;
        }
        detailTitle.Text = $"{preferences.DeviceLabel(item.Id, item.Name, item.InstanceId)}   ·   {item.Id}";
        var rows = item.Details().Select(metric => metric.Reading == null
            ? new ReadingRow(null, metric.Label, previousStale ? "—" : Format(metric.Derived, metric.Unit),
                metric.Derived.HasValue ? previousStale ? "Устарело" : "Расчёт" : QualityText(metric.Missing), "—", "—", "LibreHardwareMonitor",
                metric.Section, Advice: metric.Derived == null ? QualityAdvice(metric.Missing) : "")
            : Row(metric.Reading, metric.Label, false) with { Section = metric.Section }).ToList();
        if (item.Type == "SuperIO" && latest?.Cooling is { } cooling)
        {
            const string controls = "Управление AirDesk";
            string stamp = DateTimeOffset.TryParse(cooling.LastWriteUtc, out var confirmedAt) ? confirmedAt.ToLocalTime().ToString("HH:mm:ss") : "—";
            rows.InsertRange(0, [
                new(null, "Группа", cooling.Group ?? "—", cooling.Reason, "—", cooling.Channel ?? "—", "AirDesk", controls),
                new(null, "Датчик кривой", cooling.Source ?? "—", cooling.State.ToString(), "—", "—", "AirDesk", controls),
                new(null, "Один тахометр хаба", Format(previousStale ? null : cooling.Rpm, "об/мин"), cooling.State.ToString(), "—", cooling.Identity?.Tachometer ?? "—", "AirDesk", controls),
                new(null, "Задано", Format(cooling.Requested, "%"), cooling.State.ToString(), "—", cooling.Channel ?? "—", "AirDesk", controls),
                new(null, "Отправлено", Format(cooling.Sent, "%"), cooling.Command.HasValue ? "Команда подтверждена" : "—", stamp, cooling.Channel ?? "—", "AirDesk", controls)
            ]);
        }
        UpdateTable(deviceTable, rows);
    }

    void RefreshDiagnostics()
    {
        var cooling = latest?.Cooling;
        diagnosticStatus.Text = cooling?.Identity is { } id
            ? $"{id.BoardVendor} {id.Board} {id.Revision} · BIOS {id.Firmware} · {id.Driver} · LHM {id.Library} · определение {id.Definition}\n{id.Chip} · {id.Control} ↔ {id.Tachometer} · CPU {id.Temperature}"
            : cooling?.Reason ?? "Оборудование ещё не опрошено.";
        if (previousStale) diagnosticStatus.Text += "\nПоследний снимок устарел. Проверьте, возобновился ли опрос после сна Windows.";
        if (latest?.PollIssue is { } issue) diagnosticStatus.Text += $"\n{issue}";
        if (latest?.Polling is { } stats)
            diagnosticStatus.Text += $"\nОпрос {stats.LastMilliseconds:0.#} мс, среднее {stats.AverageMilliseconds:0.#}, пик {stats.PeakMilliseconds:0.#}. Циклы {stats.Polls}; пропущено {stats.MissedCycles}." +
                $" Устройств обновлено {stats.UpdatedDevices}, отложено {stats.DeferredDevices}, с ошибкой {stats.FailedDevices}.";
        UpdateTable(diagnosticTable, displayed?.Readings.Select(reading => Row(reading, $"{reading.Device} · {reading.Name}", true)).ToArray() ?? []);
    }

    static void UpdateTable(DataGrid table, IReadOnlyList<ReadingRow> rows)
    {
        if (table.ItemsSource is not ListCollectionView { SourceCollection: ObservableCollection<ReadingRow> existing }) return;
        var selected = table.SelectedItem as ReadingRow;
        for (int i = existing.Count; i > rows.Count; i--) existing.RemoveAt(i - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == existing.Count) existing.Add(rows[i]);
            else if (existing[i] != rows[i]) existing[i] = rows[i];
        }
        if (selected != null && table.SelectedItem == null)
            table.SelectedItem = existing.FirstOrDefault(row => row.Section == selected.Section &&
                (selected.Key == null ? row.Name == selected.Name : row.Key == selected.Key));
    }

    ReadingRow Row(Reading reading, string label, bool raw)
    {
        ReadingQuality quality = previousStale && reading.Quality is ReadingQuality.Fresh or ReadingQuality.Unverified ? ReadingQuality.Stale : reading.Quality;
        bool visible = quality is ReadingQuality.Fresh or ReadingQuality.Unverified;
        string aliasKey = displayed?.PersistentReference(reading).Id ?? reading.Id;
        string advice = QualityAdvice(quality);
        if (!visible && raw && reading.Value.HasValue && reading.UpdatedAt.HasValue)
            advice += $" Последнее измерение: {Format(reading.Value, reading.Unit)}.";
        return new(reading.Id, preferences.Label(aliasKey, preferences.Label(reading.Id, label)), Format(visible ? reading.Value : null, reading.Unit),
            QualityText(quality), reading.UpdatedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—",
            reading.Name, raw ? $"{reading.Source} · {reading.Id}" : reading.Source, AliasKey: aliasKey, Advice: advice);
    }

    static string ValueStatus(Reading? reading, bool stale, string unit) => stale ? "Данные устарели" :
        reading?.Available == true ? Format(reading.Value, unit) : reading == null ? "Датчик не найден" : QualityText(reading.Quality);

    static string QualityText(ReadingQuality value) => value switch
    {
        ReadingQuality.Fresh => "Актуально", ReadingQuality.NoSensor => "Нет измерения", ReadingQuality.Unsupported => "Нет поддержки",
        ReadingQuality.AccessDenied => "Нет прав", ReadingQuality.Unresponsive => "Не отвечает", ReadingQuality.Stale => "Устарело",
        ReadingQuality.Unverified => "Назначение не проверено", _ => "—"
    };

    static string QualityAdvice(ReadingQuality value) => value switch
    {
        ReadingQuality.NoSensor => "Устройство пока не передало значение этого датчика.",
        ReadingQuality.Unsupported => "Для этого устройства такой показатель недоступен через текущий источник.",
        ReadingQuality.AccessDenied => "Проверьте доступ Windows к драйверу на вкладке «Диагностика».",
        ReadingQuality.Unresponsive => "Опрос устройства завершился ошибкой. Подробности доступны в диагностике.",
        ReadingQuality.Stale => "Нового замера нет. Дождитесь следующего опроса.",
        ReadingQuality.Unverified => "Назначение входа ещё не подтверждено.", _ => ""
    };

    void RenameReading(DataGrid table)
    {
        if (table.SelectedItem is ReadingRow { Key: { } key } row) Alias(row.AliasKey ?? key, row.Name, key);
    }

    void RenameDevice()
    {
        if (deviceChoice.SelectedItem is not DeviceOption { Id: { } id } || displayed?.Devices.FirstOrDefault(device => device.Id == id) is not { } device) return;
        Alias(UserPreferences.DeviceAliasKey(id, device.InstanceId), preferences.DeviceLabel(id, device.Name, device.InstanceId), id);
    }

    void Alias(string key, string old, string? legacyKey = null)
    {
        string? name = Prompt("Отображаемое имя (пустое поле сбрасывает)", old, 60);
        if (name == null) return;
        try { preferences.SetAlias(key, name, legacyKey); }
        catch (Exception error) { ShowPreferenceError(error); return; }
        if (displayed != null) RenderTelemetry(displayed, previousStale);
        SetProfileFields();
    }

    void SavePreference()
    {
        try { preferences.Save(); }
        catch (Exception error) { ShowPreferenceError(error); }
    }

    void ShowPreferenceError(Exception error)
    {
        Monitor.Log(error);
        MessageBox.Show(this, "Не удалось сохранить настройки: " + error.Message, "AirDesk");
    }

    string ReportText() => JsonSerializer.Serialize(new
    {
        Captured = DateTimeOffset.Now, WindowVisible = IsVisible, DataStale = previousStale,
        Driver = startupDriver, Hardware = latest?.Cooling.Identity, Cooling = latest?.Cooling,
        Polling = latest?.Polling, Readings = displayed?.Readings.Select(reading => previousStale && reading.Quality is ReadingQuality.Fresh or ReadingQuality.Unverified
            ? reading with { Quality = ReadingQuality.Stale } : reading), PollIssue = latest?.PollIssue
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    void Export()
    {
        var window = Dialog("Предпросмотр отчёта оборудования", 770, 640, out StackPanel content);
        AddWrapped(content, "В отчёте есть названия и идентификаторы устройств, параметры платы и группы. Перед передачей другим людям удалите сведения, которыми не хотите делиться. Текст можно отредактировать.");
        var editor = new TextBox { Text = ReportText(), AcceptsReturn = true, AcceptsTab = true, Height = 445,
            FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(editor);
        content.Children.Add(Button("Сохранить отчёт…", () =>
        {
            var picker = new SaveFileDialog { Filter = "JSON|*.json|Текст|*.txt", FileName = "AirDesk-equipment.json" };
            if (picker.ShowDialog(window) != true) return;
            try
            {
                AtomicJson.RequireExternalDestination(picker.FileName);
                if (Path.GetExtension(picker.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var parsed = JsonDocument.Parse(editor.Text);
                }
                AtomicJson.WriteFile(picker.FileName, stream =>
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write(editor.Text);
                });
                window.DialogResult = true;
            }
            catch (JsonException) { MessageBox.Show(window, "Исправьте структуру JSON или выберите текстовый формат .txt.", "Отчёт оборудования"); }
            catch (Exception error) { Monitor.Log(error); MessageBox.Show(window, error.Message, "Не удалось сохранить отчёт"); }
        }));
        window.ShowDialog();
    }

    Window Dialog(string title, double width, double height, out StackPanel content)
    {
        content = new StackPanel { Margin = new(18) };
        var window = new Window { Title = title, Owner = this, Width = width, Height = height, MaxWidth = SystemParameters.WorkArea.Width,
            MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        window.SetResourceReference(BackgroundProperty, AppTheme.Surface);
        window.SetResourceReference(ForegroundProperty, AppTheme.Text);
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            window.DialogResult = false; e.Handled = true;
        };
        return window;
    }

    static TextBox Field(Panel panel, string title, string value, int max)
    {
        panel.Children.Add(Text(title, 12));
        var box = new TextBox { Text = value, MaxLength = max, Margin = new(0, 0, 0, 8) }; panel.Children.Add(box); return box;
    }

    static void AddWrapped(Panel panel, string description)
    {
        var text = Text(description, 14); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
    }

    string? Prompt(string title, string initial, int maxLength)
    {
        var window = Dialog(title, 460, 175, out StackPanel content);
        var input = Field(content, title, initial, maxLength);
        content.Children.Add(Button("Сохранить", () => window.DialogResult = true));
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
