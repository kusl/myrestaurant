using MyRestaurant.Testing;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string DatabaseName = "myrestaurant";

    private PostgreSqlTestServer? _server;

    public string? ConnectionString { get; private set; }

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _server = await PostgreSqlTestServer.StartAsync(DatabaseName);
            ConnectionString = _server.ConnectionString;
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            SkipReason = PostgreSqlTestServer.DescribeUnavailable(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}
