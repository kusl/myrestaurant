using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Tables;

/// <summary>
/// Reads a table's <c>join_secret</c> for server-side join-token work (TECHNICAL_SPECIFICATION §4.1,
/// §4.3). This is the deliberate, narrow counterpart to <see cref="ITableDirectory"/>, which never
/// selects the secret: the secret signs the rotating QR tokens guests scan and <b>must never leave the
/// server</b> (§4.1/§4.2), so the only thing allowed to read it is the token service that computes and
/// validates tokens from it — never a page, a view model, or any other caller. Keeping it behind its own
/// single-purpose interface makes that boundary explicit and substitutable in tests.
///
/// <para>The read is gated on <c>is_active = true</c>: §4.1 says deactivating a table stops its token
/// validation and display rendering, so an inactive (or non-existent) table simply has no readable
/// secret — the reader returns <c>null</c>, and the token service turns that into "cannot render" and
/// "invalid". Centralising the active-gate in this one predicate keeps the §4.1 rule in a single place
/// that both the render path and the validate path flow through.</para>
/// </summary>
public interface ITableJoinSecretReader
{
    /// <summary>
    /// The 32-byte join secret for the <b>active</b> table with the given identifier, or <c>null</c>
    /// when no such table exists or it has been deactivated (§4.1). The bytes are the live signing
    /// material; the caller must not retain or expose them beyond computing/validating a token.
    /// </summary>
    Task<byte[]?> ReadActiveJoinSecretAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ITableJoinSecretReader"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, matching the rest of the data layer; a single
/// scalar <c>SELECT</c> of the <c>bytea</c> column (which Npgsql materialises as a <c>byte[]</c>). No
/// transaction is needed — this is a lone read — and the secret column is selected nowhere else in a
/// reporting path (§4.1).
/// </summary>
public sealed class DapperTableJoinSecretReader : ITableJoinSecretReader
{
    // Only ever the secret, only ever for an active table (§4.1). ExecuteScalar returns null for a
    // missing/inactive row, which the caller reads as "no secret".
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
