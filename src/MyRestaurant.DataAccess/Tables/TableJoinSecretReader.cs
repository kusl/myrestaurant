using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Tables;

public interface ITableJoinSecretReader
{
    Task<byte[]?> ReadActiveJoinSecretAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);
}

public sealed class DapperTableJoinSecretReader : ITableJoinSecretReader
{
    private const string ReadActiveSecretSql = """
        SELECT join_secret
        FROM restaurant_table
        WHERE restaurant_table_identifier = @TableIdentifier
          AND is_active = true;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperTableJoinSecretReader(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<byte[]?> ReadActiveJoinSecretAsync(Guid tableIdentifier, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        byte[]? secret = await connection.ExecuteScalarAsync<byte[]>(new CommandDefinition(
            ReadActiveSecretSql,
            new { TableIdentifier = tableIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return secret;
    }
}
