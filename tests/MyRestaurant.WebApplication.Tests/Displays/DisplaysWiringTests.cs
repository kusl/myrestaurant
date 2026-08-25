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

/// <summary>
/// Verifies the display wiring composed by
/// <see cref="DisplaysServiceCollectionExtensions.AddRestaurantDisplays"/> (TECHNICAL_SPECIFICATION
/// §4.2): the read-only <see cref="IDisplayDeviceDirectory"/>, the transactional
/// <see cref="IDisplayDevicePairing"/>, and the <see cref="IDisplayDeviceAuthenticator"/> all resolve to
/// their concrete implementations. Constructing them opens no connection (they only capture the
/// connection factory, clock, and identifier factory), so this resolves without a database — mirroring
/// the resolvability facts in <c>TablesWiringTests</c>.
///
/// <para>The pairing rate limiter used to be asserted here, because it used to be registered here. Both
/// moved in Slice 62 (<b>F-115</b>): the limiter's rejection handler is single-valued and therefore not a
/// display concern, and asking this extension for <c>RateLimiterOptions</c> now would resolve a framework
/// default and assert nothing. The claim lives in <c>Security/RateLimitingContractTests.cs</c>. §4.2's
/// budget is still asserted below, since <c>DisplayRoutes</c> still carries it.</para>
/// </summary>
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
        // §4.2: "/display/pair (anonymous; rate-limited 5 attempts/minute/IP)". The policy body is not
        // publicly inspectable, so the numbers are pinned where the policy and the page both read them.
        // The budget stayed on DisplayRoutes when the policy NAME moved to RateLimitedSurfaces (F-115):
        // this is §4.2's number about this area, not a key shared with another surface.
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

        // The pairing route is a literal segment, so it wins over the {TableId:guid} parameter and the
        // two can never collide.
        Assert.NotEqual(DisplayRoutes.Pair, DisplayRoutes.ForTable(tableIdentifier));
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantDisplays: a clock, an identifier
        // factory, a connection factory, and the bound options. The connection factory is never used
        // here — resolution constructs, it does not connect.
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));

        services.AddRestaurantDisplays();

        return services.BuildServiceProvider();
    }

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }
}
