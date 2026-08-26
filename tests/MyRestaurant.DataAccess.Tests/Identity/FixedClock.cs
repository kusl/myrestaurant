using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Tests.Identity;

internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }
}
