namespace MyRestaurant.Domain.Time;

/// <summary>The production <see cref="IClock"/>: the machine clock, in UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
