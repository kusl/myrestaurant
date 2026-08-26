using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Sittings;

public enum JoinTableOutcome
{
    SittingOpened,
    JoinedOpenSitting,
    AlreadyMember,
    TableUnavailable,
}

public sealed record JoinTableResult(JoinTableOutcome Outcome, Guid? SittingIdentifier)
{
    public bool MembershipInserted =>
        Outcome is JoinTableOutcome.SittingOpened or JoinTableOutcome.JoinedOpenSitting;

    public bool IsMember => MembershipInserted || Outcome is JoinTableOutcome.AlreadyMember;
}

public interface ISittingMembership
{
    Task<JoinTableResult> JoinTableAsync(
        Guid tableIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperSittingMembership : ISittingMembership
{
    private const string AdvisoryLockKeyPrefix = "myrestaurant_table_sitting:";

    private const string TableIsActiveSql = """
        SELECT EXISTS (
            SELECT 1
            FROM restaurant_table
            WHERE restaurant_table_identifier = @TableIdentifier
              AND is_active = true);
        """;

    private const string OpenSittingIdentifierSql = """
        SELECT table_sitting_identifier
        FROM table_sitting
        WHERE restaurant_table_identifier = @TableIdentifier
          AND closed_at IS NULL;
        """;

    private const string MembershipExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM table_sitting_member
            WHERE table_sitting_identifier = @SittingIdentifier
              AND person_identifier = @PersonIdentifier);
        """;

    private const string InsertSittingSql = """
        INSERT INTO table_sitting (
            table_sitting_identifier, restaurant_table_identifier, opened_at,
            closed_at, closed_by_person_identifier, settled_total_amount)
        VALUES (
            @SittingIdentifier, @TableIdentifier, @OpenedAt,
            NULL, NULL, NULL);
        """;

    private const string InsertMembershipSql = """
        INSERT INTO table_sitting_member (
            table_sitting_member_identifier, table_sitting_identifier, person_identifier, joined_at)
        VALUES (
            @MembershipIdentifier, @SittingIdentifier, @PersonIdentifier, @JoinedAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperSittingMembership(
        IDatabaseConnectionFactory connectionFactory,
        IClock clock,
        IIdentifierFactory identifierFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(identifierFactory);

        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;
    }

    public async Task<JoinTableResult> JoinTableAsync(
        Guid tableIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext(@Key));",
            new { Key = AdvisoryLockKeyPrefix + tableIdentifier.ToString("D") },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        bool tableIsActive = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            TableIsActiveSql,
            new { TableIdentifier = tableIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (!tableIsActive)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new JoinTableResult(JoinTableOutcome.TableUnavailable, null);
        }

        Guid? existingSitting = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            OpenSittingIdentifierSql,
            new { TableIdentifier = tableIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        Guid sittingIdentifier;
        JoinTableOutcome outcome;

        if (existingSitting is { } openSitting)
        {
            sittingIdentifier = openSitting;

            bool alreadyMember = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                MembershipExistsSql,
                new { SittingIdentifier = sittingIdentifier, PersonIdentifier = personIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (alreadyMember)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JoinTableResult(JoinTableOutcome.AlreadyMember, sittingIdentifier);
            }

            outcome = JoinTableOutcome.JoinedOpenSitting;
        }
        else
        {
            sittingIdentifier = _identifierFactory.Create();
            outcome = JoinTableOutcome.SittingOpened;

            await connection.ExecuteAsync(new CommandDefinition(
                InsertSittingSql,
                new
                {
                    SittingIdentifier = sittingIdentifier,
                    TableIdentifier = tableIdentifier,
                    OpenedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            InsertMembershipSql,
            new
            {
                MembershipIdentifier = _identifierFactory.Create(),
                SittingIdentifier = sittingIdentifier,
                PersonIdentifier = personIdentifier,
                JoinedAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JoinTableResult(outcome, sittingIdentifier);
    }
}
