using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MyRestaurant.Testing;

internal sealed class PostgreSqlTestServer : IAsyncDisposable
{
    private const string PostgreSqlImage = "docker.io/library/postgres:17-alpine";

    private const string Username = "myrestaurant";
    private const string Password = "myrestaurant";

    private const int ServerPort = 5432;

    private const int ProbeTimeoutSeconds = 3;
    private const int ReportedLogLines = 20;

    private const string StartupDeadlineText = "five minutes";

    private static readonly TimeSpan StartupDeadline = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AbandonAfter = StartupDeadline + TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DiagnosticsDeadline = TimeSpan.FromSeconds(10);

    private static readonly string[] UnresolvableReferenceMarkers =
    [
        "short-name",
        "unqualified-search",
        "did not resolve to an alias",
    ];

    private static TimeoutException? _firstTimeout;

    private readonly PostgreSqlContainer _container;

    private PostgreSqlTestServer(PostgreSqlContainer container, string connectionString)
    {
        _container = container;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal static async Task<PostgreSqlTestServer> StartAsync(string database)
    {
        if (Volatile.Read(ref _firstTimeout) is { } earlier)
        {
            throw new TimeoutException(earlier.Message, earlier);
        }

        PostgreSqlContainer container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase(database)
            .WithUsername(Username)
            .WithPassword(Password)
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new AcceptsConnectionsFromTheHost(database)))
            .WithLogger(NullLogger.Instance)
            .Build();

        using CancellationTokenSource deadline = new(StartupDeadline);

        try
        {
            await container.StartAsync(deadline.Token).WaitAsync(AbandonAfter, CancellationToken.None);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            string observed = await DescribeContainerAsync(container);
            await DisposeQuietlyAsync(container);

            TimeoutException timeout = new(
                "PostgreSQL in a container did not accept a connection from this host within"
                + $" {StartupDeadlineText}, so the suite stopped waiting instead of hanging. The"
                + $" container engine answered and was asked to start '{PostgreSqlImage}'; what never"
                + " happened is a successful `SELECT 1` over the published port. " + observed
                + " To see which half is at fault, run the same thing by hand:"
                + $" `podman run --rm -d -p 127.0.0.1:55432:5432 -e POSTGRES_PASSWORD=probe {PostgreSqlImage}`"
                + " and then `pg_isready -h 127.0.0.1 -p 55432`. If that answers, the engine's API"
                + " socket is what differs from its command line; `podman info` and"
                + " `podman system migrate` are the next two commands after an engine upgrade.",
                exception);

            Interlocked.CompareExchange(ref _firstTimeout, timeout, null);
            throw timeout;
        }
        catch
        {
            await DisposeQuietlyAsync(container);
            throw;
        }

        return new PostgreSqlTestServer(container, container.GetConnectionString());
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    internal static string DescribeUnavailable(Exception exception)
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
            + $" — the image these tests start is '{PostgreSqlImage}'. On a rootless-Podman host,"
            + " activate the user API socket once with `systemctl --user enable --now podman.socket`"
            + " and re-run; the tests discover it automatically. (Explicit configuration also works:"
            + " `export DOCKER_HOST=unix:///run/user/$(id -u)/podman/podman.sock`.)";
    }

    private static async Task<string> DescribeContainerAsync(PostgreSqlContainer container)
    {
        try
        {
            string identifier = container.Id;
            string state = container.State.ToString();

            (string standardOutput, string standardError) = await container
                .GetLogsAsync()
                .WaitAsync(DiagnosticsDeadline, CancellationToken.None);

            string[] lines = (standardOutput + "\n" + standardError)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string tail = lines.Length == 0
                ? "(the container wrote nothing)"
                : string.Join(" | ", lines.TakeLast(ReportedLogLines));

            return $"Container {identifier} was in state {state}; the last lines it wrote were: {tail}.";
        }
        catch (Exception exception)
        {
            return
                "The container's state and logs could not be read either (" + exception.Message
                + "), which points at the engine's API rather than at PostgreSQL.";
        }
    }

    private static async Task DisposeQuietlyAsync(PostgreSqlContainer container)
    {
        try
        {
            await container.DisposeAsync().AsTask().WaitAsync(DiagnosticsDeadline, CancellationToken.None);
        }
        catch (Exception)
        {
        }
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

    private sealed class AcceptsConnectionsFromTheHost(string database) : IWaitUntil
    {
        public async Task<bool> UntilAsync(IContainer container)
        {
            NpgsqlConnectionStringBuilder target = new()
            {
                Host = container.Hostname,
                Port = container.GetMappedPublicPort(ServerPort),
                Database = database,
                Username = Username,
                Password = Password,
                Pooling = false,
                Timeout = ProbeTimeoutSeconds,
                CommandTimeout = ProbeTimeoutSeconds,
            };

            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(ProbeTimeoutSeconds * 2));

            try
            {
                await using NpgsqlConnection connection = new(target.ConnectionString);
                await connection.OpenAsync(limit.Token);

                await using NpgsqlCommand command = new("SELECT 1;", connection);
                await command.ExecuteScalarAsync(limit.Token);

                return true;
            }
            catch (Exception exception) when (exception
                is NpgsqlException
                or SocketException
                or IOException
                or TimeoutException
                or InvalidOperationException
                or OperationCanceledException)
            {
                return false;
            }
        }
    }
}
