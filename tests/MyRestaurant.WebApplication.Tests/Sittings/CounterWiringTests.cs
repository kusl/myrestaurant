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
/// The counter half of the wiring composed by
/// <see cref="TablesServiceCollectionExtensions.AddRestaurantTables"/>, plus the behaviour of
/// <see cref="SittingWorkflow"/> itself (TECHNICAL_SPECIFICATION §5.3, §9, §11.3, §12).
/// <see cref="TablesWiringTests"/> covers the join-flow services; this covers what the counter's screens
/// need, and constructing any of it opens no connection.
///
/// <para>The behavioural facts here are the ones that fail <em>quietly</em>. A close that committed but
/// published nothing leaves a settled table still taking orders on every phone that already had the page
/// open — §11.1 flips the guest surface to a read-only settled bill <em>on</em>
/// <see cref="SittingClosed"/>, and no other signal reaches it. And a losing race that broadcast anyway
/// would tell every subscriber to re-query for a change it did not make, and would double-count one
/// close in <c>sittings_closed_total</c> (§12). Neither shows up as an error anywhere.</para>
///
/// <para>No database and no container: <see cref="ISittingSettlement"/> is a hand-written fake
/// (§16.1 — hand-written fakes, no Moq) returning whatever outcome the test wants to react to, which is
/// the point — arranging a genuine already-closed race against a real PostgreSQL would test the lock,
/// which <c>SittingSettlementTests</c> already does.</para>
/// </summary>
public sealed class CounterWiringTests
{
    private static readonly Guid SittingIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000c001");
    private static readonly Guid CounterPersonIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000c002");
    private static readonly DateTimeOffset ClosedAt = new(2026, 6, 3, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public void CounterBoardReads_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperCounterBoardReads>(scope.ServiceProvider.GetRequiredService<ICounterBoardReads>());
    }

