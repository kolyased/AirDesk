using System;

namespace AirDesk;

internal record SensorPoll(Telemetry Telemetry, int UpdatedDevices, int DeferredDevices, int FailedDevices);

// A session creates, uses and disposes its adapter on the same executor thread.
internal interface IMonitorHardware : IFanController, IDisposable
{
    bool RetryDiscovery { get; }
    void Open();
    SensorPoll Poll(TimeSpan elapsed, bool discover);
    FanSample Sample(Telemetry telemetry);
}
