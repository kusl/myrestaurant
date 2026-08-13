using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>The outcome of <see cref="IMenuSectionAdministration.CreateMenuSectionAsync"/> (§7).</summary>
public enum CreateMenuSectionOutcome
{
    /// <summary>The section row was written with a <c>created</c> event carrying all three payloads.</summary>
    Created,

    /// <summary>
    /// Some section already uses that name (the <c>menu_section.name</c> UNIQUE constraint tripped);
    /// nothing was written. The column is <c>citext</c>, so "drinks" collides with "Drinks".
    /// </summary>
    NameTaken,
}

/// <summary>The outcome of <see cref="IMenuSectionAdministration.RenameMenuSectionAsync"/> (§7).</summary>
public enum RenameMenuSectionOutcome
{
    /// <summary>The name changed and a <c>renamed</c> event was written.</summary>
    Renamed,

    /// <summary>The new name equalled the current one, character for character; nothing was written.</summary>
    NoChange,

    /// <summary>Another section already uses the requested name; nothing was written.</summary>
    NameTaken,

    /// <summary>No section has that identifier; nothing was written.</summary>
    MenuSectionNotFound,
}

/// <summary>The outcome of <see cref="IMenuSectionAdministration.DescribeMenuSectionAsync"/> (§7).</summary>
public enum DescribeMenuSectionOutcome
{
    /// <summary>The description changed and a <c>described</c> event was written.</summary>
    Described,

    /// <summary>The new description equalled the current one; nothing was written.</summary>
    NoChange,

    /// <summary>No section has that identifier; nothing was written.</summary>
    MenuSectionNotFound,
}

/// <summary>The outcome of <see cref="IMenuSectionAdministration.ReorderMenuSectionAsync"/> (§7).</summary>
public enum ReorderMenuSectionOutcome
{
    /// <summary>The position changed and a <c>reordered</c> event was written.</summary>
    Reordered,

    /// <summary>The section was already at that position; nothing was written.</summary>
    NoChange,

    /// <summary>No section has that identifier; nothing was written.</summary>
    MenuSectionNotFound,
}

/// <summary>The outcome of <see cref="IMenuSectionAdministration.SetMenuSectionActiveAsync"/> (§7).</summary>
public enum MenuSectionActivationOutcome
{
    /// <summary>The active state changed and an <c>activated</c> or <c>deactivated</c> event was written.</summary>
    Changed,

    /// <summary>The section was already in the requested state; nothing was written.</summary>
    NoChange,

    /// <summary>No section has that identifier; nothing was written.</summary>
    MenuSectionNotFound,
}

/// <summary>
/// A created menu section, as stored (§7). Every member is what the row and its <c>created</c> event
/// actually carry, which is not necessarily what the caller passed: the name and description are trimmed,
/// and <see cref="DisplayOrder"/> is <em>assigned</em> rather than supplied, so a surface can echo this
/// back without a second read.
/// </summary>
/// <param name="Outcome">Whether the section was written, or the name was already taken.</param>
/// <param name="MenuSectionIdentifier">The identifier the caller minted (ADR-0011).</param>
/// <param name="Name">The stored name; <c>null</c> when the name was taken.</param>
/// <param name="Description">The stored description, <c>""</c> for none; <c>null</c> when the name was taken.</param>
/// <param name="DisplayOrder">The position the section was appended at; <c>null</c> when the name was taken.</param>
public sealed record CreateMenuSectionResult(
    CreateMenuSectionOutcome Outcome,
    Guid MenuSectionIdentifier,
    string? Name,
    string? Description,
    int? DisplayOrder)
{
    /// <summary>True only when a row was written — the precondition for publishing <c>MenuChanged</c> (§9).</summary>
    public bool Created => Outcome is CreateMenuSectionOutcome.Created;
}

