using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

public enum HistoryRetention { Off, Hour, Day, Week, Month }
public record HistoryPoint(SensorReference Sensor, string Name, string Unit, DateTimeOffset From, DateTimeOffset At,
    float Mean, float Min, float Max, int Count, int IntervalSeconds)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valid => Sensor != null && !string.IsNullOrEmpty(Sensor.Id) && !string.IsNullOrEmpty(Sensor.DeviceId) &&
        !string.IsNullOrEmpty(Sensor.DeviceName) && Name != null && Unit != null && From <= At && Count is >= 1 and <= 100_000 &&
        IntervalSeconds is >= 1 and <= 3600 && float.IsFinite(Min) && float.IsFinite(Max) && float.IsFinite(Mean) && Min <= Mean && Mean <= Max;
}

public static class HistoryModel
{
    public const int MaxPoints = 16_000;
    public const int MaxBytes = 4 * 1024 * 1024;

    public static TimeSpan Period(HistoryRetention retention) => retention switch
    {
        HistoryRetention.Hour => TimeSpan.FromHours(1), HistoryRetention.Day => TimeSpan.FromDays(1),
        HistoryRetention.Week => TimeSpan.FromDays(7), HistoryRetention.Month => TimeSpan.FromDays(30), _ => TimeSpan.Zero
    };

    public static int Interval(HistoryRetention retention) => retention switch
    {
        HistoryRetention.Hour => 10, HistoryRetention.Day => 30, HistoryRetention.Week => 60, HistoryRetention.Month => 120, _ => 0
    };

    public static HistoryPoint[] Compact(IEnumerable<HistoryPoint> source, DateTimeOffset now, HistoryRetention retention)
    {
        if (retention == HistoryRetention.Off) return [];
        var cutoff = now - Period(retention);
        return source.Where(point => point != null && point.Valid && point.At >= cutoff && point.At <= now.AddMinutes(1))
            .GroupBy(point => point.Sensor).SelectMany(group => Rollup(group.OrderBy(point => point.From), now))
            .OrderBy(point => point.At).TakeLast(MaxPoints).ToArray();
    }

    static IEnumerable<HistoryPoint> Rollup(IEnumerable<HistoryPoint> points, DateTimeOffset now)
    {
        HistoryPoint? aggregate = null;
        long bucket = long.MinValue;
        foreach (var point in points)
        {
            long seconds = now - point.At >= TimeSpan.FromDays(1) ? 1800 : now - point.At >= TimeSpan.FromHours(1) ? 300 : 0;
            long nextBucket = seconds == 0 ? long.MaxValue : point.At.ToUnixTimeSeconds() / seconds * seconds;
            if (aggregate != null && seconds > 0 && bucket == nextBucket && Continuous(aggregate, point))
            {
                int count = aggregate.Count + point.Count;
                aggregate = aggregate with { At = point.At, Count = count,
                    Mean = (float)(((double)aggregate.Mean * aggregate.Count + (double)point.Mean * point.Count) / count),
                    Min = Math.Min(aggregate.Min, point.Min), Max = Math.Max(aggregate.Max, point.Max),
                    IntervalSeconds = Math.Max(aggregate.IntervalSeconds, point.IntervalSeconds) };
            }
            else
            {
                if (aggregate != null) yield return aggregate;
                aggregate = point;
                bucket = nextBucket;
            }
        }
        if (aggregate != null) yield return aggregate;
    }

    public static bool Continuous(HistoryPoint previous, HistoryPoint next) => next.From >= previous.At &&
        next.From - previous.At <= TimeSpan.FromSeconds(Math.Max(previous.IntervalSeconds, next.IntervalSeconds) * 2.5);
}
