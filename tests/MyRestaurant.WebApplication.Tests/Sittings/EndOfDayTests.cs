using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Sittings;
using MyRestaurant.WebApplication.Tables;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// §5.4's end-of-day batch close, and the registration the administration record page depends on
/// (TECHNICAL_SPECIFICATION §5.4, §9, §11.4, §12). <see cref="CounterWiringTests"/> covers the single
/// close; this covers doing several of them and the arithmetic that reports the result.
///
/// <para>Every fact here is about something that fails <em>quietly</em>. A batch that broadcast once at
/// the end would leave eleven settled tables still taking orders on every phone that already had them
/// open, and §11.1 has no other signal to flip them. A batch that counted an already-closed sitting would
/// inflate <c>sittings_closed_total</c> and the day's takings by double-counting a table somebody else
/// settled. A batch that did not collapse a duplicated identifier would report "1 closed, 1 already
/// settled" for one table and send an administrator looking for a second close that never happened.
/// None of those raises an error anywhere.</para>
///
/// <para>No database and no container: <see cref="ISittingSettlement"/> is a hand-written fake (§16.1 —
/// hand-written fakes, no Moq) that answers per identifier, which is the point. Arranging a genuine
/// already-closed race against a real PostgreSQL would test the lock, and
/// <c>SittingSettlementTests</c> already does that.</para>
/// </summary>
public sealed class EndOfDayTests
{
    private static readonly Guid FirstSitting = Guid.Parse("0192f000-0000-7000-8000-0000000e0001");
    private static readonly Guid SecondSitting = Guid.Parse("0192f000-0000-7000-8000-0000000e0002");
    private static readonly Guid ThirdSitting = Guid.Parse("0192f000-0000-7000-8000-0000000e0003");
    private static readonly Guid AdministratorIdentifier = Guid.Parse("0192f000-0000-7000-8000-0000000e00a1");
    private static readonly DateTimeOffset ClosedAt = new(2026, 6, 4, 23, 15, 0, TimeSpan.Zero);

