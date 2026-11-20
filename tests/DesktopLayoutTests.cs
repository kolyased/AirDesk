using System;
using System.IO;
using System.Linq;
using AirDesk;
using Xunit;

public sealed class DesktopLayoutTests
{
    static void Check(bool result, string name) => Assert.True(result, name);

    [Fact]
    public void ResponsiveCardsAndTooltipStayWithinLimits()
    {
        Check(DesktopLayout.Columns(820) == 4 && DesktopLayout.Columns(819) == 2 && DesktopLayout.Columns(479) == 1,
            "Overview keeps 4, 2, and 1 columns at the configured breakpoints");
        var cards = DesktopLayout.NormalizeCards([new(OverviewCard.Gpu, false), new(OverviewCard.Gpu, true), new((OverviewCard)123, true)]);
        Check(cards.Length == 4 && cards[0] == new CardSetting(OverviewCard.Gpu, false) && cards.Skip(1).All(card => card.Visible),
            "Card preferences preserve order and visibility while repairing invalid entries");
        Check(DesktopLayout.Tooltip(["", new string('x', 70), "RAM 45 %"]) == "AirDesk · RAM 45 %",
            "Tooltip fits the Windows limit and leaves out missing or overlong measurements");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void TrayAnchorsOnEveryEdgeAtDpi(int percent)
    {
        int width = 360 * percent / 100, height = 280 * percent / 100, gap = 10 * percent / 100;
        DesktopLayout.Area screen = new(-1920, -150, 0, 930);
        var bottom = DesktopLayout.Anchor(screen, screen with { Bottom = 875 }, -30, 900, width, height, gap);
        var top = DesktopLayout.Anchor(screen, screen with { Top = -100 }, -15, -125, width, height, gap);
        var left = DesktopLayout.Anchor(screen, screen with { Left = -1870 }, -1890, 900, width, height, gap);
        var right = DesktopLayout.Anchor(screen, screen with { Right = -50 }, -20, 900, width, height, gap);
        Check(bottom.Y == 875 - height - gap && bottom.X == -width - gap, $"Bottom taskbar at {percent}%");
        Check(top.Y == -100 + gap && top.X == -width - gap, $"Top taskbar at {percent}%");
        Check(left.X == -1870 + gap && left.Y == 930 - height - gap, $"Left taskbar at {percent}%");
        Check(right.X == -50 - width - gap && right.Y == 930 - height - gap, $"Right taskbar at {percent}%");
    }

    [Fact]
    public void AutoHiddenTaskbarFallsBackToNearestEdge()
    {
        var hidden = new DesktopLayout.Area(0, 0, 1920, 1080);
        Check(DesktopLayout.Anchor(hidden, hidden, 960, 1079, 360, 280, 10) == (780, 790), "Auto-hide falls back to the closest screen edge");
    }

    [Fact]
    public void LiveTracePreservesMissingSamplesAndClearsWhenTheSourceChanges()
    {
        var trace = new LiveTrace();
        var now = DateTimeOffset.UtcNow;
        var a = new SensorReference("fan/1", "board", "Board");
        var b = new SensorReference("fan/2", "board", "Board");
        trace.Observe(now, a, 800);
        trace.Observe(now.AddSeconds(1), null, null);
        trace.Observe(now.AddSeconds(2), a, float.NaN);
        trace.Observe(now.AddSeconds(3), a, 0);
        Assert.Equal(new float?[] { 800, null, null, 0 }, trace.Points.Select(point => point.Value));
        trace.Observe(now.AddSeconds(4), b, 1000);
        Assert.Equal(1000f, Assert.Single(trace.Points).Value);
        trace.Observe(now.AddMinutes(6), b, 900);
        Assert.Equal(900f, Assert.Single(trace.Points).Value);
    }

    [Fact]
    public void MonitorAndCompactSelectionsPersistWithLegacyDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("AirDesk-layout-");
        try
        {
            string file = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(file, "{\"SchemaVersion\":2}");
            var legacy = UserPreferences.Load(file);
            Assert.Equal(OverviewDetail.Default, legacy.OverviewDetails);
            Assert.Equal(OverviewChart.Default, legacy.OverviewCharts);
            Assert.Equal(DesktopLayout.DefaultCompactMetrics(), legacy.CompactMetrics);

            legacy.OverviewDetails = OverviewDetail.GpuHotspot | (OverviewDetail)64;
            legacy.OverviewCharts = OverviewChart.FanRpm;
            legacy.CompactMetrics = [TrayMetric.FanRpm, TrayMetric.FanRpm, (TrayMetric)100];
            legacy.OverviewFan = new("/fan/1", "/board", "Motherboard");
            legacy.Save();
            var saved = UserPreferences.Load(file);
            Assert.Equal(OverviewDetail.GpuHotspot, saved.OverviewDetails);
            Assert.Equal(OverviewChart.FanRpm, saved.OverviewCharts);
            Assert.Equal(new[] { TrayMetric.FanRpm }, saved.CompactMetrics);
            Assert.Equal(legacy.OverviewFan, saved.OverviewFan);
        }
        finally { directory.Delete(recursive: true); }
    }
}
