using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AirDesk;

internal sealed class LivePlot(params LiveTrace[] series) : FrameworkElement
{
    static readonly Typeface AxisFont = new("Segoe UI");
    DateTimeOffset end = DateTimeOffset.UtcNow;
    double minimum, maximum = 100;

    public void Refresh(DateTimeOffset now, double lower, double upper)
    {
        end = now;
        minimum = lower;
        maximum = upper;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 90 || ActualHeight < 55) return;
        var plot = new Rect(42, 8, ActualWidth - 49, ActualHeight - 30);
        var foreground = (Brush)FindResource(AppTheme.Muted);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisFont, 10, foreground, dpi);
        var grid = new Pen((Brush)FindResource(AppTheme.Edge), 1);
        drawing.DrawRectangle((Brush)FindResource(AppTheme.Background), null, plot);
        for (int mark = 0; mark <= 2; mark++)
        {
            double y = plot.Top + plot.Height * mark / 2;
            var label = Label((maximum - (maximum - minimum) * mark / 2).ToString("0", CultureInfo.CurrentCulture));
            drawing.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
            drawing.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        var right = Label("Сейчас");
        drawing.DrawText(Label("−5 мин"), new Point(plot.Left, plot.Bottom + 4));
        drawing.DrawText(right, new Point(plot.Right - right.Width, plot.Bottom + 4));

        drawing.PushClip(new RectangleGeometry(plot));
        var start = end - LiveTrace.Period;
        for (int index = 0; index < series.Length; index++)
        {
            var stroke = (Brush)FindResource(index == 0 ? AppTheme.Accent : AppTheme.ChartSecondary);
            var pen = new Pen(stroke, 1.8) { DashStyle = index == 0 ? DashStyles.Solid : DashStyles.Dash };
            (DateTimeOffset At, Point Position)? previous = null;
            var line = new StreamGeometry();
            using (var geometry = line.Open())
            {
                foreach (var reading in series[index].Points)
                {
                    if (reading.Value is not float value || reading.At < start || reading.At > end)
                    {
                        previous = null;
                        continue;
                    }
                    var point = new Point(plot.Left + (reading.At - start).TotalSeconds / LiveTrace.Period.TotalSeconds * plot.Width,
                        plot.Bottom - (value - minimum) / (maximum - minimum) * plot.Height);
                    if (previous is { } sample && reading.At - sample.At <= LiveTrace.MaximumGap) geometry.LineTo(point, true, false);
                    else geometry.BeginFigure(point, false, false);
                    previous = (reading.At, point);
                }
            }
            line.Freeze();
            drawing.DrawGeometry(null, pen, line);
            if (previous is { } last) drawing.DrawEllipse(stroke, null, last.Position, 2.5, 2.5);
        }
        drawing.Pop();
    }
}
