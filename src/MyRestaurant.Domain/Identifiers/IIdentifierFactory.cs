namespace MyRestaurant.Domain.Identifiers;

/// <summary>
/// Produces primary keys. All identifiers are application-generated UUIDv7 (ADR-0011,
/// TECHNICAL_SPECIFICATION §8.1) — never database defaults — so rows sort by creation time
/// and the application owns identity before the row is written.
/// </summary>
public interface IIdentifierFactory
{
    Guid Create();
}
