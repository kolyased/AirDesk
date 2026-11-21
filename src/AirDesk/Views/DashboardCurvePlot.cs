using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AirDesk;

internal sealed class DashboardCurvePlot : Canvas
{
    const double LeftInset = 43, RightInset = 21, TopInset = 22, BottomInset = 43;
    static readonly int[] PercentTicks = [0, 25, 50, 75, 100];
    static readonly int[] TemperatureTicks = [0, 25, 50, 75, 100, CurvePreview.MaximumTemperature];
    readonly Ellipse liveMarker = new() { Width = 13, Height = 13, StrokeThickness = 2, IsHitTestVisible = false };
    CurvePoint[]? points;
    CurvePreview preview = new([], []);
    float? floor;
    CurvePoint? live;
    bool handles, protection;

    public DashboardCurvePlot()
    {
        Height = 192;
        ClipToBounds = true;
        Margin = new(0, 4, 0, 6);
        SetResourceReference(BackgroundProperty, AppTheme.Background);
        liveMarker.SetResourceReference(Shape.FillProperty, AppTheme.Background);
        SizeChanged += (_, _) => Draw();
    }

    double PlotWidth => Math.Max(1, ActualWidth - LeftInset - RightInset);
    double PlotHeight => Math.Max(1, ActualHeight - TopInset - BottomInset);

    public Point Position(CurvePoint point) => new(
        LeftInset + PlotWidth * point.Celsius / CurvePreview.MaximumTemperature,
        TopInset + PlotHeight * (1 - point.Percent / 100));

    public CurvePoint ValueAt(Point position) => new(
        MathF.Round((float)((position.X - LeftInset) / PlotWidth * CurvePreview.MaximumTemperature), 1),
        MathF.Round((float)((1 - (position.Y - TopInset) / PlotHeight) * 100), 1));

    public void SetCurve(CurvePoint[]? configured, float? verifiedFloor, bool showHandles = false)
    {
        if (ReferenceEquals(configured, points) && verifiedFloor == floor && showHandles == handles) return;
        points = configured;
        floor = verifiedFloor;
        handles = showHandles;
        preview = CurvePreview.Create(configured, verifiedFloor);
        Draw();
    }

    public void SetLivePoint(CurvePoint? value, bool temperatureProtection)
    {
        if (value == live && protection == temperatureProtection) return;
        live = value;
        protection = temperatureProtection;
        PositionLivePoint();
    }

    void Draw()
    {
        Children.Clear();
        if (ActualWidth <= LeftInset + RightInset || ActualHeight <= TopInset + BottomInset) return;

        double plotBottom = TopInset + PlotHeight, plotRight = LeftInset + PlotWidth;
        foreach (int percent in PercentTicks)
        {
            double y = Position(new(0, percent)).Y;
            Rule(LeftInset, y, plotRight, y, AppTheme.Edge, 1);
            Label($"{percent}", LeftInset - 6, y - 8, rightAlign: true);
        }
        foreach (int temperature in TemperatureTicks)
        {
            double x = Position(new(temperature, 0)).X;
            Rule(x, TopInset, x, plotBottom, AppTheme.Edge, 1);
            if (PlotWidth >= 200 || temperature % 50 == 0 || temperature == CurvePreview.MaximumTemperature)
                Label($"{temperature}", x, plotBottom + 5, centered: true);
        }
        Label("Сигнал, %", 7, 3);
        Label("Температура источника, °C", LeftInset + PlotWidth / 2, plotBottom + 23, centered: true);

        double threshold = Position(new(FanPolicy.EmergencyTemperature, 0)).X;
        Rule(threshold, TopInset, threshold, plotBottom, AppTheme.Warning, 1, [4, 3]);
        if (floor is float minimum)
        {
            double height = Position(new(0, minimum)).Y;
            Rule(LeftInset, height, plotRight, height, AppTheme.Warning, 1, [1, 3]);
        }

        Series(preview.Limited, AppTheme.Accent, 2.5);
        Series(preview.Configured, AppTheme.Muted, 1.3, [5, 4]);
        if (handles && preview.Configured.Length > 0)
        {
            foreach (var point in points!)
            {
                var marker = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1.5, IsHitTestVisible = false };
                marker.SetResourceReference(Shape.FillProperty, AppTheme.Background);
                marker.SetResourceReference(Shape.StrokeProperty, AppTheme.Text);
                MoveMarker(marker, Position(point));
                Children.Add(marker);
            }
        }
        Children.Add(liveMarker);
        PositionLivePoint();
    }

    void PositionLivePoint()
    {
        liveMarker.Visibility = live != null ? Visibility.Visible : Visibility.Collapsed;
        if (live == null) return;
        liveMarker.SetResourceReference(Shape.StrokeProperty, protection ? AppTheme.Warning : AppTheme.Accent);
        MoveMarker(liveMarker, Position(live));
    }

    static void MoveMarker(Ellipse marker, Point point)
    {
        SetLeft(marker, point.X - marker.Width / 2);
        SetTop(marker, point.Y - marker.Height / 2);
    }

    void Label(string value, double x, double y, bool rightAlign = false, bool centered = false)
    {
        TextBlock label = new() { Text = value, FontSize = 11, IsHitTestVisible = false };
        label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.Muted);
        label.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(label, Math.Max(0, x - (centered ? label.DesiredSize.Width / 2 : rightAlign ? label.DesiredSize.Width : 0)));
        SetTop(label, y);
        Children.Add(label);
    }

    void Series(IEnumerable<CurvePoint> data, string brush, double weight, double[]? dash = null)
    {
        var line = new Polyline { Points = new PointCollection(data.Select(Position)) };
        if (line.Points.Count > 1) AddLine(line, brush, weight, dash);
    }

    void Rule(double x1, double y1, double x2, double y2, string brush, double weight, double[]? dash = null) =>
        AddLine(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 }, brush, weight, dash);

    void AddLine(Shape line, string brush, double weight, double[]? dash)
    {
        line.SetResourceReference(Shape.StrokeProperty, brush);
        line.StrokeThickness = weight;
        line.IsHitTestVisible = false;
        if (dash != null) line.StrokeDashArray = new(dash);
        Children.Add(line);
    }
}
