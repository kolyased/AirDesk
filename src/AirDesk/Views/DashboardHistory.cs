using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace AirDesk;

public sealed partial class Dashboard
{
    readonly ComboBox historyDevice = new() { MinWidth = 160, MaxWidth = 285 }, historySensor = new() { MinWidth = 155, MaxWidth = 285 },
        historyRange = new() { Width = 120 };
    readonly TextBlock historyInfo = Text("", 13), historyLeft = Text("", 12), historyRight = Text("", 12);
    readonly TextBlock historyRecorded = Text("", 13), settingsHistoryRecorded = Text("", 13), historySelectionInfo = Text("", 13);
    readonly Canvas historyPlot = new() { Height = 200, ClipToBounds = true };
    bool changingHistoryFilters;
    int lastHistoryRevision = -1;
    int lastHistorySettingsRevision = -1;
    HistoryPoint[] visibleHistory = [];
    sealed record HistoryChoice(SensorReference? Sensor, (string Id, string Name)? Device, string Label) { public override string ToString() => Label; }

    FrameworkElement BuildHistory()
    {
        var body = new StackPanel { Margin = new(0, 12, 0, 0) };
        var content = new StackPanel();
        content.Children.Add(Text("История показаний", 20));
        var filters = new WrapPanel { Margin = new(0, 2, 0, 10) };
        NameForAutomation(historyDevice, "Устройство в истории"); NameForAutomation(historySensor, "Датчик в истории");
        NameForAutomation(historyRange, "Период графика");
        historyRange.ItemsSource = new[] { "1 час", "1 день", "7 дней", "30 дней" };
        historyRange.SelectedIndex = 0;
        foreach (var filter in new[] { historyDevice, historySensor, historyRange })
        { filter.Margin = new(0, 0, 8, 5); filters.Children.Add(filter); }
        content.Children.Add(filters);
        historyPlot.SetResourceReference(Panel.BackgroundProperty, AppTheme.Background); content.Children.Add(historyPlot);
        var axis = new DockPanel { Margin = new(0, 5, 0, 0) };
        DockPanel.SetDock(historyRight, Dock.Right); axis.Children.Add(historyRight); axis.Children.Add(historyLeft); content.Children.Add(axis);
        historyInfo.TextWrapping = TextWrapping.Wrap; content.Children.Add(historyInfo);
        historyRecorded.TextWrapping = TextWrapping.Wrap; content.Children.Add(historyRecorded);
        var actions = new WrapPanel(); content.Children.Add(actions);
        actions.Children.Add(Button("Экспорт выборки…", () => _ = ExportHistory(visibleHistory, "AirDesk-history")));
        actions.Children.Add(Button("Очистить историю…", () => _ = ClearHistory()));
        actions.Children.Add(Button("Датчики записи…", EditHistorySensors));
        actions.Children.Add(Button("Настроить хранение", () => Navigate(SettingsTab)));
        historyDevice.SelectionChanged += (_, _) => { if (!changingHistoryFilters) RefreshHistory(force: true); };
        historySensor.SelectionChanged += (_, _) => { if (!changingHistoryFilters) RefreshHistory(force: true); };
        historyRange.SelectionChanged += (_, _) => RefreshHistory(force: true);
        historyPlot.SizeChanged += (_, _) => DrawHistory();
        historyPlot.IsVisibleChanged += (_, _) => { if (historyPlot.IsVisible) RefreshHistory(force: true); };
        body.Children.Add(Surface(content, new Thickness(14), new Thickness(0)));
        return body;
    }

