using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Identity;

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

        if (!SecurityEventType.IsKnown(eventType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventType),
                eventType,
                "Unknown security event type; it must be one of MyRestaurant.Domain.Authentication.SecurityEventType.");
        }

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
