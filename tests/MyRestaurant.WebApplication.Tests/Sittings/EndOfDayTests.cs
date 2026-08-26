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

public sealed class EndOfDayTests
{
    private static readonly Guid FirstSitting = Guid.Parse("0192f000-0000-7000-8000-0000000e0001");
    private static readonly Guid SecondSitting = Guid.Parse("0192f000-0000-7000-8000-0000000e0002");
    private static readonly Guid ThirdSitting = Guid.Parse("0192f000-0000-7000-8000-0000000e0003");
    private static readonly Guid AdministratorIdentifier = Guid.Parse("0192f000-0000-7000-8000-0000000e00a1");
    private static readonly DateTimeOffset ClosedAt = new(2026, 6, 4, 23, 15, 0, TimeSpan.Zero);

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

        Assert.Equal(3, broadcaster.Published.Count);
        Assert.Equal(
            [FirstSitting, SecondSitting, ThirdSitting],
            broadcaster.Published.Cast<SittingClosed>().Select(closed => closed.SittingIdentifier));
    }

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

        Assert.Equal(41.50m, result.SettledTotalAmount);

        SittingClosed published = Assert.IsType<SittingClosed>(Assert.Single(broadcaster.Published));
        Assert.Equal(FirstSitting, published.SittingIdentifier);

        Assert.Equal(3, settlement.Attempted.Count);
    }

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

        Assert.Equal(0, result.ClosedCount);
        Assert.Equal(0, result.AlreadyClosedCount);
        Assert.Equal(0, result.NotFoundCount);
        Assert.Equal(0m, result.SettledTotalAmount);
    }

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
            }
        }
    }
}
