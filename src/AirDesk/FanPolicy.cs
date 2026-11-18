using System;

namespace AirDesk;

public static class FanPolicy
{
    public const float EmergencyTemperature = 85;
    public const float EmergencyReleaseTemperature = 83;
    public static bool ValidTemperature(float? value) => value is float measured && float.IsFinite(measured) && measured is >= 0 and <= 125;
}