/// <summary>
/// Menu section administration (TECHNICAL_SPECIFICATION §7, §11.4) — creating a heading, renaming it,
/// describing it, moving it, and switching it off, each writing the <c>menu_section</c> row and its
/// mirroring <c>menu_section_event</c> in one transaction.
///
/// <para><b>Why this is a second interface rather than five more methods on
/// <see cref="IMenuAdministration"/>.</b> Two tables, two event logs, and — once Stage 3 lands — two
/// audiences: §11.4 gives sections to the administrator alone, while §11.2 already puts the item-level 86
/// toggle on the kitchen board. Splitting on the table means neither interface grows a method its
/// implementation has to refuse.</para>
///
/// <para><b>Every verb is its own call, and that is §8.2 talking.</b> The event vocabulary has
/// <c>renamed</c>, <c>described</c> and <c>reordered</c> as distinct types with mutually exclusive payload
/// columns, enforced by three named paired CHECKs. A combined "save" would have to write up to three
/// events anyway and then decide what to do when two of the three are no-ops.</para>
///
/// <para><b>The no-op rule.</b> Every verb here compares before it writes and writes nothing when the
/// value has not moved, on the same terms <see cref="IMenuAdministration"/> uses: an append-only log of
/// "somebody pressed Save" is noise, and §11.4's per-section history is meant to be read by a person.</para>
///
/// <para><b>Names are compared ordinally even though the column is <c>citext</c>, and the distinction is
/// the ruling.</b> citext governs <em>collisions between sections</em> — a second "Drinks" spelled
/// "drinks" is the mis-tap the constraint exists to refuse. It does not govern whether one section's own
/// spelling moved: renaming "drinks" to "Drinks" is a capitalisation fix somebody meant to make, it
/// changes what every guest reads, and calling it a no-op because the database considers the two equal
/// would be this layer deciding that a visible change did not happen.</para>
/// </summary>
public interface IMenuSectionAdministration
{
    /// <summary>
    /// Creates a section, active, at the end of the current order, and writes the matching
    /// <c>created</c> event (which carries the name, the description and the assigned position — §8.2's
    /// three CHECKs require all three for that type). The identifier is minted by the caller (ADR-0011)
    /// so a surface can link straight to the new section.
    ///
    /// <para>The position is <c>MAX(display_order) + 1</c>, read inside the same transaction, so the
    /// first section is 0 and a new one lands at the bottom where somebody adding a heading expects it.
    /// Moving it is <see cref="ReorderMenuSectionAsync"/>'s job.</para>
    /// </summary>
    /// <param name="menuSectionIdentifier">A caller-minted UUIDv7 (ADR-0011).</param>
    /// <param name="name">The heading, 1 to 80 characters after trimming.</param>
    /// <param name="description">Optional prose under the heading; <c>null</c> or blank stores <c>""</c>.</param>
    /// <param name="actorPersonIdentifier">The administrator the event records.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="name"/> exceeds the column's 80 characters.</exception>
    Task<CreateMenuSectionResult> CreateMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames one section and appends a <c>renamed</c> event carrying the new name.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="name"/> exceeds the column's 80 characters.</exception>
    Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears one section's description and appends a <c>described</c> event carrying the new
    /// value. Clearing writes <c>""</c> and is an ordinary change with an ordinary event, not a deletion:
    /// the column is <c>NOT NULL</c> precisely so that the paired CHECK can stay an equality.
    /// </summary>
    Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
        Guid menuSectionIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one section to an absolute position and appends a <c>reordered</c> event carrying it.
    /// Positions are not unique: two sections may share one, and the reads break the tie by name. That is
    /// deliberate (§8.2) — a unique ordering column would make every move a two-phase rewrite of a table
    /// with eight rows in it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayOrder"/> is negative.</exception>
    Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
        Guid menuSectionIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches a whole heading on or off, appending <c>activated</c> or <c>deactivated</c> — the event
    /// type carries the fact, so neither of them has a payload column at all.
    /// </summary>
    Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
        Guid menuSectionIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuSectionAdministration"/>. One connection and one
/// transaction per operation, one <see cref="IClock.UtcNow"/> instant on both rows of each write, and a
/// UUIDv7 event key from <see cref="IIdentifierFactory"/> (ADR-0011) — the shape every write in this
/// layer has, and the shape <see cref="DapperMenuAdministration"/> established for the sibling table.
///
/// <para>The row is taken <c>FOR UPDATE</c> before it is compared, for the reason
/// <see cref="DapperMenuAdministration"/> takes it: without the lock, two administrators renaming the same
/// section at once could both read the old name, both write the new one, and log two <c>renamed</c> events
/// for one change. The name would still be right and the history would be a lie, which is the worse of the
/// two failures in an append-only system (ADR-0002).</para>
///
/// <para><b>Two concurrent creates may be assigned the same position, and that is not a defect.</b>
/// <c>MAX(display_order) + 1</c> is read under the transaction but the table is not locked, so two
/// administrators adding a heading at the same moment can both be appended at the same number. The column
/// is deliberately not UNIQUE, so nothing fails; the reads break the tie by name, so both headings render
/// in a stable order; and either administrator can move one. Locking the table to prevent a tie nobody
/// can see would be the more expensive answer to the smaller problem.</para>
/// </summary>
public sealed class DapperMenuSectionAdministration : IMenuSectionAdministration
{
    /// <summary>Stored spellings of <c>menu_section_event.event_type</c> (§8.2's CHECK).</summary>
    private const string CreatedEventType = "created";

