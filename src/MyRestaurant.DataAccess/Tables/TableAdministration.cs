using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Tables;

/// <summary>The outcome of <see cref="ITableAdministration.CreateTableAsync"/>.</summary>
public enum CreateTableOutcome
{
    /// <summary>The table row was written with a freshly generated 32-byte join secret (§4.1).</summary>
    Created,

    /// <summary>The label was already taken (the <c>restaurant_table.label</c> UNIQUE constraint tripped); nothing was written.</summary>
    LabelTaken,
}

/// <summary>The outcome of <see cref="ITableAdministration.RenameTableAsync"/>.</summary>
public enum RenameTableOutcome
{
    /// <summary>The label was changed.</summary>
    Renamed,

    /// <summary>The new label equalled the current one; nothing changed.</summary>
    NoChange,

    /// <summary>Another table already uses the requested label (the UNIQUE constraint tripped); nothing changed.</summary>
    LabelTaken,

    /// <summary>No table exists with the given identifier; nothing changed.</summary>
    TableNotFound,
}

/// <summary>The outcome of <see cref="ITableAdministration.RotateJoinSecretAsync"/>.</summary>
public enum RotateJoinSecretOutcome
{
    /// <summary>
    /// A new 32-byte join secret was generated and <c>join_secret_rotated_at</c> was stamped, so every
    /// outstanding token for the table dies instantly (§4.1/§4.3).
    /// </summary>
    Rotated,

    /// <summary>No table exists with the given identifier; nothing changed.</summary>
    TableNotFound,
}

/// <summary>The outcome of <see cref="ITableAdministration.SetTableActiveAsync"/>.</summary>
public enum TableActivationOutcome
{
    /// <summary>The table's active state changed (deactivating stops token validation and display rendering, §4.1).</summary>
    Changed,

    /// <summary>The table was already in the requested state; nothing changed.</summary>
    NoChange,

    /// <summary>No table exists with the given identifier; nothing changed.</summary>
    TableNotFound,
}