    void RefreshHistory(bool force = false)
    {
        int revision = viewModel.History.Revision;
        if (settingsHistoryRecorded.IsVisible && (force || lastHistorySettingsRevision != revision))
        {
            lastHistorySettingsRevision = revision;
            string range = HistoryRangeText(viewModel.History.Read());
            settingsHistoryRecorded.Text = viewModel.History.Error is { } error ? $"{range}\n{error}" : range;
        }
        if (!historyPlot.IsVisible || !force && lastHistoryRevision == revision) return;
        lastHistoryRevision = revision;
        var all = viewModel.History.Read();
        var device = (historyDevice.SelectedItem as HistoryChoice)?.Device;
        var sensor = (historySensor.SelectedItem as HistoryChoice)?.Sensor;
        changingHistoryFilters = true;
        var devices = all.Select(p => (Id: p.Sensor.DeviceId, Name: p.Sensor.DeviceName)).Distinct()
            .Select(key => new HistoryChoice(null, key, preferences.DeviceLabel(key.Id, key.Name, HistoryDeviceInstance(key.Id, key.Name)) +
                (key.Id.StartsWith(Telemetry.GpuInstancePrefix, StringComparison.Ordinal) ? " · ID Windows" : "")))
            .OrderBy(item => item.Label, StringComparer.CurrentCulture).Prepend(new(null, null, "Все устройства")).ToArray();
        var selectedDevice = devices.FirstOrDefault(item => item.Device == device) ?? devices[0];
        historyDevice.ItemsSource = devices; historyDevice.SelectedItem = selectedDevice;
        var sensors = all.Where(p => selectedDevice.Device == null || (p.Sensor.DeviceId, p.Sensor.DeviceName) == selectedDevice.Device)
            .GroupBy(p => p.Sensor).Select(g =>
        {
            var point = g.Last();
            return new HistoryChoice(g.Key, (g.Key.DeviceId, g.Key.DeviceName), HistorySensorLabel(g.Key, point.Name, point.Unit,
                HistoryDeviceInstance(g.Key.DeviceId, g.Key.DeviceName)));
        }).OrderBy(item => item.Label, StringComparer.CurrentCulture).ToArray();
        historySensor.ItemsSource = sensors; historySensor.SelectedItem = sensors.FirstOrDefault(item => item.Sensor == sensor) ?? sensors.FirstOrDefault();
        changingHistoryFilters = false;
        sensor = (historySensor.SelectedItem as HistoryChoice)?.Sensor;
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - HistoryViewPeriod();
        var recorded = all.Where(p => p.Sensor == sensor).ToArray();
        historyRecorded.Text = HistoryRangeText(recorded);
        visibleHistory = recorded.Where(p => p.At >= cutoff).OrderBy(p => p.At).ToArray();
        DrawHistory();
    }

    static string HistoryRangeText(IEnumerable<HistoryPoint> rows)
    {
        if (HistoryModel.RecordedRange(rows) is not { } range) return "В истории пока нет записей.";
        var span = range.At - range.From;
        string duration = span.TotalDays >= 1 ? $"{span.Days} д {span.Hours} ч" :
            span.TotalHours >= 1 ? $"{span.Hours} ч {span.Minutes} мин" :
            span.TotalMinutes >= 1 ? $"{span.Minutes} мин {span.Seconds} с" : $"{span.Seconds} с";
        return $"Собраны показания с {range.From.LocalDateTime:dd.MM.yyyy HH:mm:ss} по {range.At.LocalDateTime:dd.MM.yyyy HH:mm:ss} · {duration}.";
    }

