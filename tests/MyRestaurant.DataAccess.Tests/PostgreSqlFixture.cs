using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string PostgreSqlImage = "docker.io/library/postgres:17-alpine";

    private static readonly string[] UnresolvableReferenceMarkers =
    [
        "short-name",
        "unqualified-search",
        "did not resolve to an alias",
    ];

    private PostgreSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder(PostgreSqlImage)
                .WithDatabase("myrestaurant")
                .WithUsername("myrestaurant")
                .WithPassword("myrestaurant")
                .WithLogger(NullLogger.Instance)
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            SkipReason = DescribeFailure(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        string detail = Flatten(exception);

        if (MentionsUnresolvableReference(detail))
        {
            return
                $"The image reference '{PostgreSqlImage}' could not be resolved to a registry by this"
                + " container engine: " + detail
                + " — this is not an unreachable engine. A reference without a registry component is"
                + " resolved through `unqualified-search-registries`, which a stock Debian ships"
                + " commented out (F-51, F-60). The reference above is fully qualified, so if this is"
                + " what failed then the engine could not reach the registry it names, or the local"
                + " store has the image under a different name. Check network egress to that"
                + " registry, or pre-pull it by hand.";
        }

        return
            "A container engine (Podman/Docker) was not reachable: " + exception.Message
            + $" — the image this fixture starts is '{PostgreSqlImage}'. On a rootless-Podman host,"
            + " activate the user API socket once with `systemctl --user enable --now podman.socket`"
            + " and re-run; the tests discover it automatically. (Explicit configuration also works:"
            + " `export DOCKER_HOST=unix:///run/user/$(id -u)/podman/podman.sock`.)";
    }

    private static bool MentionsUnresolvableReference(string detail)
    {
        foreach (string marker in UnresolvableReferenceMarkers)
        {
            if (detail.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Flatten(Exception exception)
    {
        List<string> messages = [];

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Length > 0)
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" | ", messages);
    }
}
