using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess;
using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Tables;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

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

    [Fact]
    public void SittingDirectory_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        ISittingDirectory directory = scope.ServiceProvider.GetRequiredService<ISittingDirectory>();

        Assert.IsType<DapperSittingDirectory>(directory);
    }

    [Fact]
    public void SittingMembership_IsResolvableInAScope()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        ISittingMembership membership = scope.ServiceProvider.GetRequiredService<ISittingMembership>();

        Assert.IsType<DapperSittingMembership>(membership);
    }

    [Fact]
    public void JoinGrantProtector_IsASingletonAndProtectsWhatItUnprotects()
    {
        using ServiceProvider provider = BuildProvider();

        JoinGrantProtector first = provider.GetRequiredService<JoinGrantProtector>();
        JoinGrantProtector second = provider.GetRequiredService<JoinGrantProtector>();

        Assert.Same(first, second);

        JoinGrant grant = new(Guid.Parse("0192f000-0000-7000-8000-00000000ab01"), DateTimeOffset.UtcNow);
        Assert.True(first.TryUnprotect(first.Protect(grant), out JoinGrant? roundTripped));
        Assert.Equal(grant.TableIdentifier, roundTripped!.TableIdentifier);
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));
        services.AddMetrics();
        services.AddSingleton<RestaurantMetrics>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        services.AddRestaurantTables();

        return services.BuildServiceProvider();
    }

    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Wiring tests must not open a database connection.");
    }
}
