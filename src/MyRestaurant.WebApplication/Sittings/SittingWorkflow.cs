using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Sittings;

/// <summary>
/// What one end-of-day pass did (TECHNICAL_SPECIFICATION §5.4, §11.4).
///
/// <para>Every individual <see cref="CloseSittingResult"/> is carried, in the order the caller asked for
/// them, because a batch that half-worked has to be reportable line by line: an administrator who ticked
/// six tables and got four closures needs to know <em>which</em> two the counter had already settled.
/// The counts are derived here rather than in a Razor component so the arithmetic is tested once instead
/// of being retyped on every surface that grows an end-of-day button.</para>
/// </summary>
/// <param name="Results">One result per distinct sitting asked for, in the order asked.</param>
public sealed record EndOfDayResult(IReadOnlyList<CloseSittingResult> Results)
{
    /// <summary>An end-of-day pass over nothing — what an empty selection produces without touching the database.</summary>
    public static EndOfDayResult Nothing { get; } = new([]);

    /// <summary>How many sittings this pass actually closed — the only ones counted and announced.</summary>
    public int ClosedCount => Results.Count(result => result.Outcome is CloseSittingOutcome.Closed);

    /// <summary>
    /// How many were already settled when the lock was taken — a counter got there first, or the page was
    /// stale. Nothing was written for these, and they are not a failure.
    /// </summary>
    public int AlreadyClosedCount => Results.Count(result => result.Outcome is CloseSittingOutcome.AlreadyClosed);

    /// <summary>How many identifiers matched no sitting at all. Only reachable from a hand-edited form post.</summary>
    public int NotFoundCount => Results.Count(result => result.Outcome is CloseSittingOutcome.SittingNotFound);

    /// <summary>
    /// The sum of the totals stamped by <em>this</em> pass. Deliberately excludes
    /// <see cref="CloseSittingOutcome.AlreadyClosed"/>, whose totals belong to somebody else's close and
    /// would inflate the day's figure by double-counting a table.
    /// </summary>
    public decimal SettledTotalAmount => Results
        .Where(result => result.Outcome is CloseSittingOutcome.Closed)
        .Sum(result => result.SettledTotalAmount ?? 0m);

    /// <summary>Lines still with the kitchen across every sitting this pass closed — §5.3's record of what was charged anyway.</summary>
    public int PendingLineCountAtClose => Results
        .Where(result => result.Outcome is CloseSittingOutcome.Closed)
        .Sum(result => result.PendingLineCountAtClose);
}

