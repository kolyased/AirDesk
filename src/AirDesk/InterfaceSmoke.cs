using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace AirDesk;

public sealed partial class Dashboard
{
    void CaptureInterface()
    {
        var results = new System.Collections.Generic.List<object>();
        foreach (var (name, appearance, width, dpi, columns) in new[]
        {
            ("dark-100", Appearance.Dark, 1040d, 96, 4),
            ("light-125", Appearance.Light, 950d, 120, 4),
            ("dark-150", Appearance.Dark, 690d, 144, 2),
            ("light-200", Appearance.Light, 430d, 192, 1)
        })
        {
            AppTheme.Apply(appearance);
            Navigate(OverviewTab);
            Width = width;
            UpdateLayout();
            if (overviewCards.Columns != columns)
                throw new InvalidOperationException($"{name}: expected {columns} overview columns, got {overviewCards.Columns}.");
            SaveImage(this, name, dpi);
            results.Add(new { name, dpi, OverviewDip = overviewCards.ActualWidth, Columns = overviewCards.Columns });
        }
        AppTheme.Apply(Appearance.Dark);
        Width = 1040;
        Navigate(CoolingTab);
        SaveImage(this, "cooling", 96);
        Navigate(SettingsTab);
        SaveImage(this, "settings", 96);
        var work = Forms.Screen.PrimaryScreen!.WorkingArea;
        flyoutAnchor = new System.Drawing.Point(work.Right - 12, work.Bottom);
        ToggleFlyout();
        SaveImage(flyout!, "tray", 96);
        flyout?.Hide();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-smoke.json"), JsonSerializer.Serialize(results));
    }

    static void SaveImage(Window window, string name, int dpi)
    {
        window.UpdateLayout();
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name + ".png"));
        encoder.Save(file);
    }
}