    [Fact]
    public void SittingSettlement_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperSittingSettlement>(scope.ServiceProvider.GetRequiredService<ISittingSettlement>());
    }

    /// <summary>
    /// Surfaces take the workflow, never the settlement directly — otherwise §11.1's flip to the settled
    /// view would never happen on any page that was already open.
    /// </summary>
    [Fact]
    public void SittingWorkflow_IsResolvableInAScope_AndIsTheServiceSurfacesShouldTake()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<SittingWorkflow>(scope.ServiceProvider.GetRequiredService<ISittingWorkflow>());
        Assert.IsType<DapperSittingSettlement>(scope.ServiceProvider.GetRequiredService<ISittingSettlement>());
    }

    [Fact]
    public async Task AClosedSitting_IsAnnouncedOnceWithItsOwnIdentifier()
    {
        FakeSittingSettlement settlement = new(Closed(settledTotal: 41.50m, pendingAtClose: 2));
        RecordingBroadcaster broadcaster = new();

        CloseSittingResult result = await Workflow(settlement, broadcaster).CloseAndSettleAsync(
            SittingIdentifier,
            CounterPersonIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(SittingIdentifier, settlement.LastSittingIdentifier);
        Assert.Equal(CounterPersonIdentifier, settlement.LastClosedBy);

        // The result is passed through untouched — the caller needs the stamped total and the
        // still-pending count to say what was actually charged (§5.3).
        Assert.Equal(CloseSittingOutcome.Closed, result.Outcome);
        Assert.Equal(41.50m, result.SettledTotalAmount);
        Assert.Equal(2, result.PendingLineCountAtClose);

        SittingClosed published = Assert.IsType<SittingClosed>(Assert.Single(broadcaster.Published));
        Assert.Equal(SittingIdentifier, published.SittingIdentifier);
    }

    /// <summary>
    /// The losing side of two counters pressing Close together. Nothing was written, so nothing may be
    /// announced: the winner already announced it milliseconds earlier.
    /// </summary>
    [Fact]
    public async Task AnAlreadyClosedSitting_AnnouncesNothing()
    {
        FakeSittingSettlement settlement = new(new CloseSittingResult(
            CloseSittingOutcome.AlreadyClosed,
            SittingIdentifier,
            SettledTotalAmount: 41.50m,
            ClosedAt,
            ClosedByPersonIdentifier: CounterPersonIdentifier,
            PendingLineCountAtClose: 0));
        RecordingBroadcaster broadcaster = new();

        CloseSittingResult result = await Workflow(settlement, broadcaster).CloseAndSettleAsync(
            SittingIdentifier,
            CounterPersonIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal(CloseSittingOutcome.AlreadyClosed, result.Outcome);
        Assert.False(result.IsClosed);
        Assert.True(result.SittingIsClosed);
        Assert.Empty(broadcaster.Published);
    }

    [Fact]
    public async Task AnUnknownSitting_AnnouncesNothing()
    {
        FakeSittingSettlement settlement = new(new CloseSittingResult(
            CloseSittingOutcome.SittingNotFound,
            SittingIdentifier,
            SettledTotalAmount: null,
            ClosedAt: null,
            ClosedByPersonIdentifier: null,
            PendingLineCountAtClose: 0));
        RecordingBroadcaster broadcaster = new();

        await Workflow(settlement, broadcaster).CloseAndSettleAsync(
            SittingIdentifier,
            CounterPersonIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Empty(broadcaster.Published);
    }

    /// <summary>
    /// A sitting that settles at nothing still closed, and the table still has to flip. Zero is a total,
    /// not an absence — this guards against the tempting "only announce when money moved".
    /// </summary>
    [Fact]
    public async Task ASittingThatSettlesAtZero_IsStillAnnounced()
    {
        FakeSittingSettlement settlement = new(Closed(settledTotal: 0m, pendingAtClose: 0));
        RecordingBroadcaster broadcaster = new();

        await Workflow(settlement, broadcaster).CloseAndSettleAsync(
            SittingIdentifier,
            CounterPersonIdentifier,
            TestContext.Current.CancellationToken);

        Assert.IsType<SittingClosed>(Assert.Single(broadcaster.Published));
    }

    private static CloseSittingResult Closed(decimal settledTotal, int pendingAtClose)
        => new(
            CloseSittingOutcome.Closed,
            SittingIdentifier,
            settledTotal,
            ClosedAt,
            CounterPersonIdentifier,
            pendingAtClose);

    private static SittingWorkflow Workflow(
        ISittingSettlement settlement,
        IDomainEventBroadcaster broadcaster)
    {
        // A real RestaurantMetrics, not a stub: it is sealed and has no interface, so the only way to be
        // sure the §12 call site runs at all is to let it run against real instruments. AddMetrics()
        // supplies the meter factory it takes, exactly as Program.cs does. The provider is deliberately
        // not disposed — disposing it would dispose the Meter the returned workflow still holds. Same
        // shape as OrderWorkflowTests.
        ServiceProvider provider = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<RestaurantMetrics>()
            .BuildServiceProvider();

        return new SittingWorkflow(settlement, broadcaster, provider.GetRequiredService<RestaurantMetrics>());
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantTables: a clock, an identifier
        // factory, a connection factory, the bound options, the metrics (which need an IMeterFactory via
        // AddMetrics), the broadcaster, and Data Protection. The connection factory is never used —
        // resolution constructs, it does not connect — and an ephemeral key ring keeps the test off the
        // file system.
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

    private sealed class FakeSittingSettlement : ISittingSettlement
    {
        private readonly CloseSittingResult _result;

        public FakeSittingSettlement(CloseSittingResult result) => _result = result;

        public Guid? LastSittingIdentifier { get; private set; }

        public Guid? LastClosedBy { get; private set; }

        public Task<CloseSittingResult> CloseAndSettleAsync(
            Guid sittingIdentifier,
            Guid closedByPersonIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastSittingIdentifier = sittingIdentifier;
            LastClosedBy = closedByPersonIdentifier;
            return Task.FromResult(_result);
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
