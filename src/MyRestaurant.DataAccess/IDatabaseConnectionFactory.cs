using System.Data.Common;

namespace MyRestaurant.DataAccess;

/// <summary>
/// Opens connections to the single PostgreSQL database. Registered as a singleton wrapping a
/// pooled <c>NpgsqlDataSource</c>; repositories and Identity stores take a dependency on this
/// rather than a raw connection string. Returns the abstract <see cref="DbConnection"/> so
/// callers (and Dapper) stay decoupled from Npgsql surface area.
/// </summary>
public interface IDatabaseConnectionFactory
{
    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
