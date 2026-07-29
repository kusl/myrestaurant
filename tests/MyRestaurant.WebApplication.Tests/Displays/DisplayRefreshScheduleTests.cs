using MyRestaurant.Domain.Security;
using MyRestaurant.WebApplication.Displays;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Displays;

/// <summary>
/// Unit tests for <see cref="DisplayRefreshSchedule"/> (TECHNICAL_SPECIFICATION §4.3, §11.5).
///
/// <para>These two expressions decide whether a table display ever re-renders, and §11.5's own comment
/// names the consequence of getting it wrong: <em>a frozen QR looks exactly like a live one</em>. The
/// §16.3 scenarios watch a real boundary go past in a real browser, which is the right test for the
/// behaviour and a slow, coarse one for the arithmetic — a delay that lands a millisecond early is a
/// wasted pass there and a caught mistake here. Pure: no server, no container, no clock.</para>
/// </summary>
public sealed class DisplayRefreshScheduleTests
{
    private const int RotationSeconds = 20;

    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACodeRenderedAtTheStartOfItsWindow_WaitsTheWholeWindowAndThenSome()
    {
        // The case the previous implementation got wrong. A code minted at the boundary reports a
        // NextRotationAt one full rotation away; clamping the answer to exactly `rotation` would wake the
        // loop 250 ms BEFORE the boundary, re-render the window already on screen, and reach the new code
        // only on the pass after — a visibly late QR every single time, on a healthy display.
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now + TimeSpan.FromSeconds(RotationSeconds), RotationSeconds);

        Assert.Equal(TimeSpan.FromSeconds(RotationSeconds) + DisplayRefreshSchedule.BoundaryOvershoot, delay);
    }

    [Fact]
    public void TheDelayAlwaysLandsPastTheBoundary_NotOnIt()
    {
        // The property that makes the refresh produce the NEXT window's token rather than re-rendering
        // the current one. Asserted across the window instead of at one point, because the overshoot is
        // only useful if it survives the floor and the ceiling.
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
        // The whole point, stated in the domain's own terms: the code the refresh renders must belong to a
        // later window index than the code that prompted it.
        DateTimeOffset boundary = JoinTokenService.NextRotationInstant(Now, RotationSeconds);
        long windowOnScreen = JoinTokenService.CurrentWindowIndex(Now, RotationSeconds);

        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(Now, boundary, RotationSeconds);
        long windowAtWakeUp = JoinTokenService.CurrentWindowIndex(Now + delay, RotationSeconds);

        Assert.Equal(windowOnScreen + 1, windowAtWakeUp);
    }

    [Fact]
    public void ABoundaryMomentsAway_StillWaitsTheFloor()
    {
        // A refresh that arrives just before its own boundary must not busy-wait its way across it.
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now + TimeSpan.FromMilliseconds(1), RotationSeconds);

        Assert.Equal(DisplayRefreshSchedule.MinimumDelay, delay);
    }

    [Fact]
    public void ABoundaryAlreadyPast_WaitsTheFloorRatherThanSpinning()
    {
        // A clock that jumped forward, or a refresh that took longer than the window. Returning zero or a
        // negative delay here would turn the loop into a database hammer.
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now - TimeSpan.FromHours(3), RotationSeconds);

        Assert.Equal(DisplayRefreshSchedule.MinimumDelay, delay);
    }

    [Fact]
    public void ABoundaryAbsurdlyFarAhead_IsCappedAtOneWindow()
    {
        // A kiosk with no battery-backed clock can report a boundary years away. The cap is what stops
        // that from parking the refresh loop until the tablet is rebooted.
        TimeSpan delay = DisplayRefreshSchedule.DelayUntilNextRefresh(
            Now, Now + TimeSpan.FromDays(4000), RotationSeconds);

        Assert.Equal(TimeSpan.FromSeconds(RotationSeconds) + DisplayRefreshSchedule.BoundaryOvershoot, delay);
    }

    [Fact]
    public void NoCodeOnScreen_WaitsOneWindow()
    {
        // No QR came back — a secret that could not be read, or a table mid-deactivation. One rotation is
        // the longest a real window can be, so it retries without polling.
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
        // §13's floor is ten seconds and RestaurantOptions refuses to start below it, so this can only
        // arrive through a programming error — which should be loud rather than a division by zero deep
        // inside JoinTokenService.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DisplayRefreshSchedule.DelayUntilNextRefresh(Now, Now, rotationSeconds));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DisplayRefreshSchedule.FreshForMilliseconds(rotationSeconds));
    }

    [Theory]
    [InlineData(10, 20_000)]      // §13's floor
    [InlineData(20, 30_000)]      // the §16.3 boundary-watching scenarios
    [InlineData(60, 70_000)]      // the application's own default
    [InlineData(3600, 3_610_000)] // scenario 14's hour
    public void FreshForMilliseconds_IsOneWindowPlusTheSlack(int rotationSeconds, int expected)
    {
        // js/display.js falls back to 90 s when it cannot parse this, so the value has to be a positive
        // integer number of milliseconds for every rotation §13 permits.
        Assert.Equal(expected, DisplayRefreshSchedule.FreshForMilliseconds(rotationSeconds));
    }

    [Fact]
    public void FreshForMilliseconds_AlwaysOutlastsOneRotation()
    {
        // The invariant that keeps a healthy display from flickering into the offline state: the deadline
        // js/display.js measures must be longer than the interval between refreshes, or every screen in
        // the restaurant would raise the curtain once per window.
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
        // Both are slack, not policy. If either ever grew past a second, a display would be showing the
        // previous window's code for a visible slice of every window — which §4.3 tolerates (the previous
        // window still validates) but nobody should choose on purpose.
        Assert.InRange(DisplayRefreshSchedule.BoundaryOvershoot, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.InRange(DisplayRefreshSchedule.MinimumDelay, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // And the floor has to be at least the overshoot, or the floor could hand back a delay that lands
        // before the boundary the overshoot exists to clear.
        Assert.True(DisplayRefreshSchedule.MinimumDelay >= DisplayRefreshSchedule.BoundaryOvershoot);
    }
}
