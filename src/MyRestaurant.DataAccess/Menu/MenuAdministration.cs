using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Menu;

public enum CreateMenuItemOutcome
{
    Created,
    MenuSectionNotFound,
}

public enum RenameMenuItemOutcome
{
    Renamed,
    NoChange,
    MenuItemNotFound,
}

public enum RepriceMenuItemOutcome
{
    Repriced,
    NoChange,
    MenuItemNotFound,
}

public enum DescribeMenuItemOutcome
{
    Described,
    NoChange,
    MenuItemNotFound,
}

public enum MoveMenuItemToSectionOutcome
{
    Moved,
    NoChange,
    MenuItemNotFound,
    MenuSectionNotFound,
}

public enum ReorderMenuItemOutcome
{
    Reordered,
    NoChange,
    MenuItemNotFound,
}

public enum ResequenceMenuItemsOutcome
{
    Resequenced,
    NoChange,
    MenuItemSetChanged,
}

public sealed record CreateMenuItemResult(
    CreateMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    Guid MenuSectionIdentifier,
    string? MenuSectionName,
    string? Name,
    string? Description,
    decimal? PriceAmount,
    int? DisplayOrder)
{
    public bool Created => Outcome is CreateMenuItemOutcome.Created;

    public bool DescriptionWasSet => Description is { Length: > 0 };
}

public sealed record RenameMenuItemResult(
    RenameMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    string? PreviousName)
{
    public bool Changed => Outcome is RenameMenuItemOutcome.Renamed;

    public bool ItemExists => Outcome is not RenameMenuItemOutcome.MenuItemNotFound;
}

public sealed record RepriceMenuItemResult(
    RepriceMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    decimal? PriceAmount,
    decimal? PreviousPriceAmount)
{
    public bool Changed => Outcome is RepriceMenuItemOutcome.Repriced;

    public bool ItemExists => Outcome is not RepriceMenuItemOutcome.MenuItemNotFound;
}

