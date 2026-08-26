using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Events;
using MyRestaurant.WebApplication.Events;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

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

        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();

        services.AddRestaurantEventExplorer();

        return services.BuildServiceProvider();
    }

    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }
}
