using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Menu;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class KitchenWiringTests
{
    [Fact]
    public void KitchenBoardReads_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperKitchenBoardReads>(scope.ServiceProvider.GetRequiredService<IKitchenBoardReads>());
    }

    [Fact]
    public void KitchenNotifications_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperKitchenNotifications>(scope.ServiceProvider.GetRequiredService<IKitchenNotifications>());
    }

    [Fact]
    public void MenuAvailability_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperMenuAvailability>(scope.ServiceProvider.GetRequiredService<IMenuAvailability>());
    }

    [Fact]
    public void MenuWorkflow_IsResolvableInAScope_AndIsTheServiceSurfacesShouldTake()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<MenuWorkflow>(scope.ServiceProvider.GetRequiredService<IMenuWorkflow>());
        Assert.IsType<DapperMenuAvailability>(scope.ServiceProvider.GetRequiredService<IMenuAvailability>());
    }

    [Fact]
    public void TheReminderServiceIsRegisteredAsAHostedService()
    {
        using ServiceProvider provider = BuildProvider();

        IHostedService[] hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.Single(hosted);
        Assert.IsType<KitchenReminderService>(hosted[0]);
    }

    [Fact]
    public void TheReminderServiceScansOnTheIntervalTheSpecificationNames()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), KitchenReminderService.ScanInterval);
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));
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
