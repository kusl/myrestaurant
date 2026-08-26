using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Identity;

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

public interface IPersonDirectory
{
    Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default);

    Task<PersonSummary?> GetPersonAsync(Guid personIdentifier, CancellationToken cancellationToken = default);
}

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
            row.LockoutEndAt is { } lockoutEnd
                ? new DateTimeOffset(DateTime.SpecifyKind(lockoutEnd, DateTimeKind.Utc))
                : null,
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            roles);
    }

    private static int RoleSortKey(string role) => role switch
    {
        "administrator" => 0,
        "counter" => 1,
        "kitchen" => 2,
        _ => 3,
    };

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
        DateTime? LockoutEndAt,
        DateTime CreatedAt);

    private sealed record RoleRow(Guid PersonIdentifier, string RoleName);
}
