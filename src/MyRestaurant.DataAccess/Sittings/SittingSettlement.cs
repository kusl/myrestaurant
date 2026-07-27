using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Sittings;

/// <summary>What happened to a request to close and settle a sitting (TECHNICAL_SPECIFICATION §5.3).</summary>
public enum CloseSittingOutcome
{
    /// <summary>The sitting was open, was totalled under the lock, and is now closed. Committed.</summary>
    Closed,

    /// <summary>No sitting has that identifier; nothing was written.</summary>
    SittingNotFound,

    /// <summary>
    /// The sitting was already closed when the lock was taken — two counters pressing Close at the same
    /// moment, or a stale page. Nothing was written, and the stamped values of the close that <em>did</em>
    /// happen are returned so the caller can say what they were rather than reporting a bare failure.
    /// </summary>
    AlreadyClosed,
}

/// <summary>
/// The outcome of one close-and-settle attempt (TECHNICAL_SPECIFICATION §5.3, §9, §12).
///
/// <para>Everything the web layer needs after the commit is here, so nothing has to re-query to decide
/// what to broadcast, what to count, or what to tell the person standing at the till: the stamped
/// settled total, the instant and actor stamped with it, and how many lines were still pending at the
/// moment the total was computed.</para>
///
/// <para><see cref="PendingLineCountAtClose"/> is a <em>record of what was charged</em>, not a warning:
/// §5.3 puts the warning before the button ("the counter UI must surface still-pending lines
/// prominently before offering Close"), and by the time this returns the decision has been made and
/// committed. It is here so the confirmation can say "settled with 2 lines still with the kitchen"
/// rather than pretending the table left with everything on it.</para>
/// </summary>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="SittingIdentifier">The sitting the attempt named.</param>
/// <param name="SettledTotalAmount">The stamped total — this close's, or the earlier close's when <see cref="CloseSittingOutcome.AlreadyClosed"/>. <c>null</c> when the sitting does not exist.</param>
/// <param name="ClosedAt">When the sitting was closed, on the same terms.</param>
/// <param name="ClosedByPersonIdentifier">Who closed it, on the same terms.</param>
/// <param name="PendingLineCountAtClose">Lines still unfulfilled when the total was computed; <c>0</c> unless this call is the one that closed it.</param>
public sealed record CloseSittingResult(
    CloseSittingOutcome Outcome,
    Guid SittingIdentifier,
    decimal? SettledTotalAmount,
    DateTimeOffset? ClosedAt,
    Guid? ClosedByPersonIdentifier,
    int PendingLineCountAtClose)
{
    /// <summary>True only when this call is the one that closed the sitting — the precondition for the §9 broadcast and the §12 counter.</summary>
    public bool IsClosed => Outcome is CloseSittingOutcome.Closed;

    /// <summary>True when the sitting is closed, whoever closed it — this call or an earlier one.</summary>
    public bool SittingIsClosed => Outcome is CloseSittingOutcome.Closed or CloseSittingOutcome.AlreadyClosed;
}

/// <summary>
/// Closing and settling a sitting (TECHNICAL_SPECIFICATION §5.3), which is one transaction and one
/// method, for the same reason <see cref="Orders.IOrderMutations"/> is: the total that gets stamped and
/// the flag that stops further ordering have to be decided together or they are not the same fact.
///
/// <para>Kept apart from <see cref="ISittingDirectory"/> and <see cref="ISittingMembership"/> on the
/// pattern the rest of this layer already follows — a directory reads, a membership service opens and
/// joins, and this one closes. Nothing else in the system writes <c>closed_at</c>.</para>
///
/// <para><b>The lock is the whole point.</b> §5.3 says <c>SELECT … FOR UPDATE</c> on the sitting row,
/// and §6.6 has every order-mutating transaction take <c>FOR SHARE</c> on the same row first. Those two
/// modes conflict, and that conflict is what guarantees the two things §5.3 promises: no event slips in
/// after the total is computed, and no total is computed over a half-written order. Take a weaker lock
/// here and the failure is a bill that is quietly wrong — the worst shape of bug this system can
/// have.</para>
/// </summary>
public interface ISittingSettlement
{
    /// <summary>
    /// Closes and settles one sitting (§5.3): lock the sitting <c>FOR UPDATE</c>, verify it is open,
    /// total <c>sitting_bill</c> for it <em>under that lock</em>, and stamp <c>closed_at</c>,
    /// <c>closed_by_person_identifier</c>, and <c>settled_total_amount</c> together — the schema's
    /// paired CHECKs require all three or none.
    ///
    /// <para>The stamped total is never rewritten. Post-close corrections (§6.7) are administrator-only
    /// appended events that live beside it, and the difference between the two is what §11.3's closed
    /// view shows.</para>
    /// </summary>
    Task<CloseSittingResult> CloseAndSettleAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ISittingSettlement"/>. One connection and one transaction,
/// one <see cref="IClock.UtcNow"/> instant stamped on the row — the shape every write in this layer has.
/// No identifier factory: closing does not create a row, it completes one.
/// </summary>
public sealed class DapperSittingSettlement : ISittingSettlement
{
    // §5.3 step one. FOR UPDATE, not FOR SHARE: this is the exclusive side of the pair, and it is what
    // an in-flight order writer's FOR SHARE blocks against (§6.6).
    private const string LockSittingSql = """
        SELECT table_sitting.table_sitting_identifier    AS SittingIdentifier,
               table_sitting.closed_at                   AS ClosedAt,
               table_sitting.closed_by_person_identifier AS ClosedByPersonIdentifier,
               table_sitting.settled_total_amount        AS SettledTotalAmount
        FROM table_sitting
        WHERE table_sitting.table_sitting_identifier = @SittingIdentifier
        FOR UPDATE;
        """;

