using Testcontainers.PostgreSql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

/// <summary>
/// Starts one PostgreSQL 17 container for the whole test class (Testcontainers). If no container
/// engine is reachable, startup fails and <see cref="SkipReason"/> is set so the tests skip rather
/// than fail (BUILD_PROGRESS: container-dependent tests).
///
/// <para>Endpoint discovery order: explicit configuration (<c>DOCKER_HOST</c>,
/// <c>~/.testcontainers.properties</c>) → the Docker default socket → the rootless Podman user
/// socket, which <see cref="ContainerEngineDiscovery"/> wires up automatically when it exists. On a
/// Podman host where the socket has never been activated, the skip reason below spells out the
/// one-time fix instead of only echoing Testcontainers' Docker-flavoured error.</para>
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
            SkipReason =
                "A container engine (Podman/Docker) was not reachable: " + exception.Message +
                " — on a rootless-Podman host, activate the user API socket once with" +
                " `systemctl --user enable --now podman.socket` and re-run; the tests discover it" +
                " automatically. (Explicit configuration also works:" +
                " `export DOCKER_HOST=unix:///run/user/$(id -u)/podman/podman.sock`.)";
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
