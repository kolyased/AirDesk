using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AirDesk;

internal static class AppTheme
{
    public const string Background = "AirDesk.Background", Surface = "AirDesk.Surface", Alternate = "AirDesk.Alternate",
        Text = "AirDesk.Text", Muted = "AirDesk.Muted", Accent = "AirDesk.Accent", Warning = "AirDesk.Warning", Edge = "AirDesk.Edge";

    public static void Apply(Appearance appearance)
    {
        var application = Application.Current;
        bool dark = appearance == Appearance.Dark || appearance == Appearance.System && IsDark();
        // WPF supplies the templates, keyboard focus, and Windows high-contrast behavior.
        #pragma warning disable WPF0001
        application.ThemeMode = appearance == Appearance.System || SystemParameters.HighContrast ? ThemeMode.System : dark ? ThemeMode.Dark : ThemeMode.Light;
        #pragma warning restore WPF0001
        var resources = application.Resources;
        if (SystemParameters.HighContrast)
        {
            resources[Background] = SystemColors.WindowBrush;
            resources[Surface] = resources[Alternate] = SystemColors.WindowBrush;
            resources[Text] = resources[Muted] = resources[Accent] = resources[Warning] = resources[Edge] = SystemColors.WindowTextBrush;
            return;
        }

        resources[Background] = Brush(dark ? "#101720" : "#EEF4F3");
        resources[Surface] = Brush(dark ? "#1A2531" : "#FFFFFF");
        resources[Alternate] = Brush(dark ? "#222F3D" : "#F1F6F5");
        resources[Text] = Brush(dark ? "#F4FAFE" : "#16262B");
        resources[Muted] = Brush(dark ? "#AEBFD0" : "#445B61");
        resources[Accent] = Brush(dark ? "#75E3BF" : "#075E4D");
        resources[Warning] = Brush(dark ? "#FFCF90" : "#854411");
        resources[Edge] = Brush(dark ? "#34475A" : "#C8D9D7");
    }

    static bool IsDark()
    {
        try
        {
            using var preferences = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return preferences?.GetValue("AppsUseLightTheme") is not int value || value == 0;
        }
        catch (Exception error) { Monitor.Log(error); return true; }
    }

    static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
