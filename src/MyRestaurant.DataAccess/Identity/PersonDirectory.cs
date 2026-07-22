using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// A read-only projection of a <c>person</c> row plus the person's granted roles, for the
/// administration people list (TECHNICAL_SPECIFICATION §3.6/§3.7). It is deliberately presentational:
/// it carries the raw <see cref="LockoutEndAt"/> rather than a computed "locked" flag, so the decision
/// is made against the application <see cref="MyRestaurant.Domain.Time.IClock"/> at render time; and it
/// never exposes the password hash or the protected TOTP secret — only whether each is set.
/// </summary>
/// <param name="PersonIdentifier">The person's UUIDv7 primary key (ADR-0011).</param>
/// <param name="Username">The unique <c>citext</c> username (§3.1).</param>
/// <param name="DisplayName">Optional human display name, or <c>null</c>.</param>
/// <param name="IsActive">False once an administrator has deactivated the account (F-10b).</param>
/// <param name="HasPassword">True when a password hash is set (a passkey-only account has none, §3.2).</param>
/// <param name="HasAuthenticator">True when a TOTP secret is enrolled (<c>totp_secret_protected IS NOT NULL</c>, §3.4).</param>
/// <param name="MustChangePassword">The §3.5 obligation (1) flag: a reset forces a change before any destination.</param>
/// <param name="MustEnrollTotp">The §3.5 obligation (2) flag: a reset that cleared TOTP forces re-enrollment.</param>
/// <param name="FailedAccessCount">Consecutive failed sign-ins; five triggers a lockout (§3.1).</param>
/// <param name="LockoutEndAt">When a lockout ends, or <c>null</c> when the account is not locked (§3.1).</param>
/// <param name="CreatedAt">Row creation timestamp (UTC).</param>
/// <param name="Roles">
/// The granted role names, in the fixed display order administrator, counter, kitchen. Empty for an
/// account that holds no staff role (e.g. a guest, §3.7).
/// </param>
public sealed record PersonSummary(
    Guid PersonIdentifier,
    string Username,
    string? DisplayName,
    bool IsActive,
    bool HasPassword,
    bool HasAuthenticator,
    bool MustChangePassword,
    bool MustEnrollTotp,
    int FailedAccessCount,
    DateTimeOffset? LockoutEndAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles);

/// <summary>
/// Reads people for the administration area (TECHNICAL_SPECIFICATION §3.6/§3.7). This is a read-only
/// reporting companion to the Identity stores: enumerating every account, or reading one for its
/// management page, is an administrative concern, not part of the sign-in write path, so it lives
/// behind its own interface (substitutable in tests) rather than being bolted onto <c>UserManager</c>.
/// </summary>
public interface IPersonDirectory
{
    /// <summary>
    /// Every person, oldest first (so the first administrator created at <c>/setup</c> leads the list),
    /// each with the person's granted role names.
    /// </summary>
    Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One person by identifier, with their granted role names, or <c>null</c> when no such person
    /// exists. Backs the administration person-management page (§3.7).
    /// </summary>
    Task<PersonSummary?> GetPersonAsync(Guid personIdentifier, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IPersonDirectory"/>. One connection, two round trips
/// (people, then their role rows), joined in memory — simpler and more portable than an
/// <c>array_agg</c> that would lean on Npgsql array mapping, and the row counts here are small (staff
/// plus guests, §11.1). Columns are aliased to the record's member names so Dapper maps them without
/// needing <c>MatchNamesWithUnderscores</c>. The connection comes from the singleton
/// <see cref="IDatabaseConnectionFactory"/>, matching the rest of the data layer.
/// </summary>
public sealed class DapperPersonDirectory : IPersonDirectory
{
    private const string PeopleColumns = """
        person_identifier                   AS PersonIdentifier,
        username                            AS Username,
        display_name                        AS DisplayName,
        is_active                           AS IsActive,
        (password_hash IS NOT NULL)         AS HasPassword,
        (totp_secret_protected IS NOT NULL) AS HasAuthenticator,
        must_change_password                AS MustChangePassword,
        must_enroll_totp                    AS MustEnrollTotp,
        failed_access_count                 AS FailedAccessCount,
        lockout_end_at                      AS LockoutEndAt,
        created_at                          AS CreatedAt
        """;

    // Built from PeopleColumns at type-init (static readonly, not const) so the shared column list is
    // interpolated once without relying on constant-interpolated-string support.
    private static readonly string PeopleSql = $"""
        SELECT {PeopleColumns}
        FROM person
        ORDER BY created_at, username;
        """;

    private static readonly string PersonByIdSql = $"""
        SELECT {PeopleColumns}
        FROM person
        WHERE person_identifier = @PersonIdentifier;
        """;

    private const string RolesSql = """
        SELECT
            person_identifier AS PersonIdentifier,
            role_name         AS RoleName
        FROM person_role;
        """;

    private const string RolesByPersonSql = """
        SELECT
            person_identifier AS PersonIdentifier,
            role_name         AS RoleName
        FROM person_role
        WHERE person_identifier = @PersonIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperPersonDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<PersonRow> personRows = await connection.QueryAsync<PersonRow>(new CommandDefinition(
            PeopleSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        IEnumerable<RoleRow> roleRows = await connection.QueryAsync<RoleRow>(new CommandDefinition(
            RolesSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Group roles by person so each summary carries a stable, order-independent role list.
        ILookup<Guid, string> rolesByPerson =
            roleRows.ToLookup(row => row.PersonIdentifier, row => row.RoleName);

        List<PersonSummary> people = [];
        foreach (PersonRow row in personRows)
        {
            people.Add(ToSummary(row, rolesByPerson[row.PersonIdentifier]));
        }

        return people;
    }

    public async Task<PersonSummary?> GetPersonAsync(Guid personIdentifier, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        PersonRow? row = await connection.QuerySingleOrDefaultAsync<PersonRow>(new CommandDefinition(
            PersonByIdSql,
            new { PersonIdentifier = personIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        IEnumerable<RoleRow> roleRows = await connection.QueryAsync<RoleRow>(new CommandDefinition(
            RolesByPersonSql,
            new { PersonIdentifier = personIdentifier },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return ToSummary(row, roleRows.Select(role => role.RoleName));
    }

    private static PersonSummary ToSummary(PersonRow row, IEnumerable<string> roleNames)
    {
        IReadOnlyList<string> roles = roleNames.OrderBy(RoleSortKey).ToArray();

        return new PersonSummary(
            row.PersonIdentifier,
            row.Username,
            row.DisplayName,
            row.IsActive,
            row.HasPassword,
            row.HasAuthenticator,
            row.MustChangePassword,
            row.MustEnrollTotp,
            row.FailedAccessCount,
            row.LockoutEndAt,
            row.CreatedAt,
            roles);
    }

    // administrator first, then counter, then kitchen; anything unexpected sorts last.
    private static int RoleSortKey(string role) => role switch
    {
        "administrator" => 0,
        "counter" => 1,
        "kitchen" => 2,
        _ => 3,
    };

    // Dapper maps these positional records by constructor-parameter name (case-insensitive) against
    // the aliased columns above.
    private sealed record PersonRow(
        Guid PersonIdentifier,
        string Username,
        string? DisplayName,
        bool IsActive,
        bool HasPassword,
        bool HasAuthenticator,
        bool MustChangePassword,
        bool MustEnrollTotp,
        int FailedAccessCount,
        DateTimeOffset? LockoutEndAt,
        DateTimeOffset CreatedAt);

    private sealed record RoleRow(Guid PersonIdentifier, string RoleName);
}
