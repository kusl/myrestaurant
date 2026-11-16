using MyRestaurant.DataAccess.Tests;
using MyRestaurant.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(PostgreSqlServerFixture))]

namespace MyRestaurant.DataAccess.Tests;

public sealed class PostgreSqlServerFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<ServerStart>> Start =
        new(StartServerAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static int _databaseCounter;

    public async ValueTask InitializeAsync() => _ = await Start.Value;

    public async ValueTask DisposeAsync()
    {
        if (!Start.IsValueCreated || !Start.Value.IsCompletedSuccessfully)
        {
            return;
        }

        ServerStart start = await Start.Value;

        if (start.Server is not null)
        {
            await start.Server.DisposeAsync();
        }
    }

    internal static async Task<DatabaseLease> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        ServerStart start = await Start.Value;

        if (start.Server is null)
        {
            return new DatabaseLease(ConnectionString: null, start.SkipReason);
        }

        int ordinal = Interlocked.Increment(ref _databaseCounter);
        string connectionString = await start.Server.CreateDatabaseAsync($"data_access_{ordinal}", cancellationToken);

        return new DatabaseLease(connectionString, SkipReason: null);
    }

    private static async Task<ServerStart> StartServerAsync()
    {
        try
        {
            return new ServerStart(await PostgreSqlTestServer.StartAsync(), SkipReason: null);
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            return new ServerStart(Server: null, PostgreSqlTestServer.DescribeUnavailable(exception));
        }
    }

    internal sealed record DatabaseLease(string? ConnectionString, string? SkipReason);

    private sealed record ServerStart(PostgreSqlTestServer? Server, string? SkipReason);
}
