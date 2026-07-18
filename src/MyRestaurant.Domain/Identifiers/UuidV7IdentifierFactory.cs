namespace MyRestaurant.Domain.Identifiers;

/// <summary>
/// UUIDv7 factory backed by the BCL's <see cref="Guid.CreateVersion7()"/> (.NET 9+),
/// which encodes a Unix-millisecond timestamp in the high bits so identifiers are
/// naturally time-ordered (ADR-0011).
/// </summary>
public sealed class UuidV7IdentifierFactory : IIdentifierFactory
{
    public Guid Create() => Guid.CreateVersion7();
}
