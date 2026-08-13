using System.Data.Common;
using Dapper;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>
/// One menu section as every reading surface sees it (TECHNICAL_SPECIFICATION §7, §11.1, §11.4).
///
/// <para><see cref="IsActive"/> is carried rather than filtered on, for the same reason
/// <see cref="MenuItemSummary.IsActive"/> is: §7 does not hide a deactivated thing from the guest, it
/// marks it. A section nobody can order from is a heading with every item under it unavailable, which is
/// a fact the guest is better off seeing than guessing at from a menu that shrank.</para>
///
/// <para><see cref="Description"/> is never <c>null</c>. The column is <c>NOT NULL DEFAULT ''</c> and
/// <c>''</c> means "none", so a surface tests <see cref="string.Length"/> rather than for null — the
/// reason is the paired CHECK on <c>menu_section_event</c>, which could not tie an optional payload to
/// its event type if clearing a description wrote NULL.</para>
/// </summary>
/// <param name="MenuSectionIdentifier">The section's UUIDv7 primary key (ADR-0011).</param>
/// <param name="Name">The section's current name (§7 — renames are logged in <c>menu_section_event</c>).</param>
/// <param name="Description">The section's current description; <c>""</c> when it has none.</param>
/// <param name="DisplayOrder">Where the section sits; ties are broken by name, then identifier.</param>
/// <param name="IsActive">False when the whole heading is switched off — visible, unorderable (§7).</param>
/// <param name="CreatedAt">When the section was first created.</param>
public sealed record MenuSectionSummary(
    Guid MenuSectionIdentifier,
    string Name,
    string Description,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// Reads the menu's sections (TECHNICAL_SPECIFICATION §7). The read side only; every write —
/// create, rename, describe, reorder, activate, deactivate, each of which also appends a
/// <c>menu_section_event</c> row — is <see cref="IMenuSectionAdministration"/>, exactly the way
/// <see cref="IMenuDirectory"/> stands beside <see cref="IMenuAdministration"/>.
///
/// <para><b>Order is a stored decision, not an alphabet.</b> Both reads order by
/// <c>(display_order, name, menu_section_identifier)</c>, which is the order §11.4's index and §11.1's
/// guest menu both render. Alphabetical would be wrong on a real menu — "Drinks" before "Entrees" is not
/// a decision anybody made, and a restaurant that wants breakfast at the top says so with
/// <c>display_order</c>. The two tiebreakers are there so that equal orders, which the schema permits
/// deliberately, still render the same way on every request rather than in whatever sequence the scan
/// returned.</para>
/// </summary>
public interface IMenuSectionDirectory
{
    /// <summary>
    /// Every section, active and inactive, in display order (§7, §11.1, §11.4). An empty list is the
    /// correct answer on a fresh installation: §7 seeds no sections, and the administrator names their
    /// own.
    /// </summary>
    Task<IReadOnlyList<MenuSectionSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One section by identifier, or <c>null</c> when no section has that identifier.</summary>
    Task<MenuSectionSummary?> GetAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuSectionDirectory"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), columns
/// aliased to the record's member names, and rows read into an internal row type with a
/// <see cref="DateTime"/> member before being projected — Npgsql materialises <c>timestamptz</c> as
/// <see cref="DateTime"/> and Dapper's constructor binding will not feed one into a
/// <see cref="DateTimeOffset"/> parameter (the same fix <see cref="DapperMenuDirectory"/> carries).
///
/// <para><c>name</c> is <c>citext</c> in the schema and <see cref="string"/> here. Npgsql reads citext as
/// text, so nothing special is needed on this side; what the type buys is on the write side, where the
/// UNIQUE constraint refuses a second "Drinks" spelled any way at all.</para>
/// </summary>
public sealed class DapperMenuSectionDirectory : IMenuSectionDirectory
{
    private const string MenuSectionColumns = """
        menu_section.menu_section_identifier AS MenuSectionIdentifier,
        menu_section.name                    AS Name,
        menu_section.description             AS Description,
        menu_section.display_order           AS DisplayOrder,
        menu_section.is_active               AS IsActive,
        menu_section.created_at              AS CreatedAt
        """;

    private static readonly string ListSql = $"""
        SELECT {MenuSectionColumns}
        FROM menu_section
        ORDER BY menu_section.display_order, menu_section.name, menu_section.menu_section_identifier;
        """;

    private static readonly string GetSql = $"""
        SELECT {MenuSectionColumns}
        FROM menu_section
        WHERE menu_section.menu_section_identifier = @MenuSectionIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuSectionDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuSectionSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuSectionRow> rows = await connection.QueryAsync<MenuSectionRow>(
            new CommandDefinition(
                ListSql,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<MenuSectionSummary?> GetAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        MenuSectionRow? row = await connection.QuerySingleOrDefaultAsync<MenuSectionRow>(
            new CommandDefinition(
                GetSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : ToSummary(row);
    }

    private static MenuSectionSummary ToSummary(MenuSectionRow row) => new(
        row.MenuSectionIdentifier,
        row.Name,
        row.Description,
        row.DisplayOrder,
        row.IsActive,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));

    private sealed record MenuSectionRow(
        Guid MenuSectionIdentifier,
        string Name,
        string Description,
        int DisplayOrder,
        bool IsActive,
        DateTime CreatedAt);
}
