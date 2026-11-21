using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AirDesk;

internal sealed class OverlayWindow : Window
{
    const int ExtendedStyle = -20, Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
    readonly TextBlock metrics;
    OverlayCorner corner;

    public OverlayWindow(FontFamily font)
    {
        Title = "AirDesk OSD";
        WindowStyle = WindowStyle.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Focusable = false;
        IsHitTestVisible = false;
        metrics = CreateText(font);
        Content = CreateSurface(metrics);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, ExtendedStyle, GetWindowLong(handle, ExtendedStyle) | Transparent | ToolWindow | NoActivate);
        };
        SizeChanged += (_, _) => Position();
        DpiChanged += (_, _) => Position();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    public string Metrics
    {
        get => metrics.Text;
        set { if (metrics.Text != value) metrics.Text = value; }
    }

    internal bool PassiveStyleApplied => (GetWindowLong(new WindowInteropHelper(this).Handle, ExtendedStyle) & (Transparent | NoActivate)) ==
        (Transparent | NoActivate);

    public void ApplyOptions(FrameOverlayOptions options)
    {
        corner = options.Corner;
        metrics.FontSize = 14 * options.Scale;
        Opacity = options.Opacity;
    }

    public void Position()
    {
        if (!IsVisible || Forms.Screen.PrimaryScreen is not { } screen ||
            PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        var area = screen.WorkingArea;
        Point first = target.TransformFromDevice.Transform(new Point(area.Left, area.Top));
        Point last = target.TransformFromDevice.Transform(new Point(area.Right, area.Bottom));
        MaxWidth = Math.Max(1, last.X - first.X);
        MaxHeight = Math.Max(1, last.Y - first.Y);
        double spareWidth = Math.Max(0, last.X - first.X - ActualWidth);
        double spareHeight = Math.Max(0, last.Y - first.Y - ActualHeight);
        double horizontalMargin = Math.Min(12, spareWidth / 2);
        double verticalMargin = Math.Min(12, spareHeight / 2);
        Left = first.X + (corner is OverlayCorner.TopRight or OverlayCorner.BottomRight ? spareWidth - horizontalMargin : horizontalMargin);
        Top = first.Y + (corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight ? spareHeight - verticalMargin : verticalMargin);
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Position);
    }

    public static TextBlock CreateText(FontFamily font)
    {
        var text = new TextBlock
        {
            Foreground = Brushes.White, FontFamily = font, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340
        };
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    public static Border CreateSurface(TextBlock text) => new()
    {
        Child = text, Background = new SolidColorBrush(Color.FromRgb(15, 21, 30)),
        Padding = new Thickness(11, 7, 11, 7), CornerRadius = new CornerRadius(5)
    };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr window, int index, int value);
}
