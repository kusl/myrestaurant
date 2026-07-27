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

/// <summary>
/// Verifies the kitchen half of the wiring composed by
/// <see cref="OrdersServiceCollectionExtensions.AddRestaurantOrders"/> (TECHNICAL_SPECIFICATION §8.4,
/// §10, §11.2). <see cref="OrdersWiringTests"/> covers the ordering
/// services themselves; this covers what the kitchen board and the reminder loop need, and constructing
/// any of it opens no connection.
///
/// <para><see cref="TheReminderServiceIsRegisteredAsAHostedService"/> is the fact with teeth. §10.2's
/// reminder is the one behaviour in the system whose bug is <em>silence</em> — a missing registration
/// produces an application that starts cleanly, serves every page, alerts correctly on each send, and
/// simply never reminds. Nothing else in the test suite would notice.</para>
/// </summary>
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

    /// <summary>
    /// Surfaces take the workflow, never the raw write: an 86 that skipped the §9 broadcast would leave
    /// the item selectable in every open guest picker until that page happened to reload, and the guest
    /// would then have a whole send refused for it (§6.5.9).
    /// </summary>
    [Fact]
    public void MenuWorkflow_IsResolvableInAScope_AndIsTheServiceSurfacesShouldTake()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<MenuAvailabilityWorkflow>(scope.ServiceProvider.GetRequiredService<IMenuWorkflow>());
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

    /// <summary>§8.4: "The reminder background service (§10.2) runs every ~5 seconds".</summary>
    [Fact]
    public void TheReminderServiceScansOnTheIntervalTheSpecificationNames()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), KitchenReminderService.ScanInterval);
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantOrders. RestaurantOptions and
        // logging are here (and not in OrdersWiringTests) because the hosted service takes both; the
        // connection factory is never used — resolution constructs, it does not connect.
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

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }

    /// <summary>
    /// Constructing the reminder service captures the broadcaster; nothing here starts it, so nothing
    /// publishes. Throwing makes that assumption load-bearing rather than incidental.
    /// </summary>
    private sealed class UnusedBroadcaster : IDomainEventBroadcaster
    {
        public void Publish(DomainNotification notification)
            => throw new InvalidOperationException("Wiring tests must not publish notifications.");

        public IDisposable Subscribe(Action<DomainNotification> handler)
            => throw new InvalidOperationException("Wiring tests must not subscribe.");
    }
}