    private const string RenamedEventType = "renamed";

    private const string DescribedEventType = "described";

    private const string ReorderedEventType = "reordered";

    private const string ActivatedEventType = "activated";

    private const string DeactivatedEventType = "deactivated";

    /// <summary>
    /// The column is <c>citext</c> with a <c>char_length BETWEEN 1 AND 80</c> CHECK. A longer name is
    /// PostgreSQL error 23514 at INSERT time, which would surface as an opaque exception well after the
    /// form that caused it; refusing it here names the problem, the same way
    /// <see cref="DapperMenuAdministration"/> refuses a price that will not fit <c>numeric(10,2)</c>.
    ///
    /// <para>The comparison is against <see cref="string.Length"/>, which counts UTF-16 code units, while
    /// <c>char_length</c> counts characters. They differ only above the basic multilingual plane, and only
    /// in the safe direction: a name of forty-one emoji is refused here and would have been accepted
    /// there. A section heading is not the place to litigate that.</para>
    /// </summary>
    private const int NameMaximumLength = 80;

    /// <summary>
    /// Appends at the end. <c>COALESCE(MAX(…), -1) + 1</c> rather than <c>COUNT(*)</c>: positions are not
    /// unique and are not required to be contiguous, so counting rows would hand out a number some other
    /// section already sits on as soon as one has been moved.
    /// </summary>
    private const string NextDisplayOrderSql = """
        SELECT COALESCE(MAX(menu_section.display_order), -1) + 1
        FROM menu_section;
        """;

    private const string InsertMenuSectionSql = """
        INSERT INTO menu_section (
            menu_section_identifier, name, description, display_order, is_active, created_at)
        VALUES (
            @MenuSectionIdentifier, @Name, @Description, @DisplayOrder, true, @CreatedAt);
        """;

    private const string LockMenuSectionSql = """
        SELECT menu_section.name          AS Name,
               menu_section.description   AS Description,
               menu_section.display_order AS DisplayOrder,
               menu_section.is_active     AS IsActive
        FROM menu_section
        WHERE menu_section.menu_section_identifier = @MenuSectionIdentifier
        FOR UPDATE;
        """;

    private const string UpdateNameSql = """
        UPDATE menu_section
        SET name = @Name
        WHERE menu_section_identifier = @MenuSectionIdentifier;
        """;

    private const string UpdateDescriptionSql = """
        UPDATE menu_section
        SET description = @Description
        WHERE menu_section_identifier = @MenuSectionIdentifier;
        """;

    private const string UpdateDisplayOrderSql = """
        UPDATE menu_section
        SET display_order = @DisplayOrder
        WHERE menu_section_identifier = @MenuSectionIdentifier;
        """;

    private const string UpdateIsActiveSql = """
        UPDATE menu_section
        SET is_active = @IsActive
        WHERE menu_section_identifier = @MenuSectionIdentifier;
        """;

