using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Orders;

/// <summary>
/// One reminder that this process actually wrote (TECHNICAL_SPECIFICATION §8.4, §10.2). It is returned
/// only when the <c>INSERT … ON CONFLICT DO NOTHING</c> took, so a caller may broadcast one alert per
/// element without checking anything further — which is exactly the rule §8.4 states.
/// </summary>
public sealed record KitchenReminderIssued(
    Guid OrderEventIdentifier,
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier);

/// <summary>
/// The reminder half of kitchen alerting (TECHNICAL_SPECIFICATION §8.4, §10.2). The <em>initial</em>
/// alert is not here: §10.1 requires its <c>kitchen_notification</c> row to be written inside the same
/// transaction as the event that caused it, so it lives in <see cref="DapperOrderMutations"/> and cannot
/// be moved out without breaking that guarantee.
///
/// <para>A reminder is the opposite shape — nobody did anything, and that is the point: it fires because
/// a guest's send has sat untouched past <c>KITCHEN_SUBMISSION_REMINDER_SECONDS</c>. So it is a periodic
/// scan rather than a consequence of a write, and it belongs to a background service (§10.2), which is
/// its only caller.</para>
/// </summary>
public interface IKitchenNotifications
{
    /// <summary>
    /// Runs the §8.4 scan and writes a <c>reminder</c> row for every hit, returning only the ones this
    /// call actually inserted.
    ///
    /// <para>§8.4's rule in full: exactly one reminder per guest send, at most; only while the sitting
    /// is open; only if the send added at least one line; and only if none of the lines that send added
    /// has since been fulfilled or removed. A pure-removal send alerts once under §10.1 and never
    /// reminds. The <c>UNIQUE (order_event_identifier, kind)</c> constraint, not this code, is what
    /// makes the whole thing safe against two scans overlapping.</para>
    /// </summary>
    /// <param name="reminderSeconds">
    /// <c>KITCHEN_SUBMISSION_REMINDER_SECONDS</c> (§13) — how long a send may sit before it reminds.
    /// </param>
    Task<IReadOnlyList<KitchenReminderIssued>> IssueDueRemindersAsync(
        int reminderSeconds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IKitchenNotifications"/>: the §8.4 SELECT, then one guarded
/// INSERT per hit, all on one connection and deliberately <b>not</b> in one transaction — each insert
/// stands alone, and holding a transaction open across a scan would only lengthen the window in which a
/// concurrent order mutation waits on nothing.
///
/// <para><b>One documented deviation from §8.4's literal SQL.</b> The specification writes
/// <c>submission.occurred_at &lt; now() - make_interval(secs =&gt; :reminder_seconds)</c>. This computes
/// the same threshold from <see cref="IClock.UtcNow"/> and binds it as <c>@DueBefore</c> instead. The
/// reason is that <c>occurred_at</c> was stamped by the application clock, not by the database's, so
/// comparing it against the database's <c>now()</c> compares two clocks — which is invisible in a
/// deployment where both containers share a host clock, and is wrong the moment they do not. It also
/// makes the rule testable: with a fixed clock a test can put a send either side of the threshold, which
/// against <c>now()</c> is impossible. Everything else — the four EXISTS/NOT EXISTS clauses, the open
/// sitting, the ordering — is §8.4 verbatim.</para>
/// </summary>
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

    /// <summary>
    /// §8.4: "broadcast <c>KitchenAlert(reminder)</c> <b>only if the insert took</b> (rowcount 1)".
    /// <c>RETURNING</c> is how that is observed — a swallowed conflict returns no row, so the scalar is
    /// <c>null</c> and the caller learns that some other scan got there first.
    /// </summary>
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

        // One instant for the whole scan, like every other operation in this layer: the threshold and
        // the created_at stamped on any row it writes come from the same reading of the clock.
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
                        // The composite FK is (order_event_identifier, event_type), so the row has to
                        // restate the event's type; §10.2 reminders exist only for guest submissions.
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