    string? HistoryDeviceInstance(string id, string name) => latest?.Telemetry.Gpus
        .Where(device => string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray() is [{ Id: var found, InstanceId: var instance }]
        && found == id ? instance : null;

    string HistorySensorLabel(SensorReference sensor, string name, string unit, string? deviceInstance)
    {
        string device = preferences.DeviceLabel(sensor.DeviceId, sensor.DeviceName, deviceInstance);
        string label = preferences.Label(sensor.Id, FriendlySource(name));
        return $"{device} · {label}" + (unit.Length == 0 ? "" : $" ({unit})");
    }

    TimeSpan HistoryViewPeriod() => historyRange.SelectedIndex switch
    { 1 => TimeSpan.FromDays(1), 2 => TimeSpan.FromDays(7), 3 => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(1) };

    void DrawHistory()
    {
        historyPlot.Children.Clear();
        DateTimeOffset end = DateTimeOffset.Now, start = end - HistoryViewPeriod();
        string Stamp(DateTimeOffset time) => time.ToLocalTime().ToString(HistoryViewPeriod() <= TimeSpan.FromHours(1) ? "HH:mm:ss" : "dd.MM HH:mm");
        historyLeft.Text = Stamp(start); historyRight.Text = Stamp(end);
        if (visibleHistory.Length == 0)
        {
            historyInfo.Text = viewModel.History.Error ?? (viewModel.History.Retention == HistoryRetention.Off
                ? "Запись истории выключена. Её можно включить в настройках."
                : preferences.HistorySensors is { Length: 0 } ? "Датчики для записи не выбраны." : "За выбранный период пока нет данных.");
            return;
        }
        float min = visibleHistory.Min(p => p.Min), max = visibleHistory.Max(p => p.Max);
        double height = historyPlot.ActualHeight, width = historyPlot.ActualWidth;
        if (height < 20 || width < 20) return;
        double X(DateTimeOffset time) => 8 + Math.Clamp((time - start).TotalSeconds / (end - start).TotalSeconds, 0, 1) * (width - 16);
        double Y(float value) => max > min ? 10 + (1 - (value - min) / (max - min)) * (height - 20) : height / 2;
        var segment = new List<Point>();
        HistoryPoint? previous = null;
        var extremes = new StreamGeometry();
        using (var lines = extremes.Open())
        {
            foreach (var point in visibleHistory)
            {
                if (previous != null && !HistoryModel.Continuous(previous, point)) AddSegment();
                double x = X(point.At);
                segment.Add(new Point(x, Y(point.Mean)));
                if (point.Count > 1 && point.Min != point.Max)
                {
                    lines.BeginFigure(new Point(x, Y(point.Min)), false, false);
                    lines.LineTo(new Point(x, Y(point.Max)), true, false);
                }
                previous = point;
            }
        }
        extremes.Freeze();
        var ranges = new System.Windows.Shapes.Path { Data = extremes, StrokeThickness = 1, Opacity = .42 };
        ranges.SetResourceReference(Shape.StrokeProperty, AppTheme.Accent); historyPlot.Children.Add(ranges);
        AddSegment();
        historyInfo.Text = viewModel.History.Error ?? $"{visibleHistory.Length:N0} записей · диапазон {min:0.#}–{max:0.#} {visibleHistory[0].Unit}. Разрывы показывают периоды без показаний.";

        void AddSegment()
        {
            if (segment.Count == 1)
            {
                var dot = new Ellipse { Width = 5, Height = 5 }; dot.SetResourceReference(Shape.FillProperty, AppTheme.Accent);
                Canvas.SetLeft(dot, segment[0].X - 2.5); Canvas.SetTop(dot, segment[0].Y - 2.5); historyPlot.Children.Add(dot);
            }
            else if (segment.Count > 1)
            {
                var line = new Polyline { Points = new PointCollection(segment), StrokeThickness = 2 };
                line.SetResourceReference(Shape.StrokeProperty, AppTheme.Accent); historyPlot.Children.Add(line);
            }
            segment.Clear();
        }
    }

    async Task<bool> ExportHistory(HistoryPoint[] rows, string name)
    {
        var picker = new SaveFileDialog { FileName = name, Filter = "CSV|*.csv|JSON|*.json", DefaultExt = ".csv" };
        if (picker.ShowDialog(this) != true) return false;
        if (viewModel.History.OwnsPath(picker.FileName)) { MessageBox.Show(this, "Выберите другой файл для экспорта."); return false; }
        try
        {
            await Task.Run(() => HistoryExport.Save(picker.FileName, rows));
            return true;
        }
        catch (Exception error) { Monitor.Log(error); MessageBox.Show(this, "Не удалось сохранить историю: " + error.Message); return false; }
    }

    async Task ClearHistory()
    {
        if (MessageBox.Show(this, "Все сохранённые показания будут удалены. Перед очисткой AirDesk предложит сохранить полную копию.",
            "Очистить историю", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        viewModel.ConfigureHistory(HistoryRetention.Off);
        try
        {
            if (!await ExportHistory(await viewModel.History.ReadAsync(), "AirDesk-history-backup")) return;
            await viewModel.History.ClearAsync();
            RefreshHistory(force: true);
        }
        catch (Exception error) { Monitor.Log(error); MessageBox.Show(this, "Не удалось очистить историю: " + error.Message); }
        finally { viewModel.ConfigureHistory(preferences.Retention, preferences.HistorySensors); }
    }
}
