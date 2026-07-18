namespace MyRestaurant.Domain.Time;

/// <summary>
/// The single source of "now" for the domain. Everything is stored in UTC
/// (REQUIREMENTS §8); rendering in <c>RESTAURANT_TIME_ZONE</c> is a UI concern.
/// Injected everywhere so tests can pin time deterministically.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
