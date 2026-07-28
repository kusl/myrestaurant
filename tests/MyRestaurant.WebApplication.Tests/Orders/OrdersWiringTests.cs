using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies the ordering wiring composed by
/// <see cref="OrdersServiceCollectionExtensions.AddRestaurantOrders"/> (TECHNICAL_SPECIFICATION §6, §7,
/// §8.3, §9, §12): the menu read side, the single order write path, the read paths, §6.8's visibility
/// pair, and the post-commit workflow shells all resolve to their concrete implementations. Constructing them opens no
/// connection — they only capture the connection factory, clock, identifier factory, broadcaster, and
/// metrics — so this resolves without a database, mirroring <see cref="TablesWiringTests"/>.
///
/// <para>Two facts here have teeth, and they are the same fact twice: <see cref="IOrderWorkflow"/> and
/// <see cref="IOrderVisibilityWorkflow"/> are each reachable <em>and</em> resolving them drags in the real
/// write service underneath, because a surface that resolved either write directly would commit a change
/// nobody hears about — an order the kitchen never sees (§9, §10.1), or a hidden order that stays on the
/// history page it was hidden from (§6.8, §9).</para>
/// </summary>
public sealed class OrdersWiringTests
{
    [Fact]
    public void MenuDirectory_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuDirectory>(scope.ServiceProvider.GetRequiredService<IMenuDirectory>());
    }

    [Fact]
    public void OrderMutations_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperOrderMutations>(scope.ServiceProvider.GetRequiredService<IOrderMutations>());
    }

    [Fact]
    public void OrderReadModel_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperOrderReadModel>(scope.ServiceProvider.GetRequiredService<IOrderReadModel>());
    }

    [Fact]
    public void OrderEventLog_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperOrderEventLog>(scope.ServiceProvider.GetRequiredService<IOrderEventLog>());
    }

    [Fact]
    public void OrderWorkflow_IsResolvableInAScope_AndIsTheServiceSurfacesShouldTake()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        IOrderWorkflow workflow = scope.ServiceProvider.GetRequiredService<IOrderWorkflow>();

        Assert.IsType<OrderWorkflow>(workflow);

        // Resolving it constructed the whole chain — the mutation service, the broadcaster, and the
        // metrics — without touching a database.
        Assert.IsType<DapperOrderMutations>(scope.ServiceProvider.GetRequiredService<IOrderMutations>());
    }

    /// <summary>
    /// §6.8's read side. The one reader in the tree that <em>enforces</em> something rather than only
    /// projecting: both person-scoped queries exclude hidden orders in SQL, so no surface can forget the
    /// filter (§6.8 — a hidden order is gone from the owner's own views).
    /// </summary>
    [Fact]
    public void OrderHistoryReads_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperOrderHistoryReads>(
            scope.ServiceProvider.GetRequiredService<IOrderHistoryReads>());
    }

    /// <summary>
    /// §6.8's write side, and the shell surfaces are supposed to take instead of it. Resolving the workflow
    /// drags in a real <see cref="IOrderVisibility"/>, because a page that resolved the write service
    /// directly would hide an order without announcing it — and §9 routes
    /// <see cref="MyRestaurant.Domain.LiveUpdates.VisibilityChanged"/> to exactly the history views that
    /// would otherwise keep showing it.
    /// </summary>
    [Fact]
    public void OrderVisibilityWorkflow_IsResolvableInAScope_AndIsTheServiceSurfacesShouldTake()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<OrderVisibilityWorkflow>(
            scope.ServiceProvider.GetRequiredService<IOrderVisibilityWorkflow>());

        Assert.IsType<DapperOrderVisibility>(
            scope.ServiceProvider.GetRequiredService<IOrderVisibility>());
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantOrders: a clock, an identifier
        // factory, a connection factory, the broadcaster, and the metrics (which need an IMeterFactory
        // via AddMetrics). The connection factory is never used here — resolution constructs, it does
        // not connect.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton<IDomainEventBroadcaster, UnusedBroadcaster>();
        services.AddMetrics();
        services.AddSingleton<RestaurantMetrics>();

        services.AddRestaurantOrders();

        return services.BuildServiceProvider();
    }

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }

    /// <summary>
    /// The real in-process broadcaster would work here too; this keeps the test about the Orders
    /// registrations rather than about anything the broadcaster happens to depend on.
    /// </summary>
    private sealed class UnusedBroadcaster : IDomainEventBroadcaster
    {
        public void Publish(DomainNotification notification)
            => throw new InvalidOperationException("Wiring tests must not publish notifications.");

        public IDisposable Subscribe(Action<DomainNotification> handler)
            => throw new InvalidOperationException("Wiring tests must not subscribe.");
    }
}
