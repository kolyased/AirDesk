using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

public enum ThresholdDirection { Above, Below }
public record ThresholdRule(Guid Id, SensorReference Sensor, string Label, ThresholdDirection Direction,
    float Threshold, float Hysteresis, int HoldSeconds, int QuietSeconds)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valid => Id != Guid.Empty && Sensor != null && !string.IsNullOrWhiteSpace(Sensor.Id) &&
        !string.IsNullOrWhiteSpace(Label) && Label.Length <= 160 && Enum.IsDefined(Direction) && float.IsFinite(Threshold) &&
        Math.Abs(Threshold) <= 1_000_000 && float.IsFinite(Hysteresis) && Hysteresis is > 0 and <= 1_000_000 &&
        HoldSeconds is >= 0 and <= 1800 && QuietSeconds is >= 0 and <= 86400;
}

public sealed class ThresholdEvaluator
{
    sealed class State(ThresholdRule definition)
    {
        public readonly ThresholdRule Definition = definition;
        public DateTimeOffset? Since, LastSample, LastAlert;
        public bool Latched;
    }

    readonly Dictionary<Guid, State> states = new();

    public IEnumerable<(ThresholdRule Rule, Reading Reading)> Observe(IEnumerable<ThresholdRule> rules, Telemetry telemetry)
    {
        var configured = rules.Where(rule => rule.Valid).DistinctBy(rule => rule.Id).ToArray();
        foreach (Guid id in states.Keys.Except(configured.Select(rule => rule.Id)).ToArray()) states.Remove(id);
        foreach (var rule in configured)
        {
            if (!states.TryGetValue(rule.Id, out var state) || state.Definition != rule)
                states[rule.Id] = state = new State(rule);
            Reading? reading = telemetry.Find(rule.Sensor);
            if (reading is not { Available: true, UpdatedAt: { } time, Value: float number })
            {
                state.Since = state.LastSample = null;
                continue;
            }
            if (state.LastSample is { } last && time <= last) continue;
            if (state.LastSample is { } previous && time - previous > Telemetry.MaxAge(reading.DeviceType)) state.Since = null;
            state.LastSample = time;
            float delta = rule.Direction == ThresholdDirection.Above ? number - rule.Threshold : rule.Threshold - number;
            if (delta <= -rule.Hysteresis) state.Latched = false;
            if (delta < 0) { state.Since = null; continue; }
            state.Since ??= time;
            if (state.Latched || time - state.Since.Value < TimeSpan.FromSeconds(rule.HoldSeconds) ||
                state.LastAlert is { } sent && time - sent < TimeSpan.FromSeconds(rule.QuietSeconds)) continue;
            state.Latched = true;
            state.LastAlert = time;
            yield return (rule, reading);
        }
    }
}
