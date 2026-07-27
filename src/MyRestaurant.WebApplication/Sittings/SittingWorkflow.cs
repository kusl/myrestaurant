using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Sittings;

/// <summary>
/// The web layer's entry point for closing a sitting (TECHNICAL_SPECIFICATION §5.3, §9, §12).
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
}