    /// <summary>
    /// The read behind <c>/administration/sittings/{id}</c>. It is registered by
    /// <see cref="TablesServiceCollectionExtensions.AddRestaurantTables"/> rather than by the orders
    /// extension because it answers a question about a <em>sitting</em>; resolving it opens no connection.
    /// </summary>
    [Fact]
    public void SittingRecordReads_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperSittingRecordReads>(
            scope.ServiceProvider.GetRequiredService<ISittingRecordReads>());
    }

    [Fact]
    public async Task CloseMany_ClosesEveryTickedSitting_AndAnnouncesEachOneSeparately()
    {
        FakeSittingSettlement settlement = new();
        settlement.WillClose(FirstSitting, 41.50m, pendingAtClose: 2);
        settlement.WillClose(SecondSitting, 12.00m, pendingAtClose: 0);
        settlement.WillClose(ThirdSitting, 0m, pendingAtClose: 0);
        RecordingBroadcaster broadcaster = new();

        EndOfDayResult result = await Workflow(settlement, broadcaster).CloseManyAsync(
            [FirstSitting, SecondSitting, ThirdSitting],
            AdministratorIdentifier,
            TestContext.Current.CancellationToken);

        // One transaction per sitting, in the order asked (§5.4), each carrying the closer.
        Assert.Equal(3, settlement.Attempted.Count);
        Assert.Equal(FirstSitting, settlement.Attempted[0]);
        Assert.Equal(SecondSitting, settlement.Attempted[1]);
        Assert.Equal(ThirdSitting, settlement.Attempted[2]);
        Assert.All(settlement.ClosedBy, closedBy => Assert.Equal(AdministratorIdentifier, closedBy));

        Assert.Equal(3, result.ClosedCount);
        Assert.Equal(0, result.AlreadyClosedCount);
        Assert.Equal(0, result.NotFoundCount);
        Assert.Equal(53.50m, result.SettledTotalAmount);
        Assert.Equal(2, result.PendingLineCountAtClose);

        // Three notifications, one per close, not one for the pass. §11.1 flips each table's surface on
        // its own SittingClosed and nothing else reaches it.
        Assert.Equal(3, broadcaster.Published.Count);
        Assert.Equal(
            [FirstSitting, SecondSitting, ThirdSitting],
            broadcaster.Published.Cast<SittingClosed>().Select(closed => closed.SittingIdentifier));
    }

    /// <summary>
    /// A counter settling a table while an administrator has this page open is the system working, not a
    /// failure. Nothing was written for that table, so nothing may be counted, announced, or added to the
    /// day's takings — the total belongs to the close that actually happened.
    /// </summary>
    [Fact]
    public async Task CloseMany_CountsAndTotalsOnlyTheSittingsItActuallyClosed()
    {
        FakeSittingSettlement settlement = new();
        settlement.WillClose(FirstSitting, 41.50m, pendingAtClose: 0);
        settlement.WillReportAlreadyClosed(SecondSitting, 12.00m);
        settlement.WillReportMissing(ThirdSitting);
        RecordingBroadcaster broadcaster = new();

        EndOfDayResult result = await Workflow(settlement, broadcaster).CloseManyAsync(
            [FirstSitting, SecondSitting, ThirdSitting],
            AdministratorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ClosedCount);
        Assert.Equal(1, result.AlreadyClosedCount);
        Assert.Equal(1, result.NotFoundCount);

        // 12.00 was somebody else's close. Adding it here would report a day that took more than it did.
        Assert.Equal(41.50m, result.SettledTotalAmount);

        SittingClosed published = Assert.IsType<SittingClosed>(Assert.Single(broadcaster.Published));
        Assert.Equal(FirstSitting, published.SittingIdentifier);

        // All three were still attempted: the outcome is decided under the lock, never guessed from a list
        // that was rendered seconds ago.
        Assert.Equal(3, settlement.Attempted.Count);
    }

    /// <summary>
    /// A duplicated identifier is not hypothetical — a form can post one, and the first attempt would then
    /// close the sitting and the second would find it closed, reporting one table as both settled by this
    /// pass and previously settled by somebody else.
    /// </summary>
    [Fact]
    public async Task CloseMany_CollapsesRepeatedIdentifiers_BeforeAttemptingAnything()
    {
        FakeSittingSettlement settlement = new();
        settlement.WillClose(FirstSitting, 41.50m, pendingAtClose: 0);
        settlement.WillClose(SecondSitting, 8.25m, pendingAtClose: 0);
        RecordingBroadcaster broadcaster = new();

        EndOfDayResult result = await Workflow(settlement, broadcaster).CloseManyAsync(
            [FirstSitting, SecondSitting, FirstSitting, FirstSitting],
            AdministratorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, settlement.Attempted.Count);
        Assert.Equal(FirstSitting, settlement.Attempted[0]);
        Assert.Equal(SecondSitting, settlement.Attempted[1]);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, result.ClosedCount);
        Assert.Equal(0, result.AlreadyClosedCount);
        Assert.Equal(49.75m, result.SettledTotalAmount);
        Assert.Equal(2, broadcaster.Published.Count);
    }

    [Fact]
    public async Task CloseMany_AnEmptySelection_TouchesNothingAndAnnouncesNothing()
    {
        FakeSittingSettlement settlement = new();
        RecordingBroadcaster broadcaster = new();

        EndOfDayResult result = await Workflow(settlement, broadcaster).CloseManyAsync(
            [],
            AdministratorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Empty(settlement.Attempted);
        Assert.Empty(broadcaster.Published);
        Assert.Empty(result.Results);

        // A well-formed result whose every count is zero, so no surface has to special-case "nothing was
        // ticked" twice.
        Assert.Equal(0, result.ClosedCount);
        Assert.Equal(0, result.AlreadyClosedCount);
        Assert.Equal(0, result.NotFoundCount);
        Assert.Equal(0m, result.SettledTotalAmount);
    }

    /// <summary>
    /// A sitting that settles at nothing still closed, and its table still has to flip. Zero is a total,
    /// not an absence — the same guard <see cref="CounterWiringTests"/> keeps on the single close, kept
    /// here because a batch is where a tempting "only announce when money moved" would be written.
    /// </summary>
    [Fact]
    public async Task CloseMany_ASittingThatSettlesAtZero_IsStillCountedAndAnnounced()
    {
        FakeSittingSettlement settlement = new();
        settlement.WillClose(FirstSitting, 0m, pendingAtClose: 0);
        RecordingBroadcaster broadcaster = new();

        EndOfDayResult result = await Workflow(settlement, broadcaster).CloseManyAsync(
            [FirstSitting],
            AdministratorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ClosedCount);
        Assert.Equal(0m, result.SettledTotalAmount);
        Assert.IsType<SittingClosed>(Assert.Single(broadcaster.Published));
    }

    /// <summary>
    /// The individual results are carried, not just the counts: an administrator who ticked six tables and
    /// got four closures has to be able to find out which two did not, and a page that only received
    /// totals could not tell them.
    /// </summary>
    [Fact]
    public async Task CloseMany_CarriesEveryIndividualResult_InTheOrderAsked()
    {
        FakeSittingSettlement settlement = new();
        settlement.WillReportAlreadyClosed(FirstSitting, 5.00m);
        settlement.WillClose(SecondSitting, 30.00m, pendingAtClose: 1);
        RecordingBroadcaster broadcaster = new();

        EndOfDayResult result = await Workflow(settlement, broadcaster).CloseManyAsync(
            [FirstSitting, SecondSitting],
            AdministratorIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Results.Count);

        Assert.Equal(FirstSitting, result.Results[0].SittingIdentifier);
        Assert.Equal(CloseSittingOutcome.AlreadyClosed, result.Results[0].Outcome);
        Assert.False(result.Results[0].IsClosed);
        Assert.True(result.Results[0].SittingIsClosed);

        // The losing side still reports the winner's stamped total, so the page can say what it settled at
        // rather than reporting a bare failure (§5.3).
        Assert.Equal(5.00m, result.Results[0].SettledTotalAmount);

        Assert.Equal(SecondSitting, result.Results[1].SittingIdentifier);
        Assert.Equal(CloseSittingOutcome.Closed, result.Results[1].Outcome);
        Assert.Equal(1, result.Results[1].PendingLineCountAtClose);
    }

    [Fact]
    public async Task CloseMany_RejectsANullSelection()
    {
        FakeSittingSettlement settlement = new();
        RecordingBroadcaster broadcaster = new();
        ISittingWorkflow workflow = Workflow(settlement, broadcaster);

        await Assert.ThrowsAsync<ArgumentNullException>(() => workflow.CloseManyAsync(
            null!,
            AdministratorIdentifier,
            TestContext.Current.CancellationToken));

        Assert.Empty(settlement.Attempted);
    }

    private static SittingWorkflow Workflow(
        ISittingSettlement settlement,
        IDomainEventBroadcaster broadcaster)
    {
        // A real RestaurantMetrics, not a stub: it is sealed and has no interface, so the only way to be
        // sure the §12 call site runs at all is to let it run against real instruments. AddMetrics()
        // supplies the meter factory it takes, exactly as Program.cs does. The provider is deliberately
        // not disposed — disposing it would dispose the Meter the returned workflow still holds. Same
        // shape as CounterWiringTests and OrderWorkflowTests.
        ServiceProvider provider = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<RestaurantMetrics>()
            .BuildServiceProvider();

        return new SittingWorkflow(settlement, broadcaster, provider.GetRequiredService<RestaurantMetrics>());
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantTables. The connection factory is
        // never used — resolution constructs, it does not connect — and an ephemeral key ring keeps the
        // test off the file system.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));
        services.AddMetrics();
        services.AddSingleton<RestaurantMetrics>();
        services.AddSingleton<IDomainEventBroadcaster, RecordingBroadcaster>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        services.AddRestaurantTables();

        return services.BuildServiceProvider();
    }

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }

    /// <summary>
    /// Answers per identifier, so one batch can contain a close, a lost race, and a stale row — which is
    /// exactly the mix an end-of-day pass over a list rendered a minute ago produces. An identifier nobody
    /// arranged an answer for reports <see cref="CloseSittingOutcome.SittingNotFound"/>, matching what the
    /// real service does with an identifier no row has.
    /// </summary>
    private sealed class FakeSittingSettlement : ISittingSettlement
    {
        private readonly Dictionary<Guid, CloseSittingResult> _answers = [];

        public List<Guid> Attempted { get; } = [];

        public List<Guid> ClosedBy { get; } = [];

        public void WillClose(Guid sittingIdentifier, decimal settledTotal, int pendingAtClose)
            => _answers[sittingIdentifier] = new CloseSittingResult(
                CloseSittingOutcome.Closed,
                sittingIdentifier,
                settledTotal,
                ClosedAt,
                AdministratorIdentifier,
                pendingAtClose);

        public void WillReportAlreadyClosed(Guid sittingIdentifier, decimal settledTotal)
            => _answers[sittingIdentifier] = new CloseSittingResult(
                CloseSittingOutcome.AlreadyClosed,
                sittingIdentifier,
                settledTotal,
                ClosedAt,
                AdministratorIdentifier,
                PendingLineCountAtClose: 0);

        public void WillReportMissing(Guid sittingIdentifier)
            => _answers[sittingIdentifier] = new CloseSittingResult(
                CloseSittingOutcome.SittingNotFound,
                sittingIdentifier,
                SettledTotalAmount: null,
                ClosedAt: null,
                ClosedByPersonIdentifier: null,
                PendingLineCountAtClose: 0);

        public Task<CloseSittingResult> CloseAndSettleAsync(
            Guid sittingIdentifier,
            Guid closedByPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            Attempted.Add(sittingIdentifier);
            ClosedBy.Add(closedByPersonIdentifier);

            if (_answers.TryGetValue(sittingIdentifier, out CloseSittingResult? answer))
            {
                return Task.FromResult(answer);
            }

            return Task.FromResult(new CloseSittingResult(
                CloseSittingOutcome.SittingNotFound,
                sittingIdentifier,
                SettledTotalAmount: null,
                ClosedAt: null,
                ClosedByPersonIdentifier: null,
                PendingLineCountAtClose: 0));
        }
    }

    private sealed class RecordingBroadcaster : IDomainEventBroadcaster
    {
        public List<DomainNotification> Published { get; } = [];

        public void Publish(DomainNotification notification) => Published.Add(notification);

        public IDisposable Subscribe(Action<DomainNotification> handler) => new NoSubscription();

        private sealed class NoSubscription : IDisposable
        {
            public void Dispose()
            {
                // Nothing subscribes in these tests; the token exists only to satisfy the contract.
            }
        }
    }
}
