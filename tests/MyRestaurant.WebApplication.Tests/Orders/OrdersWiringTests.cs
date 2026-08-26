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

        Assert.IsType<DapperOrderMutations>(scope.ServiceProvider.GetRequiredService<IOrderMutations>());
    }

    [Fact]
    public void OrderHistoryReads_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperOrderHistoryReads>(
            scope.ServiceProvider.GetRequiredService<IOrderHistoryReads>());
    }

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

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton<IDomainEventBroadcaster, UnusedBroadcaster>();
        services.AddMetrics();
        services.AddSingleton<RestaurantMetrics>();

        services.AddRestaurantOrders();

        return services.BuildServiceProvider();
    }

    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }

    private sealed class UnusedBroadcaster : IDomainEventBroadcaster
    {
        public void Publish(DomainNotification notification)
            => throw new InvalidOperationException("Wiring tests must not publish notifications.");

        public IDisposable Subscribe(Action<DomainNotification> handler)
            => throw new InvalidOperationException("Wiring tests must not subscribe.");
    }
}
