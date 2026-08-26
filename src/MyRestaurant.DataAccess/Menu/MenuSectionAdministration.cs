using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Menu;

public enum CreateMenuSectionOutcome
{
    Created,
    NameTaken,
}

public enum RenameMenuSectionOutcome
{
    Renamed,
    NoChange,
    NameTaken,
    MenuSectionNotFound,
}

public enum DescribeMenuSectionOutcome
{
    Described,
    NoChange,
    MenuSectionNotFound,
}

public enum ReorderMenuSectionOutcome
{
    Reordered,
    NoChange,
    MenuSectionNotFound,
}

public enum ResequenceMenuSectionsOutcome
{
    Resequenced,
    NoChange,
    MenuSectionSetChanged,
}

public enum MenuSectionActivationOutcome
{
    Changed,
    NoChange,
    MenuSectionNotFound,
}

public sealed record CreateMenuSectionResult(
    CreateMenuSectionOutcome Outcome,
    Guid MenuSectionIdentifier,
    string? Name,
    string? Description,
    int? DisplayOrder)
{
    public bool Created => Outcome is CreateMenuSectionOutcome.Created;
}

public interface IMenuSectionAdministration
{
    Task<CreateMenuSectionResult> CreateMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
        Guid menuSectionIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
        Guid menuSectionIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ResequenceMenuSectionsOutcome> ResequenceMenuSectionsAsync(
        IReadOnlyList<Guid> orderedMenuSectionIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
        Guid menuSectionIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuSectionAdministration : IMenuSectionAdministration
{
    private const string CreatedEventType = "created";

    private const string RenamedEventType = "renamed";

    private const string DescribedEventType = "described";

    private const string ReorderedEventType = "reordered";

    private const string ActivatedEventType = "activated";

    private const string DeactivatedEventType = "deactivated";

    private const int NameMaximumLength = 80;

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

    private const string LockAllMenuSectionsSql = """
        SELECT menu_section.menu_section_identifier AS MenuSectionIdentifier,
               menu_section.display_order           AS DisplayOrder
        FROM menu_section
        ORDER BY menu_section.menu_section_identifier
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

    public async Task<ResequenceMenuSectionsOutcome> ResequenceMenuSectionsAsync(
        IReadOnlyList<Guid> orderedMenuSectionIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedMenuSectionIdentifiers);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuSectionPositionRow> locked = await connection
            .QueryAsync<MenuSectionPositionRow>(new CommandDefinition(
                LockAllMenuSectionsSql,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        Dictionary<Guid, int> storedPositions = locked
            .ToDictionary(row => row.MenuSectionIdentifier, row => row.DisplayOrder);

        if (!IsPermutationOf(orderedMenuSectionIdentifiers, storedPositions))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ResequenceMenuSectionsOutcome.MenuSectionSetChanged;
        }

        int moved = 0;

        for (int position = 0; position < orderedMenuSectionIdentifiers.Count; position++)
        {
            Guid menuSectionIdentifier = orderedMenuSectionIdentifiers[position];

            if (storedPositions[menuSectionIdentifier] == position)
            {
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                UpdateDisplayOrderSql,
                new { MenuSectionIdentifier = menuSectionIdentifier, DisplayOrder = position },
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
                position,
                now,
                cancellationToken).ConfigureAwait(false);

            moved++;
        }

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ResequenceMenuSectionsOutcome.NoChange;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ResequenceMenuSectionsOutcome.Resequenced;
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

    private static string NormalizeDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();

    private static bool IsPermutationOf(
        IReadOnlyList<Guid> requested,
        Dictionary<Guid, int> storedPositions)
    {
        if (requested.Count != storedPositions.Count)
        {
            return false;
        }

        HashSet<Guid> distinct = [.. requested];

        return distinct.Count == requested.Count && distinct.All(storedPositions.ContainsKey);
    }

    private sealed record MenuSectionLockRow(
        string Name,
        string Description,
        int DisplayOrder,
        bool IsActive);

    private sealed record MenuSectionPositionRow(
        Guid MenuSectionIdentifier,
        int DisplayOrder);
}