/// <summary>
/// Administrative management of restaurant tables and their per-table join secret
/// (TECHNICAL_SPECIFICATION §4.1). Create a table (with a CSPRNG join secret), rename it, rotate the
/// join secret (killing every outstanding join token for it), and deactivate/reactivate it. The join
/// secret is generated and held entirely server-side — it is <b>never</b> returned to any caller or
/// client (displays receive only a rendered QR; §4.1/§4.2) — so, unlike account administration (§3.7),
/// there is nothing here for the web layer to hash or hand out.
///
/// <para>This is the write companion to the read-only <see cref="ITableDirectory"/>. It follows the
/// self-contained connection/transaction pattern the identity services established
/// (<c>DapperAccountAdministration</c>, §3.7): one connection and one transaction per operation, a
/// single <see cref="IClock.UtcNow"/> instant per operation, and application-generated UUIDv7
/// identifiers (ADR-0011). Table changes are not part of the <c>security_event</c> vocabulary (which is
/// person-scoped, §8.2), so no audit row is written.</para>
/// </summary>
public interface ITableAdministration
{
    /// <summary>
    /// Creates a table with the given label and a freshly generated 32-byte join secret (§4.1). The
    /// identifier is minted by the caller (ADR-0011) so it can link to the new table immediately; it
    /// becomes the <c>restaurant_table</c> row's id. A duplicate label yields
    /// <see cref="CreateTableOutcome.LabelTaken"/> and writes nothing.
    /// </summary>
    Task<CreateTableOutcome> CreateTableAsync(
        Guid tableIdentifier,
        string label,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a table's label. Returns <see cref="RenameTableOutcome.NoChange"/> when the new label
    /// equals the current one, <see cref="RenameTableOutcome.LabelTaken"/> when another table already
    /// uses it, and <see cref="RenameTableOutcome.TableNotFound"/> when no such table exists.
    /// </summary>
    Task<RenameTableOutcome> RenameTableAsync(
        Guid tableIdentifier,
        string label,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates the table's join secret to fresh 32 bytes and stamps <c>join_secret_rotated_at</c>, which
    /// invalidates every outstanding token for the table immediately (§4.1/§4.3). Returns
    /// <see cref="RotateJoinSecretOutcome.TableNotFound"/> when no such table exists.
    /// </summary>
    Task<RotateJoinSecretOutcome> RotateJoinSecretAsync(
        Guid tableIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a table's active state (§4.1 — deactivating stops token validation and display rendering).
    /// Returns <see cref="TableActivationOutcome.NoChange"/> when it is already in the requested state
    /// and <see cref="TableActivationOutcome.TableNotFound"/> when no such table exists.
    /// </summary>
    Task<TableActivationOutcome> SetTableActiveAsync(
        Guid tableIdentifier,
        bool isActive,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ITableAdministration"/>. Like
/// <c>DapperAccountAdministration</c> it owns its own connection and transaction per operation, stamps
/// timestamps with one <see cref="IClock.UtcNow"/> instant, and holds no state. The join secret is
/// produced with <see cref="SecretGenerator.GenerateJoinSecret"/> (a CSPRNG, 32 bytes, §4.1).
/// </summary>
public sealed class DapperTableAdministration : ITableAdministration
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public DapperTableAdministration(IDatabaseConnectionFactory connectionFactory, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);

        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<CreateTableOutcome> CreateTableAsync(
        Guid tableIdentifier,
        string label,
        CancellationToken cancellationToken = default)
    {
        string normalizedLabel = NormalizeLabel(label);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A fresh 32-byte join secret, generated server-side and never returned to any caller (§4.1).
        // join_secret_rotated_at is left NULL: rotation stamps it, so NULL reads as "never rotated".
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO restaurant_table (
                    restaurant_table_identifier, label, join_secret, join_secret_rotated_at, is_active, created_at)
                VALUES (
                    @Id, @Label, @JoinSecret, NULL, true, @CreatedAt);
                """,
                new
                {
                    Id = tableIdentifier,
                    Label = normalizedLabel,
                    JoinSecret = SecretGenerator.GenerateJoinSecret(),
                    CreatedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return CreateTableOutcome.LabelTaken;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CreateTableOutcome.Created;
    }

    public async Task<RenameTableOutcome> RenameTableAsync(
        Guid tableIdentifier,
        string label,
        CancellationToken cancellationToken = default)
    {
        string normalizedLabel = NormalizeLabel(label);

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        TableStateRow? current = await ReadStateAsync(connection, transaction, tableIdentifier, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RenameTableOutcome.TableNotFound;
        }

        // label is a case-sensitive `text UNIQUE` (not citext), so compare ordinally.
        if (string.Equals(current.Label, normalizedLabel, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RenameTableOutcome.NoChange;
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE restaurant_table SET label = @Label WHERE restaurant_table_identifier = @Id;",
                new { Label = normalizedLabel, Id = tableIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RenameTableOutcome.LabelTaken;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RenameTableOutcome.Renamed;
    }

    public async Task<RotateJoinSecretOutcome> RotateJoinSecretAsync(
        Guid tableIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A single conditional UPDATE: the affected-row count distinguishes a real rotation from a
        // missing table without a separate existence read.
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE restaurant_table
            SET join_secret = @JoinSecret, join_secret_rotated_at = @Now
            WHERE restaurant_table_identifier = @Id;
            """,
            new
            {
                JoinSecret = SecretGenerator.GenerateJoinSecret(),
                Now = now,
                Id = tableIdentifier,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RotateJoinSecretOutcome.TableNotFound;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RotateJoinSecretOutcome.Rotated;
    }

    public async Task<TableActivationOutcome> SetTableActiveAsync(
        Guid tableIdentifier,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        TableStateRow? current = await ReadStateAsync(connection, transaction, tableIdentifier, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return TableActivationOutcome.TableNotFound;
        }

        if (current.IsActive == isActive)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return TableActivationOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE restaurant_table SET is_active = @IsActive WHERE restaurant_table_identifier = @Id;",
            new { IsActive = isActive, Id = tableIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TableActivationOutcome.Changed;
    }

    private static async Task<TableStateRow?> ReadStateAsync(
        DbConnection connection, DbTransaction transaction, Guid tableIdentifier, CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<TableStateRow>(new CommandDefinition(
            """
            SELECT label AS Label, is_active AS IsActive
            FROM restaurant_table
            WHERE restaurant_table_identifier = @Id;
            """,
            new { Id = tableIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static string NormalizeLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return label.Trim();
    }

    // Dapper maps this positional record by constructor-parameter name against the aliased columns above.
    private sealed record TableStateRow(string Label, bool IsActive);
}
