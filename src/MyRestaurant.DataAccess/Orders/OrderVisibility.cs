using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Orders;

/// <summary>What happened to an owner's request to hide their own order (TECHNICAL_SPECIFICATION §6.8).</summary>
public enum HideOrderOutcome
{
    /// <summary>A <c>hidden</c> row was appended. Committed.</summary>
    Hidden,

    /// <summary>No order has that identifier; nothing was written.</summary>
    OrderNotFound,

    /// <summary>
    /// The order belongs to somebody else. §6.8 gives the hide to the <em>owner</em> — an order is
    /// removed from "the owner's own views", and there is no other view it could be removed from.
    /// </summary>
    NotTheOwner,

    /// <summary>
    /// The sitting is still open. §6.8: "Hiding applies to an order in a <b>closed</b> sitting" — while a
    /// table is still eating, the order is the live one the surface is built around, and hiding it would
    /// hide the thing the guest is looking at.
    /// </summary>
    SittingStillOpen,

    /// <summary>
    /// It was already hidden when the lock was taken — a double tap, or a stale page. Nothing was
    /// written, and this is not a failure: the order is in the state the person asked for.
    /// </summary>
    AlreadyHidden,
}

/// <summary>What happened to an administrator's request to unhide an order (TECHNICAL_SPECIFICATION §6.8, §11.4).</summary>
public enum UnhideOrderOutcome
{
    /// <summary>An <c>unhidden</c> row was appended and the order is back in its owner's history. Committed.</summary>
    Unhidden,

    /// <summary>No order has that identifier; nothing was written.</summary>
    OrderNotFound,

    /// <summary>
    /// It is not currently hidden — either it never was, or another administrator unhid it first.
    /// Nothing was written.
    /// </summary>
    NotHidden,
}

/// <summary>
/// The outcome of one hide attempt (TECHNICAL_SPECIFICATION §6.8, §9).
///
/// <para>The sitting and the owner are carried out because the caller needs them after the commit and
/// must not have to re-query for them: §9's <c>VisibilityChanged</c> is keyed on the order, and the
/// surface's own sentence ("hidden from your history") is only truthful about an order it has just
/// confirmed the identity of.</para>
/// </summary>
/// <param name="Outcome">Which of the five things happened.</param>
/// <param name="GuestOrderIdentifier">The order the attempt named.</param>
/// <param name="SittingIdentifier">The sitting it belongs to; <c>null</c> when the order does not exist.</param>
/// <param name="OwnerPersonIdentifier">Whose order it is; <c>null</c> when the order does not exist.</param>
/// <param name="OccurredAt">When the <c>hidden</c> row was stamped; <c>null</c> unless this call wrote one.</param>
public sealed record HideOrderResult(
    HideOrderOutcome Outcome,
    Guid GuestOrderIdentifier,
    Guid? SittingIdentifier,
    Guid? OwnerPersonIdentifier,
    DateTimeOffset? OccurredAt)
{
    /// <summary>True only when this call appended the row — the precondition for the §9 broadcast.</summary>
    public bool IsHidden => Outcome is HideOrderOutcome.Hidden;

    /// <summary>True when the order is hidden, whether this call did it or an earlier one had.</summary>
    public bool OrderIsHidden => Outcome is HideOrderOutcome.Hidden or HideOrderOutcome.AlreadyHidden;
}

/// <summary>The outcome of one unhide attempt (TECHNICAL_SPECIFICATION §6.8, §9, §11.4).</summary>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="GuestOrderIdentifier">The order the attempt named.</param>
/// <param name="SittingIdentifier">The sitting it belongs to; <c>null</c> when the order does not exist.</param>
/// <param name="OwnerPersonIdentifier">Whose order it is; <c>null</c> when the order does not exist.</param>
/// <param name="OccurredAt">When the <c>unhidden</c> row was stamped; <c>null</c> unless this call wrote one.</param>
public sealed record UnhideOrderResult(
    UnhideOrderOutcome Outcome,
    Guid GuestOrderIdentifier,
    Guid? SittingIdentifier,
    Guid? OwnerPersonIdentifier,
    DateTimeOffset? OccurredAt)
{
    /// <summary>True only when this call appended the row — the precondition for the §9 broadcast.</summary>
    public bool IsUnhidden => Outcome is UnhideOrderOutcome.Unhidden;
}

