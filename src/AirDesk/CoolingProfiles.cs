using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

public enum ProfileKind { Quiet, Balanced, Performance, Custom, Manual }
public record CurvePoint(float Celsius, float Percent);
public record SensorReference(string Id, string DeviceId, string DeviceName);
public record TemperatureInput(SensorReference Source, string Label, float? Celsius);
public record FanProfile(ProfileKind Kind, float Manual, CurvePoint[] Custom, SensorReference? Primary, SensorReference? Secondary)
{
    public static FanProfile Default => new(ProfileKind.Balanced, 100, Curve.DefaultPoints(ProfileKind.Balanced), null, null);

    public bool SameSettings(FanProfile? other) => other != null && Kind == other.Kind && Manual == other.Manual &&
        Primary == other.Primary && Secondary == other.Secondary && Custom.SequenceEqual(other.Custom);
}

public record SavedProfile(ChannelIdentity Channel, FanProfile Current, FanProfile? Previous,
    FanProfile? Startup = null, FanProfile? LastConfirmed = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public FanProfile StartupSelection => Startup ?? Current;

    public SavedProfile Select(FanProfile profile, bool confirmed) => this with
    {
        Current = profile,
        Previous = Current.SameSettings(profile) ? Previous : Current,
        LastConfirmed = confirmed ? profile : LastConfirmed
    };
}

public static class Curve
{
    public static CurvePoint[] DefaultPoints(ProfileKind kind) => kind switch
    {
        ProfileKind.Quiet => [new(35, 40), new(55, 50), new(70, 70), new(80, 90), new(85, 100)],
        ProfileKind.Performance => [new(35, 60), new(50, 75), new(65, 90), new(80, 100), new(85, 100)],
        _ => [new(40, 40), new(60, 65), new(75, 90), new(85, 100)]
    };

    public static bool Valid(FanProfile profile) => Enum.IsDefined(profile.Kind) && float.IsFinite(profile.Manual) &&
        profile.Manual is >= 0 and <= 100 && ValidPoints(profile.Custom) &&
        (profile.Secondary == null || profile.Primary != profile.Secondary);

    public static bool ValidPoints(CurvePoint[]? points)
    {
        if (points is not { Length: >= 2 and <= 12 }) return false;
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (p == null || !float.IsFinite(p.Celsius) || !float.IsFinite(p.Percent) || p.Celsius is < 0 or > 100 || p.Percent is < 0 or > 100)
                return false;
            if (i > 0 && (p.Celsius <= points[i - 1].Celsius || p.Percent < points[i - 1].Percent)) return false;
        }
        return true;
    }

    public static float Interpolate(CurvePoint[] points, float celsius)
    {
        if (celsius <= points[0].Celsius) return points[0].Percent;
        for (int i = 1; i < points.Length; i++)
        {
            var left = points[i - 1];
            var right = points[i];
            if (celsius <= right.Celsius)
                return left.Percent + (right.Percent - left.Percent) * (celsius - left.Celsius) / (right.Celsius - left.Celsius);
        }
        return points[^1].Percent;
    }

    public static CurvePoint ConstrainPoint(CurvePoint[] points, int index, float celsius, float percent)
    {
        var left = index > 0 ? points[index - 1] : null;
        var right = index < points.Length - 1 ? points[index + 1] : null;
        float minTemperature = left == null ? 0 : left.Celsius + .1f;
        float maxTemperature = right == null ? 100 : right.Celsius - .1f;
        var point = points[index];
        return new(minTemperature <= maxTemperature ? Math.Clamp(celsius, minTemperature, maxTemperature) : point.Celsius,
            Math.Clamp(percent, left?.Percent ?? 0, right?.Percent ?? 100));
    }

    public static (float? Temperature, string Label) Resolve(FanProfile profile, float? cpu, IReadOnlyCollection<TemperatureInput> inputs)
    {
        TemperatureInput? Find(SensorReference source) => inputs.SingleOrDefault(input => input.Source == source);
        var first = profile.Primary == null ? null : Find(profile.Primary);
        float? primary = profile.Primary == null ? cpu : first?.Celsius;
        string primaryLabel = profile.Primary == null ? "CPU Package (авто)" : first?.Label ?? profile.Primary.Id;
        if (profile.Secondary == null) return (primary, primaryLabel);
        var second = Find(profile.Secondary);
        string label = $"Максимум из {primaryLabel} и {second?.Label ?? profile.Secondary.Id}";
        return primary.HasValue && second?.Celsius is float secondary ? (Math.Max(primary.Value, secondary), label) : (null, label);
    }
}

internal sealed class CurveResponse
{
    float? output, peakTemperature;
    TimeSpan last, startFall;
    bool falling;
    bool explicitChange, emergency;

    public void Reset() { output = peakTemperature = null; falling = explicitChange = emergency = false; }
    public void ProfileChanged() { falling = false; explicitChange = true; }

    public float Target(FanProfile profile, float floor, float cpu, float source, TimeSpan now)
    {
        float target = profile.Kind == ProfileKind.Manual ? profile.Manual :
            Curve.Interpolate(profile.Kind == ProfileKind.Custom ? profile.Custom : Curve.DefaultPoints(profile.Kind), source);
        target = Math.Clamp(target, floor, 100);
        double seconds = Math.Clamp((now - last).TotalSeconds, 0, 2);
        last = now;
        if (cpu >= FanPolicy.EmergencyTemperature || source >= FanPolicy.EmergencyTemperature ||
            emergency && (cpu > FanPolicy.EmergencyReleaseTemperature || source > FanPolicy.EmergencyReleaseTemperature))
        {
            emergency = true;
            output = 100;
            peakTemperature = source;
            falling = explicitChange = false;
            return 100;
        }
        if (emergency)
        {
            emergency = false;
            ProfileChanged();
        }
        if (profile.Kind == ProfileKind.Manual || !output.HasValue)
        {
            output = target;
            peakTemperature = source;
            falling = explicitChange = false;
            return (float)Math.Ceiling(output.Value);
        }
        if (target > output)
        {
            output = Math.Min(target, output.Value + (float)(seconds * 15));
            peakTemperature = source;
            falling = explicitChange = false;
        }
        else if (target < output && (explicitChange || source <= peakTemperature - 2))
        {
            if (!falling) { startFall = now; falling = true; }
            if (now - startFall >= TimeSpan.FromSeconds(4)) output = Math.Max(target, output.Value - (float)(seconds * 5));
        }
        else falling = explicitChange = false;
        return (float)Math.Ceiling(Math.Max(floor, output.Value));
    }
}
