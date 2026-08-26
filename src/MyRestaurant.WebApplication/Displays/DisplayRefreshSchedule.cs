namespace MyRestaurant.WebApplication.Displays;

public static class DisplayRefreshSchedule
{
    public static readonly TimeSpan BoundaryOvershoot = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan StalenessSlack = TimeSpan.FromSeconds(10);

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

        long delayTicks = boundary.UtcTicks - now.UtcTicks + BoundaryOvershoot.Ticks;

        if (delayTicks < MinimumDelay.Ticks)
        {
            return MinimumDelay;
        }

        return delayTicks > ceiling.Ticks ? ceiling : TimeSpan.FromTicks(delayTicks);
    }

    public static int FreshForMilliseconds(int rotationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rotationSeconds, 1);

        double milliseconds = (TimeSpan.FromSeconds(rotationSeconds) + StalenessSlack).TotalMilliseconds;

        return milliseconds >= int.MaxValue ? int.MaxValue : (int)milliseconds;
    }
}
