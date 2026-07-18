using Testcontainers.PostgreSql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

/// <summary>
/// Starts one PostgreSQL 17 container for the whole test class (Testcontainers). If no container
/// engine is available (Podman/Docker not installed or not running), startup fails and
/// <see cref="SkipReason"/> is set so the tests skip rather than fail (BUILD_PROGRESS:
/// container-dependent tests).
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("myrestaurant")
        .WithUsername("myrestaurant")
        .WithPassword("myrestaurant")
        .Build();

    /// <summary>The Npgsql connection string once the container is up; otherwise <c>null</c>.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Non-null when the container could not start; the reason to pass to <c>Assert.Skip</c>.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            SkipReason = "A container engine (Podman/Docker) is required but was unavailable: " + exception.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (ConnectionString is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
