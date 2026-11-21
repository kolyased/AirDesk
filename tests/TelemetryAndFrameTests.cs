using System;
using System.Globalization;
using System.Linq;
using AirDesk;
using Xunit;

public sealed class TelemetryAndFrameTests
{
    [Theory]
    [InlineData("fan.control.service", "FanControl")]
    [InlineData("FanControl.exe", "FanControl")]
    [InlineData("FaNcOnTrOl.SeRvIcE.ExE", "FanControl")]
    [InlineData("MSIAfterburner", "MSI Afterburner")]
    [InlineData("asusfancontrolservice", "ASUS Fan Xpert / AI Suite")]
    [InlineData("NZXT CAM.exe", "NZXT CAM")]
    [InlineData("cam_helper.exe", "NZXT CAM")]
    [InlineData("Corsair.Service.exe", "Corsair iCUE")]
    [InlineData("FanControlUpdater", null)]
    [InlineData("FanControlProfileEditor.exe", null)]
    [InlineData("MSIAfterburnerHelper", null)]
    [InlineData("ArgusMonitorSetup", null)]
    [InlineData("OtherCam_Helper.exe", null)]
    [InlineData("explorer", null)]
    public void ControllerRecognitionUsesProcessName(string process, string? expected)
    {
        Assert.Equal(expected, ControllerNames.Match(process));
    }

    [Fact]
    public void MissingAndInvalidReadingsAreNeverPresentedAsZero()
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        Assert.Equal("—", ReadingDisplay.Value(null, "°C", culture));
        Assert.Equal("—", ReadingDisplay.Value(float.NaN, "°C", culture));
        Assert.Equal("—", ReadingDisplay.Value(float.PositiveInfinity, "°C", culture));
        Assert.Equal("42,5 °C", ReadingDisplay.Value(42.5f, "°C", culture));
    }

    [Theory]
    [InlineData(ReadingQuality.Stale)]
    [InlineData(ReadingQuality.AccessDenied)]
    public void RamTotalInheritsAnUnavailableInputsStatus(ReadingQuality freeQuality)
    {
        var used = new Reading("RAM", "/ram", "Memory", "Memory Used", "Data", "ГБ", 16, "/ram/data/0",
            ReadingQuality.Fresh, DateTimeOffset.UtcNow);
        var free = used with { Name = "Memory Available", Id = "/ram/data/1", Quality = freeQuality };

        var total = new Telemetry([used, free]).Ram!.Details().Single(metric => metric.Label == "Всего");

        Assert.Null(total.Derived);
        Assert.Equal(freeQuality, total.Missing);
    }

    [Fact]
    public void FrameRateUsesTheDominantSwapChainOfOnlyTheSelectedPid()
    {
        var reader = new PresentMonFrames("\uFEFFApplication,ProcessID,SwapChainAddress,msBetweenPresents");
        var measured = reader.Summarize([
            "\"Game, Inc.\",101,A,10",
            "Game,101,A,20",
            "Game,101,A,30",
            "Game,101,B,1",
            "Other,99,C,2",
            "Other,99,C,3",
            "Other,99,C,4",
            "Other,99,C,5",
            "Invalid,101,A,NaN",
            "Invalid,101,A,Infinity",
            "Invalid,101,A,0",
            "Invalid,101,A,5001",
            "\"Unterminated,101,A,16"
        ], 101);
        Assert.Equal(new FrameRate(50, 20, 3), measured);
        Assert.Null(reader.Summarize(["Game,101,A,16.667"], 101));
        Assert.Null(reader.Summarize(["Game,101,A,20", "Game,101,A,20"], 500));
    }

    [Fact]
    public void EmptyOrMismatchedFrameDataIsUnavailable()
    {
        Assert.Throws<FormatException>(() => new PresentMonFrames("Application,ProcessID,CPUFrameTime"));
        var reader = new PresentMonFrames("ProcessID,SwapChainAddress,MsBetweenPresents");
        Assert.Null(reader.Summarize(["10,A,0", "10,A,25", "10,B,20"], 10));
    }

    [Fact]
    public void OverlayDefaultsOffAndSettingsAreBounded()
    {
        Assert.False(new FrameOverlayOptions().Enabled);
        var repaired = new FrameOverlayOptions(true, (OverlayCorner)1234, 55, double.NaN, (OverlayMetric)128).Normalize();
        Assert.Equal(new FrameOverlayOptions(true, OverlayCorner.TopLeft, 2, .85, OverlayMetric.None), repaired);
    }
}
