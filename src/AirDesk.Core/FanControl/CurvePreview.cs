using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

internal sealed record CurvePreview(CurvePoint[] Configured, CurvePoint[] Limited)
{
    public const int MaximumTemperature = 125;

    public static CurvePoint[] ProfilePoints(FanProfile profile) => profile.Kind switch
    {
        ProfileKind.Manual => [new(0, profile.Manual), new(100, profile.Manual)],
        ProfileKind.Custom => profile.Custom,
        _ => Curve.DefaultPoints(profile.Kind)
    };

    public static CurvePreview Create(CurvePoint[]? points, float? floor)
    {
        if (!Curve.ValidPoints(points)) return new([], []);
        CurvePoint[] configured = [new(0, points![0].Percent), ..points.Where(point => point.Celsius > 0),
            new(MaximumTemperature, points[^1].Percent)];
        if (floor is not float minimum || !float.IsFinite(minimum) || minimum is < 0 or > 100)
            return new(configured, []);

        float emergency = FanPolicy.EmergencyTemperature;
        CurvePoint[] beforeEmergency = [..configured.Where(point => point.Celsius < emergency),
            new(emergency, Curve.Interpolate(points, emergency))];
        var limited = new List<CurvePoint>();
        for (int i = 0; i < beforeEmergency.Length; i++)
        {
            CurvePoint next = beforeEmergency[i];
            if (i > 0)
            {
                CurvePoint prior = beforeEmergency[i - 1];
                if (prior.Percent < minimum && next.Percent > minimum)
                {
                    float fraction = (minimum - prior.Percent) / (next.Percent - prior.Percent);
                    limited.Add(new(prior.Celsius + fraction * (next.Celsius - prior.Celsius), minimum));
                }
            }
            limited.Add(next with { Percent = Math.Max(next.Percent, minimum) });
        }
        limited.Add(new(emergency, 100));
        limited.Add(new(MaximumTemperature, 100));
        return new(configured, limited.ToArray());
    }
}
