using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Tables;

public enum CreateTableOutcome
{
    Created,
    LabelTaken,
}

public enum RenameTableOutcome
{
    Renamed,
    NoChange,
    LabelTaken,
    TableNotFound,
}

public enum RotateJoinSecretOutcome
{
    Rotated,
    TableNotFound,
}

public enum TableActivationOutcome
{
    Changed,
    NoChange,
    TableNotFound,
}

public interface ITableAdministration
{
    Task<CreateTableOutcome> CreateTableAsync(
        Guid tableIdentifier,
        string label,
        CancellationToken cancellationToken = default);

    Task<RenameTableOutcome> RenameTableAsync(
        Guid tableIdentifier,
        string label,
        CancellationToken cancellationToken = default);

    Task<RotateJoinSecretOutcome> RotateJoinSecretAsync(
        Guid tableIdentifier,
        CancellationToken cancellationToken = default);

    Task<TableActivationOutcome> SetTableActiveAsync(
        Guid tableIdentifier,
        bool isActive,
        CancellationToken cancellationToken = default);
}

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

    private sealed record TableStateRow(string Label, bool IsActive);
}
