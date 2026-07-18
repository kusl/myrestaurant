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
    private PostgreSqlContainer? _container;

    /// <summary>The Npgsql connection string once the container is up; otherwise <c>null</c>.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Non-null when the container could not start; the reason to pass to <c>Assert.Skip</c>.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            // Build here rather than in a field initializer: PostgreSqlBuilder.Build() validates
            // container-engine connectivity eagerly, so constructing the container in the field
            // initializer (i.e. the fixture ctor) would throw a DockerUnavailableException BEFORE
            // this try/catch — reporting every test as a "class fixture threw in its constructor"
            // failure instead of the intended skip.
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("myrestaurant")
                .WithUsername("myrestaurant")
                .WithPassword("myrestaurant")
                .Build();

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
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
