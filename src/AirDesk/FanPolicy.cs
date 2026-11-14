using System;
namespace AirDesk;
public static class FanPolicy
{
    // Missing or implausible source => full cooling, never zero RPM.
    public static float Target(float? temperature, bool automatic, float manual)
    {
        if (!temperature.HasValue || !float.IsFinite(temperature.Value) || temperature < 0 || temperature >= 85) return 100;
        if (!automatic) return Math.Clamp(manual, 40, 100);
        float t = temperature.Value;
        if (t <= 40) return 40;
        if (t <= 60) return 40 + (t - 40) * 1.25f;
        if (t <= 75) return 65 + (t - 60) * (25f / 15);
        return 90 + (t - 75);
    }
}
