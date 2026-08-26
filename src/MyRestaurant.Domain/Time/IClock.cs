namespace MyRestaurant.Domain.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
