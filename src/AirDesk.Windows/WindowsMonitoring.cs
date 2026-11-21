namespace AirDesk;

internal static class WindowsMonitoring
{
    public static Monitor Create(bool monitorOnly) => new(() => new LibreMonitorHardware(), () => new LocalCalibration(),
        ControllerProcesses.Scan, monitorOnly);
}
