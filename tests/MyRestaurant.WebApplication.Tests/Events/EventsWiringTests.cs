using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Events;
using MyRestaurant.WebApplication.Events;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies the wiring composed by
/// <see cref="EventsServiceCollectionExtensions.AddRestaurantEventExplorer"/>
/// (TECHNICAL_SPECIFICATION §11.4). Constructing the reader opens no connection — it only captures the
/// connection factory — so this resolves without a database, mirroring <see cref="OrdersWiringTests"/>
/// and <see cref="TablesWiringTests"/>.
///
/// <para>The second fact is the one with teeth. This extension is the only one in the tree that does not
/// belong to a subsystem, and its whole justification is that its single dependency is the connection
/// factory every other data service already takes. A registration that quietly grew a dependency on the
/// clock, the identifier factory, the broadcaster, or the metrics would mean the explorer had acquired a
/// write path or a notification, and it must never have either: it is a window on three append-only
/// logs, and the screens that own each subsystem are the only things allowed to append to them.</para>
/// </summary>
public sealed class EventsWiringTests
{
    [Fact]
    public void EventExplorerReads_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperEventExplorerReads>(
            scope.ServiceProvider.GetRequiredService<IEventExplorerReads>());
    }

    /// <summary>
    /// The connection factory is the whole of it. Nothing else is registered here, and nothing else needs
    /// to be — which is precisely why this extension can stand alone rather than being welded to one of
    /// the four subsystem extensions.
    /// </summary>
    [Fact]
    public void EventExplorer_NeedsNothingButTheConnectionFactory()
    {
        ServiceCollection services = new();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddRestaurantEventExplorer();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<DapperEventExplorerReads>(
            scope.ServiceProvider.GetRequiredService<IEventExplorerReads>());
    }

    /// <summary>Scoped, like every other data service — one per request, never captured by a singleton.</summary>
    [Fact]
    public void EventExplorerReads_IsRegisteredScoped()
    {
        ServiceCollection services = new();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddRestaurantEventExplorer();

        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IEventExplorerReads));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The one prerequisite Program.cs registers before AddRestaurantEventExplorer. It is never used
        // here — resolution constructs, it does not connect.
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();

        services.AddRestaurantEventExplorer();

        return services.BuildServiceProvider();
    }

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }
}
