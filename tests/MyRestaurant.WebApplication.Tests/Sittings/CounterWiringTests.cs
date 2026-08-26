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

        Assert.Equal(CloseSittingOutcome.Closed, result.Outcome);
        Assert.Equal(41.50m, result.SettledTotalAmount);
        Assert.Equal(2, result.PendingLineCountAtClose);

        SittingClosed published = Assert.IsType<SittingClosed>(Assert.Single(broadcaster.Published));
        Assert.Equal(SittingIdentifier, published.SittingIdentifier);
    }

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
        ServiceProvider provider = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<RestaurantMetrics>()
            .BuildServiceProvider();

        return new SittingWorkflow(settlement, broadcaster, provider.GetRequiredService<RestaurantMetrics>());
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

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
            }
        }
    }
}
