using System;
using System.Collections.Generic;

namespace AirDesk;

internal sealed class LiveTrace
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(3);
    readonly Queue<(DateTimeOffset At, float? Value)> points = new();
    SensorReference? source;
    DateTimeOffset? last;

    public IEnumerable<(DateTimeOffset At, float? Value)> Points => points;

    public void Observe(DateTimeOffset at, SensorReference? sensor, float? value)
    {
        if (at < last || sensor != null && sensor != source) Clear();
        if (at == last) return;
        source = sensor ?? source;
        last = at;
        points.Enqueue((at, value is float measured && float.IsFinite(measured) ? measured : null));
        while (points.TryPeek(out var first) && (at - first.At > Period || points.Count > 600)) points.Dequeue();
    }

    public void Clear()
    {
        points.Clear();
        source = null;
        last = null;
    }
}
