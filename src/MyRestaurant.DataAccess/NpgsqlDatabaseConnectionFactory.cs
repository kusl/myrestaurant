using System.Data.Common;
using Npgsql;

namespace MyRestaurant.DataAccess;

/// <summary>
/// The Npgsql-backed <see cref="IDatabaseConnectionFactory"/>. Owns one pooled
/// <see cref="NpgsqlDataSource"/> for the process (created once, disposed at shutdown), which
/// is also where the Npgsql OpenTelemetry ActivitySource is enabled from the web layer (§12).
/// </summary>
public sealed class NpgsqlDatabaseConnectionFactory : IDatabaseConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlDatabaseConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