/// <summary>
/// The two writes §6.8 defines: an owner hides one of their own closed orders, and an administrator —
/// nobody else — puts it back.
///
/// <para><b>Append-only, like every other log in this schema (ADR-0002).</b> Nothing here updates or
/// deletes: hiding writes a <c>hidden</c> row, unhiding writes an <c>unhidden</c> row, and the current
/// flag is the latest of them (the <c>order_visibility_current</c> view). A boolean column on
/// <c>guest_order</c> would have been shorter and would have thrown away the two questions this log
/// answers — who hid it, and had it been hidden before.</para>
///
/// <para><b>Why this is not on <see cref="IOrderMutations"/>.</b> A visibility event is not an order
/// event: it has no <c>sequence_number</c>, no operations, changes no line and no total, and appears
/// nowhere in §8.5's fold. §6.6's locking protocol exists to serialise writes that a bill is computed
/// from, and this is not one of them — putting it through that transaction would take a
/// <c>FOR SHARE</c> on a sitting that is closed by definition and imply, wrongly, that a bill could
/// move because somebody tidied their history.</para>
///
/// <para><b>Separated from the reads</b> on the pattern the rest of this layer follows
/// (<c>ITableDirectory</c>/<c>ITableAdministration</c>): <see cref="IOrderHistoryReads"/> renders the
/// history and the visibility log, this appends to it, and the guest surface that only lists past orders
/// cannot reach a write at all.</para>
/// </summary>
public interface IOrderVisibility
{
    /// <summary>
    /// §6.8's owner hide. Refuses an order that is not <paramref name="ownerPersonIdentifier"/>'s, an
    /// order whose sitting is still open, and an order that is already hidden; each refusal writes
    /// nothing.
    ///
    /// <para>There is deliberately no <c>unhide</c> counterpart for a guest. §6.8: "There is <b>no</b>
    /// user-facing unhide; the confirmation dialog states plainly that this cannot be undone from the
    /// guest's account." The only way back is <see cref="UnhideAsync"/>, which an administration surface
    /// calls (§11.4).</para>
    /// </summary>
    Task<HideOrderResult> HideAsync(
        Guid guestOrderIdentifier,
        Guid ownerPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// §6.8's administrator unhide, reached from the §11.4 hidden-records view. The actor is recorded, so
    /// the log says who put a record back and when.
    ///
    /// <para>The <em>authorization</em> is the surface's: <c>area.administration</c> guards the page
    /// (§3.7), and this method takes an identifier rather than a role because
    /// <c>order_visibility_event.actor_person_identifier</c> is NOT NULL and needs a person, not a
    /// claim.</para>
    /// </summary>
    Task<UnhideOrderResult> UnhideAsync(
        Guid guestOrderIdentifier,
        Guid administratorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IOrderVisibility"/>. One connection and one transaction per
/// call, one <see cref="IClock.UtcNow"/> instant per call, one UUIDv7 from
/// <see cref="IIdentifierFactory"/> per appended row (ADR-0011) — the shape every write in this layer
/// has.
///
/// <para><b>The lock, and why it is only on <c>guest_order</c>.</b> Both methods take
/// <c>FOR UPDATE OF guest_order</c> on the order row before reading the current flag, so two taps on
/// Hide cannot both see "not hidden" and both append. It deliberately does <em>not</em> lock
/// <c>table_sitting</c>: §6.6's order-mutating transaction locks the sitting first and the order second,
/// and a transaction that waits on the order alone can never be the other half of a deadlock with it.
/// The sitting's <c>closed_at</c> is read in the same statement without a lock, which is sound because a
/// close is one-way — §5.3 stamps <c>closed_at</c> and nothing in the system ever clears it, so a value
/// read here cannot become stale in the direction that would matter.</para>
/// </summary>
public sealed class DapperOrderVisibility : IOrderVisibility
{
    /// <summary>
    /// The order, its owner, and whether its sitting is closed — one statement, and the only one that
    /// takes a lock. <c>FOR UPDATE OF guest_order</c> names the order row alone; the joined sitting row
    /// is read, not locked.
    /// </summary>
    private const string LockOrderSql = """
        SELECT guest_order.guest_order_identifier   AS GuestOrderIdentifier,
               guest_order.table_sitting_identifier AS SittingIdentifier,
               guest_order.person_identifier        AS OwnerPersonIdentifier,
               sitting.closed_at                    AS SittingClosedAt
        FROM guest_order
        INNER JOIN table_sitting AS sitting
                ON sitting.table_sitting_identifier = guest_order.table_sitting_identifier
        WHERE guest_order.guest_order_identifier = @GuestOrderIdentifier
        FOR UPDATE OF guest_order;
        """;

    /// <summary>
    /// §6.8's "Current flag = latest event (view <c>order_visibility_current</c>)". No row at all means
    /// no visibility event has ever been written for this order, which is the overwhelmingly common case
    /// and reads as "not hidden" — hence the nullable scalar rather than a <c>COALESCE</c>, so the two
    /// states stay distinguishable to the caller if they ever need to be.
    /// </summary>
    private const string CurrentFlagSql = """
        SELECT order_visibility_current.is_hidden
        FROM order_visibility_current
        WHERE order_visibility_current.guest_order_identifier = @GuestOrderIdentifier;
        """;

    private const string AppendVisibilityEventSql = """
        INSERT INTO order_visibility_event (
            order_visibility_event_identifier,
            guest_order_identifier,
            actor_person_identifier,
            event_type,
            occurred_at)
        VALUES (
            @VisibilityEventIdentifier,
            @GuestOrderIdentifier,
            @ActorPersonIdentifier,
            @EventType,
            @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperOrderVisibility(
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

    public async Task<HideOrderResult> HideAsync(
        Guid guestOrderIdentifier,
        Guid ownerPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        OrderLockRow? order = await LockOrderAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.OrderNotFound, guestOrderIdentifier, null, null, null);
        }

        if (order.OwnerPersonIdentifier != ownerPersonIdentifier)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.NotTheOwner,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        if (order.SittingClosedAt is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.SittingStillOpen,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        bool isHidden = await ReadCurrentFlagAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (isHidden)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HideOrderResult(
                HideOrderOutcome.AlreadyHidden,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        await AppendAsync(
            connection,
            transaction,
            guestOrderIdentifier,
            ownerPersonIdentifier,
            OrderEventVocabulary.HiddenVisibility,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new HideOrderResult(
            HideOrderOutcome.Hidden,
            guestOrderIdentifier,
            order.SittingIdentifier,
            order.OwnerPersonIdentifier,
            now);
    }

    public async Task<UnhideOrderResult> UnhideAsync(
        Guid guestOrderIdentifier,
        Guid administratorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        OrderLockRow? order = await LockOrderAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new UnhideOrderResult(
                UnhideOrderOutcome.OrderNotFound, guestOrderIdentifier, null, null, null);
        }

        // No open/closed check here, and no owner check either. An unhide is a correction to a record,
        // and both of the things that would make hiding wrong — the sitting being live, the actor not
        // owning the order — are exactly the situation an administrator is here to fix.
        bool isHidden = await ReadCurrentFlagAsync(
            connection, transaction, guestOrderIdentifier, cancellationToken).ConfigureAwait(false);

        if (!isHidden)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new UnhideOrderResult(
                UnhideOrderOutcome.NotHidden,
                guestOrderIdentifier,
                order.SittingIdentifier,
                order.OwnerPersonIdentifier,
                null);
        }

        await AppendAsync(
            connection,
            transaction,
            guestOrderIdentifier,
            administratorPersonIdentifier,
            OrderEventVocabulary.UnhiddenVisibility,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new UnhideOrderResult(
            UnhideOrderOutcome.Unhidden,
            guestOrderIdentifier,
            order.SittingIdentifier,
            order.OwnerPersonIdentifier,
            now);
    }

    private static async Task<OrderLockRow?> LockOrderAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken)
        => await connection
            .QuerySingleOrDefaultAsync<OrderLockRow>(new CommandDefinition(
                LockOrderSql,
                new { GuestOrderIdentifier = guestOrderIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task<bool> ReadCurrentFlagAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid guestOrderIdentifier,
        CancellationToken cancellationToken)
    {
        bool? isHidden = await connection
            .QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
                CurrentFlagSql,
                new { GuestOrderIdentifier = guestOrderIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return isHidden ?? false;
    }

    private async Task AppendAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid guestOrderIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            AppendVisibilityEventSql,
            new
            {
                VisibilityEventIdentifier = _identifierFactory.Create(),
                GuestOrderIdentifier = guestOrderIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                OccurredAt = occurredAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    // Npgsql materialises `timestamptz` as a DateTime and Dapper's constructor binding will not feed one
    // into a DateTimeOffset parameter, so the locked row carries a DateTime? — the same fix every other
    // reader in this layer has. Nothing outside this class sees it; only its nullness is consulted.
    private sealed record OrderLockRow(
        Guid GuestOrderIdentifier,
        Guid SittingIdentifier,
        Guid OwnerPersonIdentifier,
        DateTime? SittingClosedAt);
}
