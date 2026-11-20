using System;
using AirDesk;
using Xunit;

namespace AirDesk.Tests;

public sealed class CurveTests
{
    [Fact]
    public void BalancedCurveRisesUnderLoadAndEmergencyBypassesSmoothing()
    {
        var response = new CurveResponse();
        var profile = FanProfile.Default;

        Assert.Equal(40, response.Target(profile, 40, 40, 40, TimeSpan.Zero));
        Assert.Equal(55, response.Target(profile, 40, 75, 75, TimeSpan.FromSeconds(1)));
        Assert.Equal(70, response.Target(profile, 40, 75, 75, TimeSpan.FromSeconds(2)));
        Assert.Equal(85, response.Target(profile, 40, 75, 75, TimeSpan.FromSeconds(3)));
        Assert.Equal(90, response.Target(profile, 40, 75, 75, TimeSpan.FromSeconds(4)));
        Assert.Equal(100, response.Target(profile, 40, 85, 85, TimeSpan.FromSeconds(4.25)));
    }

    [Theory]
    [InlineData(ProfileKind.Quiet)]
    [InlineData(ProfileKind.Balanced)]
    [InlineData(ProfileKind.Performance)]
    public void PresetsAreMonotonicAndBounded(ProfileKind kind)
    {
        var points = Curve.DefaultPoints(kind);
        Assert.True(Curve.ValidPoints(points));
        float previous = 0;
        for (int temperature = 0; temperature <= 100; temperature++)
        {
            float value = Curve.Interpolate(points, temperature);
            Assert.InRange(value, previous, 100);
            previous = value;
        }
        Assert.Equal(points[0].Percent, Curve.Interpolate(points, -30));
        Assert.Equal(points[^1].Percent, Curve.Interpolate(points, 125));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidCurveInputsAreRejected(float invalid)
    {
        Assert.False(Curve.ValidPoints([new(35, 50), new(invalid, 60)]));
        Assert.False(Curve.ValidPoints([new(35, invalid), new(60, 80)]));
    }
}
