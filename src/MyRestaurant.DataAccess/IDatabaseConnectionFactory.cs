using System.Data.Common;

namespace MyRestaurant.DataAccess;

public interface IDatabaseConnectionFactory
{
    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