/// <summary>
/// The web layer's entry point for closing a sitting (TECHNICAL_SPECIFICATION §5.3, §5.4, §9, §12).
///
/// <para>Same division of labour as <see cref="Orders.IOrderWorkflow"/> and
/// <see cref="Menu.IMenuWorkflow"/>: <see cref="ISittingSettlement"/> owns the transaction and stops at
/// commit, because a data-access service has no business knowing about Blazor circuits or OpenTelemetry
/// meters. The two things that must happen <em>after</em> that commit happen here — §9's
/// <see cref="SittingClosed"/> goes out to every subscribed circuit, and §12's
/// <c>sittings_closed_total</c> is incremented.</para>
///
/// <para>A surface calls this and never <see cref="ISittingSettlement"/> directly. The broadcast is not
/// cosmetic: §11.1 requires the guest's table surface to flip to a read-only settled-bill view
/// <em>on</em> <see cref="SittingClosed"/>, and the kitchen board drops the table from its queue on the
/// same notification. A close that committed without announcing itself would leave a settled table
/// still taking orders on every phone that already had the page open — and those sends would then be
/// refused one by one, with nobody able to say why.</para>
/// </summary>
public interface ISittingWorkflow
{
    /// <summary>
    /// Closes and settles a sitting (§5.3) and, if this call is the one that closed it, counts and
    /// announces the close.
    /// </summary>
    Task<CloseSittingResult> CloseAndSettleAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// §5.4's end-of-day batch close: several sittings, each through the same §5.3 transaction, each
    /// separately counted and separately announced.
    ///
    /// <para><b>One transaction per sitting, not one transaction for the batch.</b> §5.4 says "close each
    /// via the same §5.3 transaction", and that is not an implementation detail: §5.3's guarantee is that
    /// a sitting's total is computed under a <c>FOR UPDATE</c> that conflicts with the <c>FOR SHARE</c>
    /// every order writer holds (§6.6). One long transaction spanning twelve tables would hold twelve
    /// such locks until the last one committed, so a guest still ordering at table 1 would block the
    /// closing of table 12 — and an error on table 12 would roll back eleven closures that were correct.
    /// Twelve short transactions cannot do either.</para>
    ///
    /// <para>Repeated identifiers are collapsed before anything is attempted, so a duplicated form field
    /// cannot produce one <see cref="CloseSittingOutcome.Closed"/> and one
    /// <see cref="CloseSittingOutcome.AlreadyClosed"/> for the same table and report a table that was
    /// already settled when it was not.</para>
    ///
    /// <para>If <paramref name="cancellationToken"/> trips part-way the exception propagates and the
    /// sittings already closed <em>stay</em> closed — they were separate committed transactions, and
    /// there is no undo for a close (§5.3). The surface re-reads and shows what is still open.</para>
    /// </summary>
    Task<EndOfDayResult> CloseManyAsync(
        IReadOnlyList<Guid> sittingIdentifiers,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only implementation of <see cref="ISittingWorkflow"/>: a thin post-commit shell over
/// <see cref="ISittingSettlement"/>.
/// </summary>
public sealed class SittingWorkflow : ISittingWorkflow
{
    private readonly ISittingSettlement _settlement;
    private readonly IDomainEventBroadcaster _broadcaster;
    private readonly RestaurantMetrics _metrics;

    public SittingWorkflow(
        ISittingSettlement settlement,
        IDomainEventBroadcaster broadcaster,
        RestaurantMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(metrics);

        _settlement = settlement;
        _broadcaster = broadcaster;
        _metrics = metrics;
    }

    public async Task<CloseSittingResult> CloseAndSettleAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        CloseSittingResult result = await _settlement
            .CloseAndSettleAsync(sittingIdentifier, closedByPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // Only the call that actually closed it. A losing race saw the sitting already closed and wrote
        // nothing, so counting it would double-count one close and broadcasting it would make every
        // subscriber re-query for a state change that happened milliseconds ago and was already
        // announced by the winner.
        if (result.IsClosed)
        {
            // Metrics before the broadcast, matching OrderWorkflow: a subscriber that re-queries
            // synchronously must not be able to observe a state change that has not been counted.
            _metrics.RecordSittingClosed();
            _broadcaster.Publish(new SittingClosed(result.SittingIdentifier));
        }

        return result;
    }

    public async Task<EndOfDayResult> CloseManyAsync(
        IReadOnlyList<Guid> sittingIdentifiers,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sittingIdentifiers);

        if (sittingIdentifiers.Count == 0)
        {
            // Nothing selected. No connection, no transaction, no notification — and the caller still
            // gets a well-formed result whose every count is zero, rather than having to special-case it.
            return EndOfDayResult.Nothing;
        }

        // Distinct, order preserved. A HashSet decides membership; the list keeps the sequence, because
        // §5.4's list is ordered oldest-first and the report reads back in the order somebody ticked.
        HashSet<Guid> seen = [];
        List<CloseSittingResult> results = new(sittingIdentifiers.Count);

        foreach (Guid sittingIdentifier in sittingIdentifiers)
        {
            if (!seen.Add(sittingIdentifier))
            {
                continue;
            }

            // Deliberately through the public method rather than the settlement directly: each close that
            // committed must be counted (§12) and announced (§9) on its own, at the moment it happened.
            // Batching the broadcasts to the end would leave a settled table taking orders on every phone
            // that had it open for as long as the rest of the pass took.
            results.Add(await CloseAndSettleAsync(sittingIdentifier, closedByPersonIdentifier, cancellationToken)
                .ConfigureAwait(false));
        }

        return new EndOfDayResult(results);
    }
}
