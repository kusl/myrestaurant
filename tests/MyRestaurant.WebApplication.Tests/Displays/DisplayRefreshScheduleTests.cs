using MyRestaurant.Domain.Security;
using MyRestaurant.WebApplication.Displays;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Displays;

public sealed class DisplayRefreshScheduleTests
{
    private const int RotationSeconds = 20;

    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACodeRenderedAtTheStartOfItsWindow_WaitsTheWholeWindowAndThenSome()
    {
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now + TimeSpan.FromSeconds(RotationSeconds), RotationSeconds);

        Assert.Equal(TimeSpan.FromSeconds(RotationSeconds) + DisplayRefreshSchedule.BoundaryOvershoot, delay);
    }

    [Fact]
    public void TheDelayAlwaysLandsPastTheBoundary_NotOnIt()
    {
        for (int secondsIntoWindow = 0; secondsIntoWindow < RotationSeconds; secondsIntoWindow++)
        {
            DateTimeOffset observedAt = Now + TimeSpan.FromSeconds(secondsIntoWindow);
            DateTimeOffset boundary = Now + TimeSpan.FromSeconds(RotationSeconds);

            TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(observedAt, boundary, RotationSeconds);

            Assert.True(
                observedAt + delay > boundary,
                $"waking {delay} after a read {secondsIntoWindow}s into the window lands at or before the boundary");
        }
    }

    [Fact]
    public void AWakeUpLandsInTheWindowAfterTheOneOnScreen()
    {
        DateTimeOffset boundary = JoinTokenService.NextRotationInstant(Now, RotationSeconds);
        long windowOnScreen = JoinTokenService.CurrentWindowIndex(Now, RotationSeconds);

        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(Now, boundary, RotationSeconds);
        long windowAtWakeUp = JoinTokenService.CurrentWindowIndex(Now + delay, RotationSeconds);

        Assert.Equal(windowOnScreen + 1, windowAtWakeUp);
    }

    [Fact]
    public void ABoundaryMomentsAway_StillWaitsTheFloor()
    {
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now + TimeSpan.FromMilliseconds(1), RotationSeconds);

        Assert.Equal(DisplayRefreshSchedule.MinimumDelay, delay);
    }

    [Fact]
    public void ABoundaryAlreadyPast_WaitsTheFloorRatherThanSpinning()
    {
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now - TimeSpan.FromHours(3), RotationSeconds);

        Assert.Equal(DisplayRefreshSchedule.MinimumDelay, delay);
    }

    [Fact]
    public void ABoundaryAbsurdlyFarAhead_IsCappedAtOneWindow()
    {
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now + TimeSpan.FromDays(4000), RotationSeconds);

        Assert.Equal(TimeSpan.FromSeconds(RotationSeconds) + DisplayRefreshSchedule.BoundaryOvershoot, delay);
    }

    [Fact]
    public void NoCodeOnScreen_WaitsOneWindow()
    {
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(Now, nextRotationAt: null, RotationSeconds);

        Assert.Equal(TimeSpan.FromSeconds(RotationSeconds) + DisplayRefreshSchedule.BoundaryOvershoot, delay);
    }

    [Fact]
    public void TheDelayIsNeverZeroOrNegative_ForAnyBoundaryAtAll()
    {
        DateTimeOffset[] boundaries =
        [
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            Now,
            Now - TimeSpan.FromTicks(1),
            Now + TimeSpan.FromTicks(1),
        ];

        foreach (DateTimeOffset boundary in boundaries)
        {
            TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(Now, boundary, RotationSeconds);

            Assert.InRange(
                delay,
                DisplayRefreshSchedule.MinimumDelay,
                TimeSpan.FromSeconds(RotationSeconds) + DisplayRefreshSchedule.BoundaryOvershoot);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARotationOfZeroOrLess_IsRejected(int rotationSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DisplayRefreshSchedule.DelayUntilNextRefresh(Now, Now, rotationSeconds));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DisplayRefreshSchedule.FreshForMilliseconds(rotationSeconds));
    }

    [Theory]
    [InlineData(10, 20_000)]
    [InlineData(20, 30_000)]
    [InlineData(60, 70_000)]
    [InlineData(3600, 3_610_000)]
    public void FreshForMilliseconds_IsOneWindowPlusTheSlack(int rotationSeconds, int expected)
    {
        Assert.Equal(expected, DisplayRefreshSchedule.FreshForMilliseconds(rotationSeconds));
    }

    [Fact]
    public void FreshForMilliseconds_AlwaysOutlastsOneRotation()
    {
        int[] rotations = [10, 11, 20, 45, 60, 120, 3600, 86_400];

        foreach (int rotationSeconds in rotations)
        {
            TimeSpan freshFor = TimeSpan.FromMilliseconds(
                DisplayRefreshSchedule.FreshForMilliseconds(rotationSeconds));
            TimeSpan longestDelay = DisplayRefreshSchedule.DelayUntilNextRefresh(
                Now, nextRotationAt: null, rotationSeconds);

            Assert.True(
                freshFor > longestDelay,
                $"a {rotationSeconds}s rotation goes stale ({freshFor}) before its own refresh ({longestDelay})");
        }
    }

    [Fact]
    public void TheOvershootAndFloorAreSmallEnoughToBeInvisible()
    {
        Assert.InRange(DisplayRefreshSchedule.BoundaryOvershoot, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.InRange(DisplayRefreshSchedule.MinimumDelay, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.True(DisplayRefreshSchedule.MinimumDelay >= DisplayRefreshSchedule.BoundaryOvershoot);
    }
}
