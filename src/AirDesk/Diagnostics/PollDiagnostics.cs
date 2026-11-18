using System;

namespace AirDesk;

public record PollDiagnostics(long Polls, long MissedCycles, double LastMilliseconds, double AverageMilliseconds, double PeakMilliseconds,
    int UpdatedDevices, int DeferredDevices, int FailedDevices)
{
    public static PollDiagnostics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    public PollDiagnostics Observe(TimeSpan duration, long missed, SensorPoll samples) => new(Polls + 1, MissedCycles + missed,
        duration.TotalMilliseconds, (AverageMilliseconds * Polls + duration.TotalMilliseconds) / (Polls + 1),
        Math.Max(PeakMilliseconds, duration.TotalMilliseconds), samples.UpdatedDevices, samples.DeferredDevices, samples.FailedDevices);
}