public interface IMenuAdministration
{
    Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuAdministration : IMenuAdministration
{
    private const string CreatedEventType = "created";

    private const string NameChangedEventType = "name_changed";

    private const string PriceChangedEventType = "price_changed";

    private const string DescriptionChangedEventType = "description_changed";

    private const string SectionChangedEventType = "section_changed";

    private const string ReorderedEventType = "reordered";

    private const decimal PriceExclusiveUpperBound = 100_000_000m;

    private const string InsertMenuItemSql = """
        INSERT INTO menu_item (
            menu_item_identifier, menu_section_identifier, name, description,
            price_amount, display_order, is_active, created_at)
        VALUES (
            @MenuItemIdentifier, @MenuSectionIdentifier, @Name, @Description,
            @PriceAmount, @DisplayOrder, true, @CreatedAt);
        """;

    private const string LockMenuSectionAndReadNextPositionSql = """
        SELECT menu_section.name AS Name,
               COALESCE(
                   (SELECT MAX(menu_item.display_order) + 1
                    FROM menu_item
                    WHERE menu_item.menu_section_identifier = menu_section.menu_section_identifier),
                   0) AS NextDisplayOrder
        FROM menu_section
        WHERE menu_section.menu_section_identifier = @MenuSectionIdentifier
        FOR UPDATE;
        """;

    private const string LockMenuItemSql = """
        SELECT menu_item.name                   AS Name,
               menu_item.description            AS Description,
               menu_item.price_amount           AS PriceAmount,
               menu_item.display_order          AS DisplayOrder,
               menu_item.menu_section_identifier AS MenuSectionIdentifier
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    private const string UpdateNameSql = """
        UPDATE menu_item
        SET name = @Name
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string UpdatePriceSql = """
        UPDATE menu_item
        SET price_amount = @PriceAmount
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string UpdateDescriptionSql = """
        UPDATE menu_item
        SET description = @Description
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string UpdateDisplayOrderSql = """
        UPDATE menu_item
        SET display_order = @DisplayOrder
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string LockMenuItemsInSectionSql = """
        SELECT menu_item.menu_item_identifier AS MenuItemIdentifier,
               menu_item.display_order        AS DisplayOrder
        FROM menu_item
        WHERE menu_item.menu_section_identifier = @MenuSectionIdentifier
        ORDER BY menu_item.menu_item_identifier
        FOR UPDATE;
        """;

    private const string UpdateMenuSectionAndPositionSql = """
        UPDATE menu_item
        SET menu_section_identifier = @MenuSectionIdentifier,
            display_order = @DisplayOrder
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string InsertMenuItemEventSql = """
        INSERT INTO menu_item_event (
            menu_item_event_identifier, menu_item_identifier, actor_person_identifier,
            event_type, new_name, new_price_amount, new_description, new_display_order,
            new_menu_section_identifier, occurred_at)
        VALUES (
            @MenuItemEventIdentifier, @MenuItemIdentifier, @ActorPersonIdentifier,
            @EventType, @NewName, @NewPriceAmount, @NewDescription, @NewDisplayOrder,
            @NewMenuSectionIdentifier, @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperMenuAdministration(
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

    public async Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        string normalizedDescription = NormalizeDescription(description);
        decimal normalizedPrice = NormalizePrice(priceAmount);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuSectionPositionRow? section = await connection
            .QuerySingleOrDefaultAsync<MenuSectionPositionRow>(new CommandDefinition(
                LockMenuSectionAndReadNextPositionSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CreateMenuItemResult(
                CreateMenuItemOutcome.MenuSectionNotFound,
                menuItemIdentifier,
                menuSectionIdentifier,
                MenuSectionName: null,
                Name: null,
                Description: null,
                PriceAmount: null,
                DisplayOrder: null);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            InsertMenuItemSql,
            new
            {
                MenuItemIdentifier = menuItemIdentifier,
                MenuSectionIdentifier = menuSectionIdentifier,
                Name = normalizedName,
                Description = normalizedDescription,
                PriceAmount = normalizedPrice,
                DisplayOrder = section.NextDisplayOrder,
                CreatedAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            CreatedEventType,
            normalizedName,
            normalizedPrice,
            newDescription: null,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            SectionChangedEventType,
            newName: null,
            newPriceAmount: null,
            newDescription: null,
            newDisplayOrder: null,
            menuSectionIdentifier,
            now,
            cancellationToken).ConfigureAwait(false);

        if (normalizedDescription.Length > 0)
        {
            await InsertEventAsync(
                connection,
                transaction,
                menuItemIdentifier,
                actorPersonIdentifier,
                DescriptionChangedEventType,
                newName: null,
                newPriceAmount: null,
                normalizedDescription,
                newDisplayOrder: null,
                newMenuSectionIdentifier: null,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CreateMenuItemResult(
            CreateMenuItemOutcome.Created,
            menuItemIdentifier,
            menuSectionIdentifier,
            section.Name,
            normalizedName,
            normalizedDescription,
            normalizedPrice,
            section.NextDisplayOrder);
    }

    public async Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
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

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RenameMenuItemResult(
                RenameMenuItemOutcome.MenuItemNotFound, menuItemIdentifier, Name: null, PreviousName: null);
        }

        if (string.Equals(item.Name, normalizedName, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RenameMenuItemResult(
                RenameMenuItemOutcome.NoChange, menuItemIdentifier, item.Name, item.Name);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateNameSql,
            new { MenuItemIdentifier = menuItemIdentifier, Name = normalizedName },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            NameChangedEventType,
            normalizedName,
            newPriceAmount: null,
            newDescription: null,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RenameMenuItemResult(
            RenameMenuItemOutcome.Renamed, menuItemIdentifier, normalizedName, item.Name);
    }

    public async Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        decimal normalizedPrice = NormalizePrice(priceAmount);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RepriceMenuItemResult(
                RepriceMenuItemOutcome.MenuItemNotFound,
                menuItemIdentifier,
                Name: null,
                PriceAmount: null,
                PreviousPriceAmount: null);
        }

        if (item.PriceAmount == normalizedPrice)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RepriceMenuItemResult(
                RepriceMenuItemOutcome.NoChange,
                menuItemIdentifier,
                item.Name,
                item.PriceAmount,
                item.PriceAmount);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdatePriceSql,
            new { MenuItemIdentifier = menuItemIdentifier, PriceAmount = normalizedPrice },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            PriceChangedEventType,
            newName: null,
            normalizedPrice,
            newDescription: null,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RepriceMenuItemResult(
            RepriceMenuItemOutcome.Repriced,
            menuItemIdentifier,
            item.Name,
            normalizedPrice,
            item.PriceAmount);
    }

    public async Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
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

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return DescribeMenuItemOutcome.MenuItemNotFound;
        }

        if (string.Equals(item.Description, normalizedDescription, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return DescribeMenuItemOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateDescriptionSql,
            new { MenuItemIdentifier = menuItemIdentifier, Description = normalizedDescription },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            DescriptionChangedEventType,
            newName: null,
            newPriceAmount: null,
            normalizedDescription,
            newDisplayOrder: null,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return DescribeMenuItemOutcome.Described;
    }

    public async Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
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

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ReorderMenuItemOutcome.MenuItemNotFound;
        }

        if (item.DisplayOrder == displayOrder)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ReorderMenuItemOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateDisplayOrderSql,
            new { MenuItemIdentifier = menuItemIdentifier, DisplayOrder = displayOrder },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            ReorderedEventType,
            newName: null,
            newPriceAmount: null,
            newDescription: null,
            displayOrder,
            newMenuSectionIdentifier: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ReorderMenuItemOutcome.Reordered;
    }

    public async Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedMenuItemIdentifiers);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemPositionRow> locked = await connection
            .QueryAsync<MenuItemPositionRow>(new CommandDefinition(
                LockMenuItemsInSectionSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        Dictionary<Guid, int> storedPositions = locked
            .ToDictionary(row => row.MenuItemIdentifier, row => row.DisplayOrder);

        if (!IsPermutationOf(orderedMenuItemIdentifiers, storedPositions))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ResequenceMenuItemsOutcome.MenuItemSetChanged;
        }

        int moved = 0;

        for (int position = 0; position < orderedMenuItemIdentifiers.Count; position++)
        {
            Guid menuItemIdentifier = orderedMenuItemIdentifiers[position];

            if (storedPositions[menuItemIdentifier] == position)
            {
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                UpdateDisplayOrderSql,
                new { MenuItemIdentifier = menuItemIdentifier, DisplayOrder = position },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await InsertEventAsync(
                connection,
                transaction,
                menuItemIdentifier,
                actorPersonIdentifier,
                ReorderedEventType,
                newName: null,
                newPriceAmount: null,
                newDescription: null,
                position,
                newMenuSectionIdentifier: null,
                now,
                cancellationToken).ConfigureAwait(false);

            moved++;
        }

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ResequenceMenuItemsOutcome.NoChange;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ResequenceMenuItemsOutcome.Resequenced;
    }

    public async Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemLockRow? item = await ReadLockedAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MoveMenuItemToSectionOutcome.MenuItemNotFound;
        }

        if (item.MenuSectionIdentifier == menuSectionIdentifier)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MoveMenuItemToSectionOutcome.NoChange;
        }

        MenuSectionPositionRow? section = await connection
            .QuerySingleOrDefaultAsync<MenuSectionPositionRow>(new CommandDefinition(
                LockMenuSectionAndReadNextPositionSql,
                new { MenuSectionIdentifier = menuSectionIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (section is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MoveMenuItemToSectionOutcome.MenuSectionNotFound;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateMenuSectionAndPositionSql,
            new
            {
                MenuItemIdentifier = menuItemIdentifier,
                MenuSectionIdentifier = menuSectionIdentifier,
                DisplayOrder = section.NextDisplayOrder,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            actorPersonIdentifier,
            SectionChangedEventType,
            newName: null,
            newPriceAmount: null,
            newDescription: null,
            newDisplayOrder: null,
            menuSectionIdentifier,
            now,
            cancellationToken).ConfigureAwait(false);

        if (section.NextDisplayOrder != item.DisplayOrder)
        {
            await InsertEventAsync(
                connection,
                transaction,
                menuItemIdentifier,
                actorPersonIdentifier,
                ReorderedEventType,
                newName: null,
                newPriceAmount: null,
                newDescription: null,
                section.NextDisplayOrder,
                newMenuSectionIdentifier: null,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return MoveMenuItemToSectionOutcome.Moved;
    }

    private async Task InsertEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newName,
        decimal? newPriceAmount,
        string? newDescription,
        int? newDisplayOrder,
        Guid? newMenuSectionIdentifier,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            InsertMenuItemEventSql,
            new
            {
                MenuItemEventIdentifier = _identifierFactory.Create(),
                MenuItemIdentifier = menuItemIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                NewName = newName,
                NewPriceAmount = newPriceAmount,
                NewDescription = newDescription,
                NewDisplayOrder = newDisplayOrder,
                NewMenuSectionIdentifier = newMenuSectionIdentifier,
                OccurredAt = occurredAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task<MenuItemLockRow?> ReadLockedAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<MenuItemLockRow>(new CommandDefinition(
            LockMenuItemSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static decimal NormalizePrice(decimal priceAmount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priceAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(priceAmount, PriceExclusiveUpperBound);

        return Math.Round(priceAmount, 2, MidpointRounding.AwayFromZero);
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

    private sealed record MenuSectionPositionRow(string Name, int NextDisplayOrder);

    private sealed record MenuItemPositionRow(
        Guid MenuItemIdentifier,
        int DisplayOrder);

    private sealed record MenuItemLockRow(
        string Name,
        string Description,
        decimal PriceAmount,
        int DisplayOrder,
        Guid MenuSectionIdentifier);
}
