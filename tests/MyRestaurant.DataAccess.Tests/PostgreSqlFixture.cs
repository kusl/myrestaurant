using Xunit;

namespace MyRestaurant.DataAccess.Tests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public string? ConnectionString { get; private set; }

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        PostgreSqlServerFixture.DatabaseLease lease =
            await PostgreSqlServerFixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        ConnectionString = lease.ConnectionString;
        SkipReason = lease.SkipReason;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