    // §5.3: "compute the settled total as the sum over sitting_bill for the sitting under that lock".
    // A sitting whose members all joined and never ordered has no sitting_bill rows at all, which sums
    // to NULL rather than to zero — hence the COALESCE. The cast keeps the value in the column's own
    // numeric(10,2) domain so what is read back is bit-for-bit what was stamped.
    private const string SettledTotalSql = """
        SELECT COALESCE(sum(sitting_bill.person_total_amount), 0)::numeric(10,2)
        FROM sitting_bill
        WHERE sitting_bill.table_sitting_identifier = @SittingIdentifier;
        """;

    // The count behind §5.3's "still-pending lines" — read under the same lock as the total, so the two
    // numbers describe the same instant.
    private const string PendingLineCountSql = """
        SELECT count(*)::int
        FROM order_current_line AS line
        INNER JOIN guest_order
                ON guest_order.guest_order_identifier = line.guest_order_identifier
        WHERE guest_order.table_sitting_identifier = @SittingIdentifier
          AND NOT line.is_fulfilled;
        """;

    // All three columns move together: the schema has CHECK ((closed_at IS NULL) = (…)) on both the
    // actor and the total, so a partial stamp is rejected by the database, not merely discouraged here.
    private const string StampClosedSql = """
        UPDATE table_sitting
        SET closed_at = @ClosedAt,
            closed_by_person_identifier = @ClosedByPersonIdentifier,
            settled_total_amount = @SettledTotalAmount
        WHERE table_sitting_identifier = @SittingIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public DapperSittingSettlement(IDatabaseConnectionFactory connectionFactory, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);

        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<CloseSittingResult> CloseAndSettleAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        SittingLockRow? sitting = await connection
            .QuerySingleOrDefaultAsync<SittingLockRow>(new CommandDefinition(
                LockSittingSql,
                new { SittingIdentifier = sittingIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (sitting is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CloseSittingResult(
                CloseSittingOutcome.SittingNotFound,
                sittingIdentifier,
                SettledTotalAmount: null,
                ClosedAt: null,
                ClosedByPersonIdentifier: null,
                PendingLineCountAtClose: 0);
        }

        if (sitting.ClosedAt is { } alreadyClosedAt)
        {
            // Somebody got here first. Report their close rather than a bare refusal: the person at the
            // till wants to know the table is settled and for how much, not that a button failed.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CloseSittingResult(
                CloseSittingOutcome.AlreadyClosed,
                sittingIdentifier,
                sitting.SettledTotalAmount,
                AsUtc(alreadyClosedAt),
                sitting.ClosedByPersonIdentifier,
                PendingLineCountAtClose: 0);
        }

        int pendingLineCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            PendingLineCountSql,
            new { SittingIdentifier = sittingIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        decimal settledTotalAmount = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            SettledTotalSql,
            new { SittingIdentifier = sittingIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            StampClosedSql,
            new
            {
                SittingIdentifier = sittingIdentifier,
                ClosedAt = now,
                ClosedByPersonIdentifier = closedByPersonIdentifier,
                SettledTotalAmount = settledTotalAmount,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CloseSittingResult(
            CloseSittingOutcome.Closed,
            sittingIdentifier,
            settledTotalAmount,
            now,
            closedByPersonIdentifier,
            pendingLineCount);
    }

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // Npgsql materialises `timestamptz` as a UTC DateTime and Dapper's constructor binding will not
    // feed one into a DateTimeOffset parameter, so the locked row is read with a DateTime member and
    // converted above — the same fix every other reader in this layer carries.
    private sealed record SittingLockRow(
        Guid SittingIdentifier,
        DateTime? ClosedAt,
        Guid? ClosedByPersonIdentifier,
        decimal? SettledTotalAmount);
}
