namespace MyRestaurant.WebApplication.Displays;

/// <summary>
/// The arithmetic behind the table display's window-aligned refresh (TECHNICAL_SPECIFICATION §4.3: "the
/// display re-renders on a server timer aligned to the window boundary — fire at
/// <c>(window_index+1) × rotation</c>", and §11.5's staleness contract).
///
/// <para><b>Why this is a type rather than two private methods on the Razor page.</b> These two
/// expressions decide whether a display ever refreshes at all, and a display that stops refreshing is
/// §11.5's worst failure — "a frozen QR looks exactly like a live one". Anything that important should be
/// covered by a test that runs in milliseconds rather than only by a Playwright scenario that has to
/// watch a real boundary go past. Everything else about the surface needs a circuit; this does not.</para>
///
/// <para>Both members are pure and take the clock read as an argument, so the caller keeps §14's "one
/// <c>IClock.UtcNow</c> instant per operation" discipline and nothing here has an opinion about time.</para>
/// </summary>
public static class DisplayRefreshSchedule
{
    /// <summary>
    /// How far past the boundary the refresh is deliberately aimed. Without it, a delay computed as
    /// exactly <c>boundary - now</c> can land a hair <em>before</em> the boundary — thanks to rounding,
    /// timer granularity, or a millisecond of scheduling — and re-render the window that is already on
    /// screen, wasting a pass and leaving the new code late.
    /// </summary>
    public static readonly TimeSpan BoundaryOvershoot = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The floor. A boundary already in the past (a clock that jumped forward, a refresh that took longer
    /// than the window) must not turn the loop into a spin; it waits half a second and tries again.
    /// </summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The slack added to one rotation before a rendered code is treated as untrustworthy by
    /// <c>js/display.js</c>. One rotation covers the healthy case — the loop refreshes within a fraction
    /// of a second of the boundary — and this is room for a slow round trip, so a working display never
    /// flickers into the offline state.
    /// </summary>
    public static readonly TimeSpan StalenessSlack = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait before re-rendering, given the instant the code currently on screen rotates.
    ///
    /// <para><paramref name="nextRotationAt"/> is <c>null</c> when there is no code on screen to take a
    /// boundary from — a table whose secret could not be read, or the very first pass — in which case one
    /// full rotation from now is the right guess: it is the longest a real window can be, so it cannot
    /// busy-poll a database that is having a bad minute.</para>
    ///
    /// <para>The ceiling is one rotation <em>plus</em> the overshoot rather than one rotation flat. That
    /// distinction matters: a code rendered at the very start of its window legitimately wants
    /// <c>rotation + overshoot</c>, and clamping that to <c>rotation</c> would wake the loop just before
    /// the boundary, re-render the same window, and only reach the new code on the pass after. The
    /// ceiling exists to stop a clock that jumped backwards from parking the loop for hours, not to
    /// second-guess an ordinary full-window wait.</para>
    /// </summary>
    /// <param name="now">The current UTC instant, read once by the caller.</param>
    /// <param name="nextRotationAt">
    /// <c>TableJoinQrCode.NextRotationAt</c> for the code on screen, or <c>null</c> when there is none.
    /// </param>
    /// <param name="rotationSeconds">The configured <c>TABLE_JOIN_TOKEN_ROTATION_SECONDS</c> (§13).</param>
    public static TimeSpan DelayUntilNextRefresh(
        DateTimeOffset now,
        DateTimeOffset? nextRotationAt,
        int rotationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rotationSeconds, 1);

        TimeSpan rotation = TimeSpan.FromSeconds(rotationSeconds);
        TimeSpan ceiling = rotation + BoundaryOvershoot;

        if (nextRotationAt is not { } boundary)
        {
            return ceiling;
        }

        // Computed in ticks so an absurd boundary — a clock read from a device with no battery-backed
        // RTC, say — cannot overflow the subtraction before the clamp gets a chance to reject it.
        long delayTicks = boundary.UtcTicks - now.UtcTicks + BoundaryOvershoot.Ticks;

        if (delayTicks < MinimumDelay.Ticks)
        {
            return MinimumDelay;
        }

        return delayTicks > ceiling.Ticks ? ceiling : TimeSpan.FromTicks(delayTicks);
    }

    /// <summary>
    /// How long a rendered code stays trustworthy, for <c>js/display.js</c>'s <c>data-fresh-for-ms</c>
    /// (§11.5). The script measures the deadline from when it observed the token change, never from a
    /// server timestamp, so a kiosk with a badly wrong clock still detects a dead circuit.
    /// </summary>
    public static int FreshForMilliseconds(int rotationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rotationSeconds, 1);

        // Saturating rather than wrapping: an absurd rotation window is a configuration problem, but a
        // negative data-fresh-for-ms would make js/display.js declare a healthy display stale on its
        // first tick, which is a worse outcome than a deadline nobody will ever reach.
        double milliseconds = (TimeSpan.FromSeconds(rotationSeconds) + StalenessSlack).TotalMilliseconds;

        return milliseconds >= int.MaxValue ? int.MaxValue : (int)milliseconds;
    }
}
