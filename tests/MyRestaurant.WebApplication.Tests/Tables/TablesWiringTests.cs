using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Tables;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies the table wiring composed by
/// <see cref="TablesServiceCollectionExtensions.AddRestaurantTables"/> (TECHNICAL_SPECIFICATION §4): the
/// read-only <see cref="ITableDirectory"/> and transactional <see cref="ITableAdministration"/>
/// management services (§4.1), plus the join-token services (§4.3–§4.5) — the server-only
/// <see cref="ITableJoinSecretReader"/> and the <see cref="ITableJoinTokens"/> that depends on it — all
/// resolve to their concrete implementations. Constructing them opens no connection (they only capture
/// the connection factory, clock, options, and metrics), so this resolves without a database — mirroring
/// the resolvability facts in <see cref="IdentityWiringTests"/>.
/// </summary>
public sealed class TablesWiringTests
{
    [Fact]
    public void TableDirectory_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        ITableDirectory directory = scope.ServiceProvider.GetRequiredService<ITableDirectory>();

        Assert.IsType<DapperTableDirectory>(directory);
    }

    [Fact]
    public void TableAdministration_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        ITableAdministration administration = scope.ServiceProvider.GetRequiredService<ITableAdministration>();

        Assert.IsType<DapperTableAdministration>(administration);
    }

    [Fact]
    public void TableJoinSecretReader_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        ITableJoinSecretReader reader = scope.ServiceProvider.GetRequiredService<ITableJoinSecretReader>();

        Assert.IsType<DapperTableJoinSecretReader>(reader);
    }

    [Fact]
    public void TableJoinTokens_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        ITableJoinTokens tokens = scope.ServiceProvider.GetRequiredService<ITableJoinTokens>();

        Assert.IsType<TableJoinTokens>(tokens);
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantTables: a clock, a connection
        // factory, the bound options, and the metrics (which need an IMeterFactory via AddMetrics). The
        // connection factory is never used here — resolution constructs, it does not connect.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));
        services.AddMetrics();
        services.AddSingleton<RestaurantMetrics>();

        services.AddRestaurantTables();

        return services.BuildServiceProvider();
    }

    /// <summary>The wiring tests never open a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }
}
