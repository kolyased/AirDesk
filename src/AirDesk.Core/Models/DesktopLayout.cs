using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

public enum Appearance { System, Dark, Light }
public enum OverviewCard { Cpu, Gpu, Memory, Storage }
public enum TrayMetric { CpuTemperature, CpuLoad, GpuTemperature, GpuLoad, MemoryLoad, FanRpm }
[Flags]
public enum OverviewDetail
{
    CpuTemperature = 1, GpuTemperature = 2, GpuHotspot = 4, MemoryCapacity = 8, StorageTemperature = 16,
    Default = CpuTemperature | GpuTemperature | MemoryCapacity | StorageTemperature,
    All = Default | GpuHotspot
}
[Flags]
public enum OverviewChart { CpuLoad = 1, Temperatures = 2, FanRpm = 4, Default = Temperatures | FanRpm, All = CpuLoad | Default }
public record CardSetting(OverviewCard Card, bool Visible);

public static class DesktopLayout
{
    public static CardSetting[] DefaultCards() => Enum.GetValues<OverviewCard>().Select(card => new CardSetting(card, true)).ToArray();
    public static TrayMetric[] DefaultTooltip() => [TrayMetric.CpuTemperature, TrayMetric.GpuTemperature];
    public static TrayMetric[] DefaultCompactMetrics() => Enum.GetValues<TrayMetric>();
    public static int Columns(double width) => width >= 820 ? 4 : width >= 480 ? 2 : 1;

    public static CardSetting[] NormalizeCards(IEnumerable<CardSetting>? saved)
    {
        var valid = (saved ?? []).Where(card => card != null && Enum.IsDefined(card.Card)).DistinctBy(card => card.Card).ToList();
        valid.AddRange(DefaultCards().Where(item => valid.All(card => card.Card != item.Card)).ToArray());
        return valid.ToArray();
    }

    public static string Tooltip(IEnumerable<string> values, int limit = 63)
    {
        string text = "AirDesk";
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (text.Length + 3 + value.Length <= limit) text += " · " + value;
        }
        return text;
    }

    public readonly record struct Area(int Left, int Top, int Right, int Bottom);

    public static (int X, int Y) Anchor(Area screen, Area work, int cursorX, int cursorY, int width, int height, int gap)
    {
        int[] reserved = [work.Left - screen.Left, work.Top - screen.Top, screen.Right - work.Right, screen.Bottom - work.Bottom];
        int[] distances = [Math.Abs(cursorX - screen.Left), Math.Abs(cursorY - screen.Top),
            Math.Abs(cursorX - screen.Right), Math.Abs(cursorY - screen.Bottom)];
        // Prefer the screen edge nearest the click that has a reserved taskbar area. If the
        // taskbar is hidden, use the nearest physical edge instead.
        var edges = Enumerable.Range(0, 4);
        int edge = (reserved.Any(size => size > 0) ? edges.Where(i => reserved[i] > 0) : edges).MinBy(i => distances[i]);
        int x = edge switch { 0 => work.Left + gap, 2 => work.Right - width - gap, _ => cursorX - width / 2 };
        int y = edge switch { 1 => work.Top + gap, 3 => work.Bottom - height - gap, _ => cursorY - height / 2 };
        return (Clamp(x, work.Left, work.Right - width, gap), Clamp(y, work.Top, work.Bottom - height, gap));
    }

    static int Clamp(int coordinate, int start, int end, int gap) => end - start >= gap * 2
        ? Math.Clamp(coordinate, start + gap, end - gap)
        : Math.Clamp(coordinate, start, Math.Max(start, end));
}
