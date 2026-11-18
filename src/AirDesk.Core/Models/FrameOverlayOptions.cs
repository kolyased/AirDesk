using System;

namespace AirDesk;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

[Flags]
public enum OverlayMetric { None = 0, Frames = 1, FrameTime = 2, Cpu = 4, Gpu = 8, Ram = 16 }

public sealed record FrameOverlayOptions(bool Enabled = false, OverlayCorner Corner = OverlayCorner.TopLeft,
    double Scale = 1, double Opacity = .85, OverlayMetric Metrics = OverlayMetric.Frames | OverlayMetric.Cpu | OverlayMetric.Gpu)
{
    public FrameOverlayOptions Normalize() => this with
    {
        Corner = Enum.IsDefined(Corner) ? Corner : OverlayCorner.TopLeft,
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .75, 2) : 1,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .25, 1) : .85,
        Metrics = Metrics & (OverlayMetric.Frames | OverlayMetric.FrameTime | OverlayMetric.Cpu | OverlayMetric.Gpu | OverlayMetric.Ram)
    };
}
