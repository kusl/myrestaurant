using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Sittings;

public sealed record EndOfDayResult(IReadOnlyList<CloseSittingResult> Results)
{
    public static EndOfDayResult Nothing { get; } = new([]);

    public int ClosedCount => Results.Count(result => result.Outcome is CloseSittingOutcome.Closed);

    public int AlreadyClosedCount => Results.Count(result => result.Outcome is CloseSittingOutcome.AlreadyClosed);

    public int NotFoundCount => Results.Count(result => result.Outcome is CloseSittingOutcome.SittingNotFound);

    public decimal SettledTotalAmount => Results
        .Where(result => result.Outcome is CloseSittingOutcome.Closed)
        .Sum(result => result.SettledTotalAmount ?? 0m);

    public int PendingLineCountAtClose => Results
        .Where(result => result.Outcome is CloseSittingOutcome.Closed)
        .Sum(result => result.PendingLineCountAtClose);
}

public interface ISittingWorkflow
{
    Task<CloseSittingResult> CloseAndSettleAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<EndOfDayResult> CloseManyAsync(
        IReadOnlyList<Guid> sittingIdentifiers,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

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

        if (result.IsClosed)
        {
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
            return EndOfDayResult.Nothing;
        }

        HashSet<Guid> seen = [];
        List<CloseSittingResult> results = new(sittingIdentifiers.Count);

        foreach (Guid sittingIdentifier in sittingIdentifiers)
        {
            if (!seen.Add(sittingIdentifier))
            {
                continue;
            }

            results.Add(await CloseAndSettleAsync(sittingIdentifier, closedByPersonIdentifier, cancellationToken)
                .ConfigureAwait(false));
        }

        return new EndOfDayResult(results);
    }
}
