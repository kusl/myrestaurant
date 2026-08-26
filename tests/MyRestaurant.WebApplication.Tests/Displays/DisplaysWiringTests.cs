using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Displays;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Displays;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Displays;

public sealed class DisplaysWiringTests
{
    [Fact]
    public void DisplayDeviceDirectory_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        IDisplayDeviceDirectory directory = scope.ServiceProvider.GetRequiredService<IDisplayDeviceDirectory>();

        Assert.IsType<DapperDisplayDeviceDirectory>(directory);
    }

    [Fact]
    public void DisplayDevicePairing_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        IDisplayDevicePairing pairing = scope.ServiceProvider.GetRequiredService<IDisplayDevicePairing>();

        Assert.IsType<DapperDisplayDevicePairing>(pairing);
    }

    [Fact]
    public void DisplayDeviceAuthenticator_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        IDisplayDeviceAuthenticator authenticator =
            scope.ServiceProvider.GetRequiredService<IDisplayDeviceAuthenticator>();

        Assert.IsType<DapperDisplayDeviceAuthenticator>(authenticator);
    }

    [Fact]
    public void PairingRateLimit_MatchesTheSpecifiedBudget()
    {
        Assert.Equal(5, DisplayRoutes.PairingAttemptsPerWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), DisplayRoutes.PairingRateLimitWindow);
        Assert.Equal("/display/pair", DisplayRoutes.Pair);
        Assert.StartsWith(DisplayRoutes.Prefix, DisplayRoutes.Pair, StringComparison.Ordinal);
    }

    [Fact]
    public void ForTable_BuildsThePathTheRouteTemplateMatches()
    {
        Guid tableIdentifier = Guid.Parse("0192f000-0000-7000-8000-00000000ab01");

        Assert.Equal($"/display/{tableIdentifier:D}", DisplayRoutes.ForTable(tableIdentifier));

        Assert.NotEqual(DisplayRoutes.Pair, DisplayRoutes.ForTable(tableIdentifier));
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));

        services.AddRestaurantDisplays();

        return services.BuildServiceProvider();
    }

    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }
}
