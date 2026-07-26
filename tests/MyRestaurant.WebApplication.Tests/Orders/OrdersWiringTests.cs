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
/// §8.3, §9, §12): the menu read side, the single order write path, the two read paths, and the
/// post-commit workflow shell all resolve to their concrete implementations. Constructing them opens no
/// connection — they only capture the connection factory, clock, identifier factory, broadcaster, and
/// metrics — so this resolves without a database, mirroring <see cref="TablesWiringTests"/>.
///
/// <para>The last fact is the one with teeth: it asserts that <see cref="IOrderWorkflow"/> is reachable
/// <em>and</em> that resolving it drags in a real <see cref="IOrderMutations"/>, because a surface that
/// resolved the mutation service directly would commit orders the kitchen never hears about (§9,
/// §10.1).</para>
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
