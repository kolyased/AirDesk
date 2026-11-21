using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AirDesk;

public sealed partial class Dashboard
{
    readonly Dictionary<OverviewCard, ProgressBar> cardMeters = new();
    readonly LiveTrace cpuLoadTrace = new(), cpuTemperatureTrace = new(), gpuTemperatureTrace = new(), fanTrace = new();
    readonly Dictionary<OverviewChart, Border> chartPanels = new();
    readonly UniformGrid chartGrid = new() { Columns = 1 };
    readonly ComboBox graphFan = new() { MinWidth = 125, MaxWidth = 315, Margin = new(0, 3, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock monitorSettingsHeading = Text("Показатели обзора", 19), loadNow = Text("", 12),
        tempNow = Text("", 12), fanNow = Text("", 12);
    LivePlot loadPlot = null!, tempPlot = null!, fanPlot = null!;
    Telemetry? traceTelemetry;
    DateTimeOffset lastTraceRefresh;
    (string? Id, string? Instance) traceGpu;
    bool traceStale, updatingFanChoice;
    record FanOption(SensorReference? Sensor, string Label, bool Missing = false) { public override string ToString() => Label; }

    Border MonitorCard(OverviewCard card, TextBlock value, TextBlock detail, string measure)
    {
        var body = new StackPanel();
        var title = Text(CardName(card), 14);
        title.FontWeight = FontWeights.SemiBold;
        body.Children.Add(title);
        body.Children.Add(Text(measure, 12));
        value.FontWeight = FontWeights.SemiBold;
        value.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.Accent);
        body.Children.Add(value);
        var bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, IsHitTestVisible = false,
            Focusable = false, Margin = new(0, 0, 0, 9), Visibility = Visibility.Hidden };
        bar.SetResourceReference(Control.ForegroundProperty, AppTheme.Accent);
        bar.SetResourceReference(Control.BackgroundProperty, AppTheme.Alternate);
        NameForAutomation(bar, $"{CardName(card)}. {measure}");
        cardMeters.Add(card, bar);
        body.Children.Add(bar);
        detail.TextWrapping = TextWrapping.Wrap;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.MaxHeight = 72;
        body.Children.Add(detail);
        return Surface(body, new Thickness(13), new Thickness(0, 0, 8, 8));
    }

    void UpdateMonitorCards(Telemetry data, DeviceReadings? gpu, bool stale)
    {
        var fields = preferences.OverviewDetails;
        var disk = data.Disks.Where(device => LiveValue(device.Temperature, stale).HasValue)
            .MaxBy(device => LiveValue(device.Temperature, stale));
        float? storage = data.Disks.Select(device => LiveValue(device.Load, stale)).DefaultIfEmpty().Max();
        Set(OverviewCard.Cpu, cpuMetric, LiveValue(data.Cpu?.Load, stale), fields.HasFlag(OverviewDetail.CpuTemperature)
            ? "Температура · " + ValueStatus(data.Cpu?.Temperature, stale, "°C") : "");

        var gpuInfo = new List<string>(3);
        if (fields.HasFlag(OverviewDetail.GpuTemperature)) gpuInfo.Add("Ядро · " + ValueStatus(gpu?.Temperature, stale, "°C"));
        if (fields.HasFlag(OverviewDetail.GpuHotspot)) gpuInfo.Add("Горячая точка · " + ValueStatus(gpu?.Hotspot, stale, "°C"));
        if (gpu != null) gpuInfo.Add(preferences.DeviceLabel(gpu.Id, gpu.Name, gpu.InstanceId));
        else if (preferences.Gpu != null) gpuInfo.Add("Выбранная видеокарта недоступна");
        Set(OverviewCard.Gpu, gpuMetric, LiveValue(gpu?.Load, stale), string.Join("\n", gpuInfo));

        string memory = fields.HasFlag(OverviewDetail.MemoryCapacity)
            ? LiveValue(data.Ram?.MemoryUsed, stale) is float used && data.Ram?.MemoryTotal is float total
                ? $"{used:0.#} / {total:0.#} ГБ" : "Объём · нет данных"
            : "";
        Set(OverviewCard.Memory, memoryMetric, LiveValue(data.Ram?.Load, stale), memory);
        Set(OverviewCard.Storage, storageMetric, storage, fields.HasFlag(OverviewDetail.StorageTemperature)
            ? disk == null ? "Температура · нет данных" : $"{Format(disk.Temperature?.Value, "°C")} · {preferences.Label(disk.Id, disk.Name)}" : "");
        cards[OverviewCard.Cpu].ToolTip = CardHint("Загрузка", data.Cpu?.Load, "Температура", data.Cpu?.Temperature, stale);
        cards[OverviewCard.Gpu].ToolTip = CardHint("Загрузка", gpu?.Load, "Температура ядра", gpu?.Temperature, stale);
        cards[OverviewCard.Memory].ToolTip = CardHint("Занято", data.Ram?.Load, "Объём", data.Ram?.MemoryUsed, stale);
        cards[OverviewCard.Storage].ToolTip = CardHint("Активность", data.Disks.Select(device => device.Load)
            .Where(reading => LiveValue(reading, stale).HasValue).MaxBy(reading => reading?.Value), "Температура", disk?.Temperature, stale);

        void Set(OverviewCard card, DisplayMetric metric, float? value, string detail)
        {
            metric.Set(Format(value, "%"), detail.Length == 0 && value == null ? stale ? "Показания устарели" : "Нет показаний нагрузки" : detail);
            var bar = cardMeters[card];
            bar.Value = Math.Clamp(value ?? 0, 0, 100);
            bar.Visibility = value.HasValue ? Visibility.Visible : Visibility.Hidden;
        }
    }