    /// <summary>
    /// One INSERT for all six event types. §8.2's three named paired CHECKs tie each nullable payload
    /// column to exactly the types that carry it — <c>created</c> needs all three, <c>renamed</c> the name
    /// alone, <c>described</c> the description alone, <c>reordered</c> the position alone, and the two
    /// activation types none — so the callers below pass NULL for whichever the type must not have, and
    /// the database refuses any combination this file gets wrong.
    /// </summary>
    private const string InsertMenuSectionEventSql = """
        INSERT INTO menu_section_event (
            menu_section_event_identifier, menu_section_identifier, actor_person_identifier,
            event_type, new_name, new_description, new_display_order, occurred_at)
        VALUES (
            @MenuSectionEventIdentifier, @MenuSectionIdentifier, @ActorPersonIdentifier,
            @EventType, @NewName, @NewDescription, @NewDisplayOrder, @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperMenuSectionAdministration(
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

    public async Task<CreateMenuSectionResult> CreateMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        string normalizedDescription = NormalizeDescription(description);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int displayOrder = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            NextDisplayOrderSql,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertMenuSectionSql,
                new
                {
                    MenuSectionIdentifier = menuSectionIdentifier,
                    Name = normalizedName,
                    Description = normalizedDescription,
                    DisplayOrder = displayOrder,
                    CreatedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CreateMenuSectionResult(
                CreateMenuSectionOutcome.NameTaken,
                menuSectionIdentifier,
                Name: null,
                Description: null,
                DisplayOrder: null);
        }

        await InsertEventAsync(
            connection,
            transaction,
            menuSectionIdentifier,
            actorPersonIdentifier,
            CreatedEventType,
            normalizedName,
            normalizedDescription,
            displayOrder,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CreateMenuSectionResult(
            CreateMenuSectionOutcome.Created,
            menuSectionIdentifier,
            normalizedName,
            normalizedDescription,
            displayOrder);
    }

    public async Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuSectionLockRow? section = await ReadLockedAsync(
            connection, transaction, menuSectionIdentifier, cancellationToken).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RenameMenuSectionOutcome.MenuSectionNotFound;
        }

        // Ordinal, though the column is citext — see the interface's ruling: citext refuses a collision
        // between two sections, and a capitalisation fix on one section is a change the guest can read.
        if (string.Equals(section.Name, normalizedName, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RenameMenuSectionOutcome.NoChange;
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpdateNameSql,
                new { MenuSectionIdentifier = menuSectionIdentifier, Name = normalizedName },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RenameMenuSectionOutcome.NameTaken;
        }

        await InsertEventAsync(
            connection,
            transaction,
            menuSectionIdentifier,
            actorPersonIdentifier,
            RenamedEventType,
            normalizedName,
            newDescription: null,
            newDisplayOrder: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return RenameMenuSectionOutcome.Renamed;
    }

    public async Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
        Guid menuSectionIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string normalizedDescription = NormalizeDescription(description);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuSectionLockRow? section = await ReadLockedAsync(
            connection, transaction, menuSectionIdentifier, cancellationToken).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return DescribeMenuSectionOutcome.MenuSectionNotFound;
        }

        if (string.Equals(section.Description, normalizedDescription, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return DescribeMenuSectionOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateDescriptionSql,
            new { MenuSectionIdentifier = menuSectionIdentifier, Description = normalizedDescription },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuSectionIdentifier,
            actorPersonIdentifier,
            DescribedEventType,
            newName: null,
            normalizedDescription,
            newDisplayOrder: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return DescribeMenuSectionOutcome.Described;
    }

    public async Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
        Guid menuSectionIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(displayOrder);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuSectionLockRow? section = await ReadLockedAsync(
            connection, transaction, menuSectionIdentifier, cancellationToken).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ReorderMenuSectionOutcome.MenuSectionNotFound;
        }

        if (section.DisplayOrder == displayOrder)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ReorderMenuSectionOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateDisplayOrderSql,
            new { MenuSectionIdentifier = menuSectionIdentifier, DisplayOrder = displayOrder },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuSectionIdentifier,
            actorPersonIdentifier,
            ReorderedEventType,
            newName: null,
            newDescription: null,
            displayOrder,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ReorderMenuSectionOutcome.Reordered;
    }

    public async Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
        Guid menuSectionIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuSectionLockRow? section = await ReadLockedAsync(
            connection, transaction, menuSectionIdentifier, cancellationToken).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MenuSectionActivationOutcome.MenuSectionNotFound;
        }

        if (section.IsActive == isActive)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MenuSectionActivationOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateIsActiveSql,
            new { MenuSectionIdentifier = menuSectionIdentifier, IsActive = isActive },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuSectionIdentifier,
            actorPersonIdentifier,
            isActive ? ActivatedEventType : DeactivatedEventType,
            newName: null,
            newDescription: null,
            newDisplayOrder: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return MenuSectionActivationOutcome.Changed;
    }

    private async Task InsertEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newName,
        string? newDescription,
        int? newDisplayOrder,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            InsertMenuSectionEventSql,
            new
            {
                MenuSectionEventIdentifier = _identifierFactory.Create(),
                MenuSectionIdentifier = menuSectionIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                NewName = newName,
                NewDescription = newDescription,
                NewDisplayOrder = newDisplayOrder,
                OccurredAt = occurredAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task<MenuSectionLockRow?> ReadLockedAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<MenuSectionLockRow>(new CommandDefinition(
            LockMenuSectionSql,
            new { MenuSectionIdentifier = menuSectionIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, NameMaximumLength, nameof(name));

        return trimmed;
    }

    /// <summary>
    /// Trims, and turns null or blank into <c>""</c> — the stored spelling of "no description" (§8.2).
    /// There is no length ceiling here because the column has none: inventing one would be this layer
    /// overruling the schema of record, which is the same reason nothing here rejects a duplicate item
    /// name. What the form offers is a Stage 3 question.
    /// </summary>
    private static string NormalizeDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();

    // Dapper maps this positional record by constructor-parameter name against the aliased columns above.
    private sealed record MenuSectionLockRow(
        string Name,
        string Description,
        int DisplayOrder,
        bool IsActive);
}
