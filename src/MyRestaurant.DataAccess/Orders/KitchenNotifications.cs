using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Orders;

public sealed record KitchenReminderIssued(
    Guid OrderEventIdentifier,
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier);

public interface IKitchenNotifications
{
    Task<IReadOnlyList<KitchenReminderIssued>> IssueDueRemindersAsync(
        int reminderSeconds,
        CancellationToken cancellationToken = default);
}

public sealed class DapperKitchenNotifications : IKitchenNotifications
{
    private static readonly string DueSubmissionsSql = $"""
        SELECT submission.order_event_identifier    AS OrderEventIdentifier,
               submission.guest_order_identifier    AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier
        FROM order_event AS submission
        INNER JOIN guest_order
                ON guest_order.guest_order_identifier = submission.guest_order_identifier
        INNER JOIN table_sitting
                ON table_sitting.table_sitting_identifier = guest_order.table_sitting_identifier
        WHERE submission.event_type = '{OrderEventVocabulary.GuestSubmission}'
          AND table_sitting.closed_at IS NULL
          AND submission.occurred_at < @DueBefore
          AND EXISTS (SELECT 1
                      FROM order_operation_line_added AS added
                      WHERE added.order_event_identifier = submission.order_event_identifier)
          AND NOT EXISTS (SELECT 1
                          FROM kitchen_notification AS prior
                          WHERE prior.order_event_identifier = submission.order_event_identifier
                            AND prior.kind = '{OrderEventVocabulary.ReminderNotification}')
          AND NOT EXISTS (
              SELECT 1
              FROM order_operation_line_added AS added
              WHERE added.order_event_identifier = submission.order_event_identifier
                AND (EXISTS (SELECT 1
                             FROM order_operation_line_fulfilled AS fulfilled
                             WHERE fulfilled.order_line_identifier = added.order_line_identifier)
                  OR EXISTS (SELECT 1
                             FROM order_operation_line_removed AS removed
                             WHERE removed.order_line_identifier = added.order_line_identifier)))
        ORDER BY submission.occurred_at, submission.order_event_identifier;
        """;

    private const string InsertReminderSql = """
        INSERT INTO kitchen_notification (
            kitchen_notification_identifier, order_event_identifier, event_type, kind, created_at)
        VALUES (@KitchenNotificationIdentifier, @OrderEventIdentifier, @EventType, @Kind, @CreatedAt)
        ON CONFLICT (order_event_identifier, kind) DO NOTHING
        RETURNING kitchen_notification_identifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperKitchenNotifications(
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

    public async Task<IReadOnlyList<KitchenReminderIssued>> IssueDueRemindersAsync(
        int reminderSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reminderSeconds, 1);

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset dueBefore = now - TimeSpan.FromSeconds(reminderSeconds);

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<DueSubmissionRow> due = await connection
            .QueryAsync<DueSubmissionRow>(new CommandDefinition(
                DueSubmissionsSql,
                new { DueBefore = dueBefore },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        List<KitchenReminderIssued> issued = [];

        foreach (DueSubmissionRow submission in due)
        {
            Guid? written = await connection
                .ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    InsertReminderSql,
                    new
                    {
                        KitchenNotificationIdentifier = _identifierFactory.Create(),
                        submission.OrderEventIdentifier,

                        EventType = OrderEventVocabulary.GuestSubmission,
                        Kind = OrderEventVocabulary.ReminderNotification,
                        CreatedAt = now,
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (written is not null)
            {
                issued.Add(new KitchenReminderIssued(
                    submission.OrderEventIdentifier,
                    submission.GuestOrderIdentifier,
                    submission.SittingIdentifier));
            }
        }

        return issued;
    }

    private sealed record DueSubmissionRow(
        Guid OrderEventIdentifier,
        Guid GuestOrderIdentifier,
        Guid SittingIdentifier);
}
