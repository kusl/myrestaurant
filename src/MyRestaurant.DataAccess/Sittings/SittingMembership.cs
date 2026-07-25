using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Sittings;

/// <summary>What happened when a consumed join grant was turned into membership (§4.4, §5.1).</summary>
public enum JoinTableOutcome
{
    /// <summary>
    /// No sitting was open on the table, so one was created and this person became its first member —
    /// both in the same transaction (§5.1: "the first consumed grant … creates <c>table_sitting</c> and
    /// the first membership atomically").
    /// </summary>
    SittingOpened,

    /// <summary>A sitting was already open and this person was added to it (§5.1).</summary>
    JoinedOpenSitting,

    /// <summary>
    /// The person was already a member of the open sitting, so nothing was written — the
    /// <c>UNIQUE (table_sitting_identifier, person_identifier)</c> constraint makes a double join
    /// idempotent (§5.1), and re-scanning a code must not be an error.
    /// </summary>
    AlreadyMember,

    /// <summary>
    /// No <b>active</b> table has that identifier, so there is nothing to join: it never existed, or it
    /// has been deactivated (§4.1 — deactivating stops token validation and display rendering, and by
    /// the same rule stops new sittings and joins). Nothing was written.
    /// </summary>
    TableUnavailable,
}

/// <summary>
/// The result of a join attempt: what happened, and which sitting the person is now in.
/// </summary>
/// <param name="Outcome">Which of the four §4.4/§5.1 cases occurred.</param>
/// <param name="SittingIdentifier">
/// The sitting the person belongs to, or <c>null</c> for <see cref="JoinTableOutcome.TableUnavailable"/>.
/// </param>
public sealed record JoinTableResult(JoinTableOutcome Outcome, Guid? SittingIdentifier)
{
    /// <summary>
    /// True when a <c>table_sitting_member</c> row was actually inserted — the exact condition
    /// §9 attaches the <c>SittingMemberJoined</c> broadcast to ("fired on: membership insert"). An
    /// idempotent re-join inserts nothing and therefore broadcasts nothing.
    /// </summary>
    public bool MembershipInserted =>
        Outcome is JoinTableOutcome.SittingOpened or JoinTableOutcome.JoinedOpenSitting;

    /// <summary>True when the person is a member of <see cref="SittingIdentifier"/> after this call.</summary>
    public bool IsMember => MembershipInserted || Outcome is JoinTableOutcome.AlreadyMember;
}

/// <summary>
/// Turns a consumed join grant into sitting membership (TECHNICAL_SPECIFICATION §4.4, §5.1). This is
/// the single write path the join flow drives: the caller has already validated the token, issued the
/// grant, authenticated the person, and confirmed the grant matches this table — everything left is one
/// atomic database decision, which is what lives here.
///
/// <para>The atomicity §5.1 asks for is real: "open a sitting if none is open, then insert membership"
/// is a read-then-write race between two guests scanning the same table at the same instant. The
/// implementation serializes on a per-table transaction-scoped advisory lock (the same device
/// <c>/setup</c> uses for the zero-administrator gate, §3.6) and re-reads the open sitting under it, so
/// the loser of the race joins the winner's sitting instead of tripping the
/// <c>table_sitting_one_open_per_table</c> partial unique index.</para>
///
/// <para>Sittings are not part of the person-scoped <c>security_event</c> vocabulary (§8.2), so no audit
/// row is written here; the live-update broadcast is the web layer's job, after commit (§9).</para>
/// </summary>
public interface ISittingMembership
{
    /// <summary>
    /// Joins <paramref name="personIdentifier"/> to the open sitting on <paramref name="tableIdentifier"/>,
    /// opening that sitting first if none is open (§5.1). Idempotent: a person already in the sitting
    /// gets <see cref="JoinTableOutcome.AlreadyMember"/> and nothing is written. An unknown or
    /// deactivated table gets <see cref="JoinTableOutcome.TableUnavailable"/> (§4.1).
    /// </summary>
    Task<JoinTableResult> JoinTableAsync(
        Guid tableIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="ISittingMembership"/>. Like the other write services
/// (<c>DapperTableAdministration</c>, <c>DapperAccountAdministration</c>) it owns one connection and one
/// transaction per operation, stamps every row from a single <see cref="IClock.UtcNow"/> instant, mints
/// surrogate keys with the application <see cref="IIdentifierFactory"/> (UUIDv7, ADR-0011), and holds no
/// state.
/// </summary>
public sealed class DapperSittingMembership : ISittingMembership
{
    /// <summary>
    /// The per-table advisory-lock key prefix (§5.1). <c>hashtext</c> maps the composed text to the
    /// <c>integer</c> the transaction-scoped lock takes; the lock releases automatically on commit or
    /// rollback. Keyed per table, not globally, so two different tables never block each other.
    /// </summary>
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
        // One instant for the whole operation: a sitting opened by this join and the membership that
        // opened it carry the same timestamp, which is what "atomically" means to a reader (§5.1).
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // (1) Serialize concurrent joins on THIS table. Two guests scanning the same display in the
        // same second both find "no open sitting" without this; the second one blocks here and then
        // re-reads the sitting the first just opened.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext(@Key));",
            new { Key = AdvisoryLockKeyPrefix + tableIdentifier.ToString("D") },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // (2) The table must exist and be active (§4.1). Checked under the lock so a deactivation that
        // commits mid-flight cannot slip a new sitting in behind it.
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

        // (3) The open sitting, if any — authoritative because we hold the lock.
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
                // Nothing to write: re-scanning a code, or a double submit, is a no-op (§5.1).
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