    static float? LiveValue(Reading? reading, bool stale) => !stale && reading is { Available: true, Value: float value } &&
        float.IsFinite(value) ? value : null;

    FrameworkElement BuildLiveGraphs()
    {
        loadPlot = Plot("Загрузка процессора, проценты", cpuLoadTrace);
        tempPlot = Plot("Температура процессора и видеокарты, градусы Цельсия", cpuTemperatureTrace, gpuTemperatureTrace);
        fanPlot = Plot("Скорость вентилятора, обороты в минуту", fanTrace);
        NameForAutomation(graphFan, "Тахометр графика оборотов");
        chartPanels.Add(OverviewChart.CpuLoad, Panel("Загрузка CPU · %", loadNow, loadPlot));
        chartPanels.Add(OverviewChart.Temperatures, Panel("Температуры · °C", tempNow, tempPlot));
        chartPanels.Add(OverviewChart.FanRpm, Panel("Обороты · об/мин", fanNow, fanPlot, graphFan));
        chartGrid.SizeChanged += (_, size) => chartGrid.Columns = size.NewSize.Width >= 755 ? 2 : 1;
        chartGrid.IsVisibleChanged += (_, _) =>
        {
            if (chartGrid.IsVisible && latest is { } current) RenderLiveGraphs(current, previousStale, force: true);
        };
        graphFan.SelectionChanged += (_, _) =>
        {
            if (updatingFanChoice || graphFan.SelectedItem is not FanOption { Missing: false } option || option.Sensor == preferences.OverviewFan) return;
            preferences.OverviewFan = option.Sensor;
            fanTrace.Clear();
            SavePreference();
            if (latest != null) RenderLiveGraphs(latest, previousStale, force: true);
        };
        ApplyLiveGraphPreferences();
        return chartGrid;

        static LivePlot Plot(string label, params LiveTrace[] series)
        {
            var plot = new LivePlot(series) { Height = 118 };
            NameForAutomation(plot, label);
            return plot;
        }

        static Border Panel(string label, TextBlock current, LivePlot plot, ComboBox? source = null)
        {
            var body = new StackPanel();
            body.Children.Add(Text(label, 15));
            if (source != null) body.Children.Add(source);
            current.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(current);
            body.Children.Add(plot);
            return Surface(body, new Thickness(12), new Thickness(0, 0, 8, 8));
        }
    }

