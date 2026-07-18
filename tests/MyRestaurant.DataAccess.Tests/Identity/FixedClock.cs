using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>A deterministic <see cref="IClock"/> for tests; <see cref="UtcNow"/> is settable.</summary>
internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }
}
