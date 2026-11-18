using System;
using AirDesk;

int scenarios = SafetyTests.Run() + DesktopLayoutTests.Run() + HistoryTests.Run() + ReleasePackageTests.Run();
int points = 0;
foreach (ProfileKind kind in new[] { ProfileKind.Quiet, ProfileKind.Balanced, ProfileKind.Performance })
{
    var curve = Curve.DefaultPoints(kind);
    if (!Curve.ValidPoints(curve)) throw new InvalidOperationException($"Invalid preset: {kind}");
    float previous = 0;
    for (int temperature = 0; temperature <= 100; ++temperature)
    {
        float demand = Curve.Interpolate(curve, temperature);
        if (demand < previous || demand > 100) throw new InvalidOperationException($"Invalid demand for {kind} at {temperature} °C");
        previous = demand;
        ++points;
    }
}
Console.WriteLine($"PASS: {scenarios} scenario checks and {points} preset points");