    void ApplyLiveGraphPreferences()
    {
        foreach (var (type, panel) in chartPanels) panel.Visibility = preferences.OverviewCharts.HasFlag(type) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var panel in chartPanels.Values)
            if (!chartGrid.Children.Contains(panel)) chartGrid.Children.Add(panel);
        if (latest != null) RenderLiveGraphs(latest, previousStale, force: true);
    }

    void RenderLiveGraphs(Snapshot snapshot, bool stale, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        var gpuSelection = (preferences.Gpu, preferences.GpuInstance);
        if (!force && ReferenceEquals(traceTelemetry, snapshot.Telemetry) && stale == traceStale && gpuSelection == traceGpu &&
            (!stale || now - lastTraceRefresh < TimeSpan.FromSeconds(1))) return;
        if (gpuSelection != traceGpu) gpuTemperatureTrace.Clear();
        traceGpu = gpuSelection;
        traceTelemetry = snapshot.Telemetry;
        traceStale = stale;
        lastTraceRefresh = now;

        var data = snapshot.Telemetry;
        var cpu = data.Cpu;
        var gpu = preferences.SelectedGpu(data);
        var sampleAt = stale ? now : snapshot.Time;
        var fans = data.Readings.Where(reading => reading.Kind == "Fan" && data.Find(reading.Reference) != null).ToArray();
        var fan = preferences.OverviewFan is { } selectedFan
            ? fans.FirstOrDefault(reading => data.PersistentReference(reading) == selectedFan)
            : fans.FirstOrDefault(reading => reading.Id == snapshot.Cooling.Identity?.Tachometer);
        if (graphFan.IsVisible) UpdateGraphFanChoices(data, fans);

        float? load = LiveValue(cpu?.Load, stale);
        float? cpuTemp = Temperature(cpu?.Temperature);
        float? gpuTemp = Temperature(gpu?.Temperature);
        float? rpm = LiveValue(fan, stale) is float speed && speed >= 0 ? speed : null;
        cpuLoadTrace.Observe(sampleAt, cpu?.Load?.Reference, load);
        cpuTemperatureTrace.Observe(sampleAt, cpu?.Temperature?.Reference, cpuTemp);
        gpuTemperatureTrace.Observe(sampleAt, gpu?.Temperature is { } reading ? data.PersistentReference(reading) : null, gpuTemp);
        fanTrace.Observe(sampleAt, fan == null ? preferences.OverviewFan : data.PersistentReference(fan), rpm);
        if (!IsVisible || tabs.SelectedIndex != OverviewTab) return;

        loadNow.Text = $"Сейчас · {Format(load, "%")}";
        tempNow.Text = $"CPU {Format(cpuTemp, "°C")} · GPU {Format(gpuTemp, "°C")} (пунктир)";
        fanNow.Text = fan != null ? $"Сейчас · {Format(rpm, "об/мин")}" : fans.Length == 0 ? "Нет доступных тахометров" :
            preferences.OverviewFan == null ? "Выберите тахометр для графика" : "Выбранный тахометр недоступен";
        if (loadPlot.IsVisible) loadPlot.Refresh(now, 0, 100);
        if (tempPlot.IsVisible)
        {
            var points = cpuTemperatureTrace.Points.Concat(gpuTemperatureTrace.Points).Select(point => point.Value).OfType<float>().ToArray();
            double low = points.Length == 0 ? 0 : Math.Max(0, Math.Floor(points.Min() / 10d) * 10 - 10);
            double high = points.Length == 0 ? 100 : Math.Max(low + 20, Math.Ceiling(points.Max() / 10d) * 10 + 10);
            tempPlot.Refresh(now, low, high);
        }
        if (fanPlot.IsVisible)
        {
            double high = Math.Max(1000, Math.Ceiling(fanTrace.Points.Select(point => (double?)point.Value).Max() / 500d ?? 0) * 500);
            fanPlot.Refresh(now, 0, high);
        }

        float? Temperature(Reading? input) => LiveValue(input, stale) is float value && FanPolicy.ValidTemperature(value) ? value : null;
    }

    void UpdateGraphFanChoices(Telemetry data, IEnumerable<Reading> fans)
    {
        var options = new List<FanOption> { new(null, "Тахометр канала охлаждения") };
        options.AddRange(fans.DistinctBy(data.PersistentReference).Select(reading => new FanOption(data.PersistentReference(reading),
            $"{reading.Device} · {preferences.Label(reading.Id, reading.Name)} ({reading.Id})")));
        var selected = options.FirstOrDefault(option => option.Sensor == preferences.OverviewFan);
        if (selected == null)
        {
            selected = new(preferences.OverviewFan, "Сохранённый тахометр недоступен", true);
            options.Add(selected);
        }
        if (graphFan.ItemsSource is FanOption[] current && current.SequenceEqual(options) && Equals(graphFan.SelectedItem, selected)) return;
        updatingFanChoice = true;
        try { graphFan.ItemsSource = options.ToArray(); graphFan.SelectedItem = selected; }
        finally { updatingFanChoice = false; }
    }
}
