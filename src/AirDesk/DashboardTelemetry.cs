using System;
using System.Collections.Generic;
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
    readonly ComboBox deviceChoice = new() { Width = 310 }, gpuChoice = new() { Width = 290 };
    readonly DataGrid deviceTable = new(), diagnosticTable = new();
    readonly DataGridTextColumn deviceSection = new() { Header = "Раздел", Binding = new Binding("Section"), Width = 145 };
    readonly TextBlock detailTitle = Text("", 14), diagnosticStatus = Text("", 12);
    bool previousStale, updatingDeviceChoices;
    record DeviceOption(string Id, string Label) { public override string ToString() => Label; }
    record ReadingRow(string? Key, string Name, string Value, string Quality, string Time, string Original, string Source, string Section = "");

    FrameworkElement BuildDevices()
    {
        var panel = new DockPanel { Margin = new(0, 12, 0, 8) };
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head);
        var controls = new WrapPanel(); head.Children.Add(controls);
        controls.Children.Add(deviceChoice);
        controls.Children.Add(Button("Переименовать устройство", () => { if (deviceChoice.SelectedItem is DeviceOption option) Alias(option.Id, option.Label); }));
        controls.Children.Add(Button("Переименовать датчик", () => RenameReading(deviceTable)));
        var gpuRow = new WrapPanel(); head.Children.Add(gpuRow);
        gpuRow.Children.Add(Text("Видеокарта в обзоре    ", 14)); gpuRow.Children.Add(gpuChoice);
        head.Children.Add(detailTitle);
        deviceChoice.SelectionChanged += (_, _) => { if (!updatingDeviceChoices) RefreshDevice(); };
        gpuChoice.SelectionChanged += (_, _) =>
        {
            if (updatingDeviceChoices || gpuChoice.SelectedItem is not DeviceOption option) return;
            preferences.Gpu = option.Id;
            SavePreference();
            if (displayed != null) RenderTelemetry(displayed, previousStale, false);
        };
        ConfigureReadings(deviceTable, false); panel.Children.Add(deviceTable);
        deviceTable.Columns.Insert(0, deviceSection);
        return panel;
    }

    FrameworkElement BuildDiagnostics()
    {
        var panel = new DockPanel { Margin = new(0, 12, 0, 8) };
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head);
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
        table.Background = Panel; table.RowBackground = Panel; table.AlternatingRowBackground = Brush("#202A38");
        table.Foreground = Brushes.White; table.BorderThickness = new(0);
        table.GridLinesVisibility = DataGridGridLinesVisibility.None;
        foreach (var (title, field, width) in new[] { ("Показатель", "Name", 230), ("Значение", "Value", 125),
            ("Статус", "Quality", 170), ("Обновлено", "Time", 95), ("Исходное имя", "Original", 155), (raw ? "Источник и ID" : "Источник", "Source", raw ? 360 : 175) })
            table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(field), Width = width });
    }

    void RenderTelemetry(Telemetry data, bool stale, bool newData)
    {
        var gpu = data.Gpus.FirstOrDefault(item => item.Id == preferences.Gpu) ?? data.Gpus.FirstOrDefault();
        float? Read(Reading? reading) => !stale && reading?.Available == true ? reading.Value : null;
        float? load = Read(data.Cpu?.Load), cpuTemperature = Read(data.Cpu?.Temperature), gpuTemperature = Read(gpu?.Temperature);
        cpuValue.Text = Format(load, "%"); cpuSub.Text = ValueStatus(data.Cpu?.Temperature, stale, "°C");
        gpuValue.Text = Format(Read(gpu?.Load), "%");
        gpuSub.Text = ValueStatus(gpu?.Temperature, stale, "°C") + (gpu == null ? "" : " · " + preferences.Label(gpu.Id, gpu.Name));
        ramValue.Text = Format(Read(data.Ram?.Load), "%");
        ramSub.Text = Read(data.Ram?.MemoryUsed) is float used && data.Ram?.MemoryTotal is float total ? $"{used:0.#} / {total:0.#} ГБ" : "Физическая память";
        var hottestDisk = data.Disks.Where(disk => Read(disk.Temperature).HasValue).MaxBy(disk => Read(disk.Temperature));
        diskValue.Text = Format(data.Disks.Select(disk => Read(disk.Load)).DefaultIfEmpty().Max(), "%");
        diskSub.Text = hottestDisk == null ? "Макс. активность · °C недоступна" : $"{Format(Read(hottestDisk.Temperature), "°C")} · {preferences.Label(hottestDisk.Id, hottestDisk.Name)}";
        if (newData && load.HasValue)
        {
            DateTimeOffset time = data.Cpu?.Load?.UpdatedAt ?? DateTimeOffset.Now;
            history.Enqueue((time, load.Value));
        }
        while (history.TryPeek(out var first) && DateTimeOffset.Now - first.Time > TimeSpan.FromSeconds(60)) history.Dequeue();
        DrawGraph();
        var cooling = latest?.Cooling;
        flyText.Text = $"CPU   {Format(load, "%")} · {Format(cpuTemperature, "°C")}\nGPU   {gpuValue.Text} · {Format(gpuTemperature, "°C")}\nRAM   {ramValue.Text}\n" +
            $"{cooling?.Group ?? "Тахометр"}   {Format(stale ? null : cooling?.Rpm, "об/мин")}\n" +
            (stale ? "Данные устарели" : cooling?.Conflict != null ? "Конфликт управления" : cooling?.State == ChannelState.Controlled ? "Управление AirDesk" : "Мониторинг");
        tray.Text = $"AirDesk | CPU {Format(cpuTemperature, "°C")} | GPU {Format(gpuTemperature, "°C")}";
        var newSources = data.CurveSources.Select(source => stale ? source with { Celsius = null } : source).ToArray();
        if (!sources.Select(source => (source.Source, source.Label, source.Celsius.HasValue))
            .SequenceEqual(newSources.Select(source => (source.Source, source.Label, source.Celsius.HasValue))))
        {
            sources = newSources;
            SetProfileFields();
        }
        UpdateDevices(data);
        RefreshDevice();
        if (diagnosticTable.IsVisible) RefreshDiagnostics();
    }

    void UpdateDevices(Telemetry data)
    {
        updatingDeviceChoices = true;
        ReplaceOptions(deviceChoice, data.Devices, (deviceChoice.SelectedItem as DeviceOption)?.Id);
        ReplaceOptions(gpuChoice, data.Gpus, preferences.Gpu);
        updatingDeviceChoices = false;
    }

    void ReplaceOptions(ComboBox box, DeviceReadings[] devices, string? selectedId)
    {
        var options = devices.Select(device => new DeviceOption(device.Id, preferences.Label(device.Id, device.Name))).ToArray();
        if (box.ItemsSource is DeviceOption[] previous && previous.SequenceEqual(options)) return;
        box.ItemsSource = options;
        box.SelectedItem = options.FirstOrDefault(item => item.Id == selectedId) ?? options.FirstOrDefault();
    }

    void RefreshDevice()
    {
        var item = displayed?.Devices.FirstOrDefault(device => device.Id == (deviceChoice.SelectedItem as DeviceOption)?.Id);
        if (item == null) { deviceTable.ItemsSource = null; return; }
        detailTitle.Text = $"{preferences.Label(item.Id, item.Name)}   ·   {item.Id}";
        deviceSection.Visibility = item.Type is "SuperIO" or "Motherboard" ? Visibility.Visible : Visibility.Collapsed;
        var rows = item.Details().Select(metric => metric.Reading == null
            ? new ReadingRow(null, metric.Label, previousStale ? "—" : Format(metric.Derived, metric.Unit),
                metric.Derived.HasValue ? previousStale ? "Устарело" : "Расчёт" : QualityText(metric.Missing), "—", "—", "LibreHardwareMonitor")
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
        if (previousStale) diagnosticStatus.Text += "\nПоследний снимок устарел.";
        UpdateTable(diagnosticTable, displayed?.Readings.Select(reading => Row(reading, $"{reading.Device} · {reading.Name}", true)).ToArray() ?? []);
    }

    static void UpdateTable(DataGrid table, IReadOnlyList<ReadingRow> rows)
    {
        string? selected = (table.SelectedItem as ReadingRow)?.Key;
        table.ItemsSource = rows;
        if (selected != null) table.SelectedItem = rows.FirstOrDefault(row => row.Key == selected);
    }

    ReadingRow Row(Reading reading, string label, bool raw)
    {
        ReadingQuality quality = previousStale && reading.Quality is ReadingQuality.Fresh or ReadingQuality.Unverified ? ReadingQuality.Stale : reading.Quality;
        bool visible = raw || quality is ReadingQuality.Fresh or ReadingQuality.Unverified;
        return new(reading.Id, preferences.Label(reading.Id, label), Format(visible ? reading.Value : null, reading.Unit),
            QualityText(quality), reading.UpdatedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—",
            reading.Name, raw ? $"{reading.Source} · {reading.Id}" : reading.Source);
    }

    static string ValueStatus(Reading? reading, bool stale, string unit) => stale ? "Данные устарели" :
        reading?.Available == true ? Format(reading.Value, unit) : reading == null ? "Датчик не найден" : QualityText(reading.Quality);

    static string QualityText(ReadingQuality value) => value switch
    {
        ReadingQuality.Fresh => "Актуально", ReadingQuality.NoSensor => "Нет показаний", ReadingQuality.Unsupported => "Не поддерживается",
        ReadingQuality.AccessDenied => "Нет прав", ReadingQuality.Unresponsive => "Не отвечает", ReadingQuality.Stale => "Устарело",
        ReadingQuality.Unverified => "Назначение не проверено", _ => "—"
    };

    void RenameReading(DataGrid table)
    {
        if (table.SelectedItem is ReadingRow { Key: { } key } row) Alias(key, row.Name);
    }

    void Alias(string key, string old)
    {
        string? name = Prompt("Отображаемое имя (пустое поле сбрасывает)", old, 60);
        if (name == null) return;
        if (name.Length == 0) preferences.Aliases.Remove(key);
        else preferences.Aliases[key] = name;
        SavePreference();
        if (displayed != null) RenderTelemetry(displayed, previousStale, false);
        SetProfileFields();
    }

    void SavePreference()
    {
        try { preferences.Save(); }
        catch (Exception error) { Monitor.Log(error); MessageBox.Show(this, "Не удалось сохранить настройки: " + error.Message, "AirDesk"); }
    }

    string ReportText() => JsonSerializer.Serialize(new
    {
        Captured = DateTimeOffset.Now, DataStale = previousStale, Hardware = latest?.Cooling.Identity, Cooling = latest?.Cooling,
        Readings = displayed?.Readings.Select(reading => previousStale && reading.Quality is ReadingQuality.Fresh or ReadingQuality.Unverified
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
                if (Path.GetExtension(picker.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var parsed = JsonDocument.Parse(editor.Text);
                }
                File.WriteAllText(picker.FileName, editor.Text);
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
        return new Window { Title = title, Owner = this, Width = width, Height = height, Background = Panel, Foreground = Brushes.White,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
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
