using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// The Dapper-backed <see cref="ISecurityEventLog"/> over the <c>security_event</c> table
/// (TECHNICAL_SPECIFICATION §8.2). Like <see cref="DapperUserStore"/> it holds no connection: each call
/// opens one from the injected factory and disposes it, so a single instance is safe for the scoped
/// Identity lifetime. Identifiers are application-generated UUIDv7 (ADR-0011); timestamps come from the
/// injected <see cref="IClock"/> so tests are deterministic.
/// </summary>
public sealed class DapperSecurityEventLog : ISecurityEventLog
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperSecurityEventLog(
        IDatabaseConnectionFactory connectionFactory,
        IClock clock,
        IIdentifierFactory identifierFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(identifierFactory);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;
    }

    public async Task RecordAsync(
        Guid subjectPersonIdentifier,
        Guid? actorPersonIdentifier,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);

        // Fail fast with a clear message: the column has a CHECK for exactly this set, so a bad value
        // would otherwise surface as an opaque PostgreSQL CheckViolation at write time.
        if (!SecurityEventType.IsKnown(eventType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventType),
                eventType,
                "Unknown security event type; it must be one of MyRestaurant.Domain.Authentication.SecurityEventType.");
        }

        // Cast the nullable actor to uuid so Npgsql resolves the parameter type whether the value is a
        // Guid or NULL (mirrors the '::citext' casts in DapperUserStore). The subject is always present.
        const string sql = """
            INSERT INTO security_event (
                security_event_identifier, subject_person_identifier, actor_person_identifier,
                event_type, occurred_at)
            VALUES (
                @SecurityEventIdentifier, @SubjectPersonIdentifier, @ActorPersonIdentifier::uuid,
                @EventType, @OccurredAt);
            """;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                SecurityEventIdentifier = _identifierFactory.Create(),
                SubjectPersonIdentifier = subjectPersonIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                OccurredAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
