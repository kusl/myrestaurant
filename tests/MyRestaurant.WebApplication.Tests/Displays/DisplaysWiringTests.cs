using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// their concrete implementations, and the pairing rate limiter is registered. Constructing them opens
/// no connection (they only capture the connection factory, clock, and identifier factory), so this
/// resolves without a database — mirroring the resolvability facts in <c>TablesWiringTests</c>.
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
    public void RateLimiter_IsRegisteredAndRefusesWithTooManyRequests()
    {
        using ServiceProvider provider = BuildProvider();

        // AddRateLimiter is what makes app.UseRateLimiter() legal; without it the middleware throws at
        // startup. Resolving the options proves the call happened and pins the refusal status: the
        // framework default is 503, which would misreport a brute-force block as a sick server.
        RateLimiterOptions limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.Equal(StatusCodes.Status429TooManyRequests, limiter.RejectionStatusCode);
        Assert.NotNull(limiter.OnRejected);

        // No global limiter: only endpoints carrying [EnableRateLimiting] are affected, so nothing else
        // in the application silently acquires a budget.
        Assert.Null(limiter.GlobalLimiter);
    }

    [Fact]
    public void PairingRateLimit_MatchesTheSpecifiedBudget()
    {
        // §4.2: "/display/pair (anonymous; rate-limited 5 attempts/minute/IP)". The policy body is not
        // publicly inspectable, so the numbers are pinned where the policy and the page both read them.
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
