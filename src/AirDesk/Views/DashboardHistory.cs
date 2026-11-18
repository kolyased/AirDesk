using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    readonly Canvas historyPlot = new() { Height = 200, ClipToBounds = true };
    bool changingHistoryFilters;
    int lastHistoryRevision = -1;
    HistoryPoint[] visibleHistory = [];
    sealed record HistoryChoice(SensorReference? Sensor, string? DeviceId, string Label) { public override string ToString() => Label; }

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
        var actions = new WrapPanel(); content.Children.Add(actions);
        actions.Children.Add(Button("Экспорт выборки…", () => _ = ExportHistory(visibleHistory, "AirDesk-history")));
        actions.Children.Add(Button("Очистить историю…", () => _ = ClearHistory()));
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
        if (!historyPlot.IsVisible || !force && lastHistoryRevision == viewModel.History.Revision) return;
        lastHistoryRevision = viewModel.History.Revision;
        var all = viewModel.History.Read();
        var device = (historyDevice.SelectedItem as HistoryChoice)?.DeviceId;
        var sensor = (historySensor.SelectedItem as HistoryChoice)?.Sensor;
        changingHistoryFilters = true;
        var devices = all.GroupBy(p => p.Sensor.DeviceId).Select(g => new HistoryChoice(null, g.Key, preferences.Label(g.Key, g.Last().Sensor.DeviceName)))
            .OrderBy(item => item.Label, StringComparer.CurrentCulture).Prepend(new(null, null, "Все устройства")).ToArray();
        historyDevice.ItemsSource = devices; historyDevice.SelectedItem = devices.FirstOrDefault(item => item.DeviceId == device) ?? devices[0];
        var sensors = all.Where(p => device == null || p.Sensor.DeviceId == device).GroupBy(p => p.Sensor).Select(g =>
        {
            var point = g.Last();
            return new HistoryChoice(g.Key, g.Key.DeviceId, $"{preferences.Label(g.Key.DeviceId, g.Key.DeviceName)} · {preferences.Label(g.Key.Id, point.Name)}");
        }).OrderBy(item => item.Label, StringComparer.CurrentCulture).ToArray();
        historySensor.ItemsSource = sensors; historySensor.SelectedItem = sensors.FirstOrDefault(item => item.Sensor == sensor) ?? sensors.FirstOrDefault();
        changingHistoryFilters = false;
        sensor = (historySensor.SelectedItem as HistoryChoice)?.Sensor;
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - HistoryViewPeriod();
        visibleHistory = all.Where(p => p.Sensor == sensor && p.At >= cutoff).OrderBy(p => p.At).ToArray();
        DrawHistory();
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
            historyInfo.Text = viewModel.History.Error ?? (viewModel.History.Retention == HistoryRetention.Off ?
                "Запись истории выключена. Её можно включить в настройках." : "За выбранный период пока нет данных.");
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
            await Task.Run(() =>
            {
                if (System.IO.Path.GetExtension(picker.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    AtomicJson.Write(picker.FileName, new { Version = 1, ExportedAt = DateTimeOffset.UtcNow, Points = rows });
                else
                {
                    using var writer = new StreamWriter(picker.FileName, false, new UTF8Encoding(true));
                    writer.WriteLine("from,at,device,device_id,sensor,sensor_id,unit,mean,min,max,count");
                    foreach (var item in rows)
                        writer.WriteLine(string.Join(',', item.From.ToString("O"), item.At.ToString("O"), Csv(item.Sensor.DeviceName),
                            Csv(item.Sensor.DeviceId), Csv(item.Name), Csv(item.Sensor.Id), Csv(item.Unit),
                            item.Mean.ToString("R", CultureInfo.InvariantCulture), item.Min.ToString("R", CultureInfo.InvariantCulture),
                            item.Max.ToString("R", CultureInfo.InvariantCulture), item.Count.ToString(CultureInfo.InvariantCulture)));
                }
            });
            return true;
        }
        catch (Exception error) { Monitor.Log(error); MessageBox.Show(this, "Не удалось сохранить историю: " + error.Message); return false; }
    }

    static string Csv(string value)
    {
        if (value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
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
        finally { viewModel.ConfigureHistory(preferences.Retention); }
    }
}
