using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Tables;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies the table-management wiring composed by
/// <see cref="TablesServiceCollectionExtensions.AddRestaurantTables"/> (TECHNICAL_SPECIFICATION §4.1):
/// the read-only <see cref="ITableDirectory"/> and the transactional <see cref="ITableAdministration"/>
/// resolve to their Dapper implementations. Constructing them opens no connection (they only capture
/// the connection factory and clock), so this resolves without a database — mirroring the
/// resolvability facts in <see cref="IdentityWiringTests"/>.
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

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // The prerequisites Program.cs registers before AddRestaurantTables (a clock and a connection
        // factory). The factory is never used here — resolution constructs, it does not connect.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();

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
