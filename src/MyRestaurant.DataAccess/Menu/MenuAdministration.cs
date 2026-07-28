using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>The outcome of <see cref="IMenuAdministration.RenameMenuItemAsync"/> (§7).</summary>
public enum RenameMenuItemOutcome
{
    /// <summary>The name changed and a <c>name_changed</c> event was written.</summary>
    Renamed,

    /// <summary>The new name equalled the current one; nothing was written.</summary>
    NoChange,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>The outcome of <see cref="IMenuAdministration.RepriceMenuItemAsync"/> (§7).</summary>
public enum RepriceMenuItemOutcome
{
    /// <summary>The price changed and a <c>price_changed</c> event was written.</summary>
    Repriced,

    /// <summary>The new price equalled the current one; nothing was written.</summary>
    NoChange,

    /// <summary>No item has that identifier; nothing was written.</summary>
    MenuItemNotFound,
}

/// <summary>
/// A created menu item, as stored (§7). Both members are what the row and its <c>created</c> event
/// actually carry, which is not necessarily what the caller passed: the name is trimmed and the price is
/// rounded to the column's own <c>numeric(10,2)</c> scale before either row is written, so a surface can
/// echo this back without a second read and without lying by two hundredths.
/// </summary>
/// <param name="MenuItemIdentifier">The identifier the caller minted (ADR-0011), now a <c>menu_item</c> primary key.</param>
/// <param name="Name">The stored name.</param>
/// <param name="PriceAmount">The stored price.</param>
public sealed record CreateMenuItemResult(
    Guid MenuItemIdentifier,
    string Name,
    decimal PriceAmount);

/// <summary>
/// The outcome of one rename, carrying both names so a confirmation can say what it used to be — which
/// is the whole reason §7 logs renames rather than silently overwriting a column.
/// </summary>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="MenuItemIdentifier">The item the attempt named.</param>
/// <param name="Name">The stored name after the call; <c>null</c> when the item does not exist.</param>
/// <param name="PreviousName">The name before the call; <c>null</c> when the item does not exist.</param>
public sealed record RenameMenuItemResult(
    RenameMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    string? PreviousName)
{
    /// <summary>True only when the name actually moved — the precondition for publishing <c>MenuChanged</c> (§9).</summary>
    public bool Changed => Outcome is RenameMenuItemOutcome.Renamed;

    /// <summary>True unless the identifier named nothing.</summary>
    public bool ItemExists => Outcome is not RenameMenuItemOutcome.MenuItemNotFound;
}

/// <summary>
/// The outcome of one reprice, carrying both prices for the same reason
/// <see cref="RenameMenuItemResult"/> carries both names.
/// </summary>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="MenuItemIdentifier">The item the attempt named.</param>
/// <param name="Name">The item's name, for a confirmation that can then avoid a second read; <c>null</c> when it does not exist.</param>
/// <param name="PriceAmount">The stored price after the call; <c>null</c> when the item does not exist.</param>
/// <param name="PreviousPriceAmount">The price before the call; <c>null</c> when the item does not exist.</param>
public sealed record RepriceMenuItemResult(
    RepriceMenuItemOutcome Outcome,
    Guid MenuItemIdentifier,
    string? Name,
    decimal? PriceAmount,
    decimal? PreviousPriceAmount)
{
    /// <summary>True only when the price actually moved — the precondition for publishing <c>MenuChanged</c> (§9).</summary>
    public bool Changed => Outcome is RepriceMenuItemOutcome.Repriced;

    /// <summary>True unless the identifier named nothing.</summary>
    public bool ItemExists => Outcome is not RepriceMenuItemOutcome.MenuItemNotFound;
}

/// <summary>
/// Menu administration (TECHNICAL_SPECIFICATION §7, §11.4: "Menu (CRUD + activity, event history per
/// item)") — creating an item, renaming it, and repricing it, each writing the <c>menu_item</c> row and
/// its mirroring <c>menu_item_event</c> in one transaction.
///
/// <para><b>Why availability is not here.</b> <see cref="IMenuAvailability"/> already owns the
/// activate/deactivate write, and it stays there: §7 gives that one verb to kitchen and counter as well
/// as to administrators, because the kitchen is the surface that knows the salmon has run out, and
/// §11.2 puts the toggle on the kitchen board. Everything on <em>this</em> interface is administrator
/// only (§11.4). Two interfaces, two audiences, one event log — which is the point of the log.</para>
///
/// <para><b>Why rename and reprice are separate calls rather than one edit.</b> §7's event vocabulary
/// has <c>name_changed</c> and <c>price_changed</c> as distinct types with mutually exclusive payload
/// columns, enforced by the §8.2 paired CHECKs. A combined "save" that moved both would have to write
/// two events anyway, and would then have to decide what to do when one half is a no-op. Two calls make
/// the log read the way somebody investigating a price dispute needs it to.</para>
///
/// <para><b>Prices on existing order lines never move.</b> §6.5.4 captures <c>unit_price_amount</c> into
/// the adding operation, so repricing changes what the <em>next</em> line costs and nothing that is
/// already on a bill. <c>OrderReadModelTests</c> owns that fact against a real database; nothing here
/// needs to defend it.</para>
///
/// <para><b>Names are not unique, deliberately.</b> <c>menu_item.name</c> carries no UNIQUE constraint
/// (§8.2), unlike <c>restaurant_table.label</c>, so nothing here rejects a duplicate: a real kitchen
/// runs "Soup" as a rotating special, and inventing a constraint the schema of record does not have
/// would be this layer overruling it. The index page orders by name, so duplicates sit next to each
/// other where somebody will notice them.</para>
/// </summary>
public interface IMenuAdministration
{
    /// <summary>
    /// Creates a menu item, active, and writes the matching <c>created</c> event (which carries both the
    /// name and the price — §8.2's CHECK requires both for that type). The identifier is minted by the
    /// caller (ADR-0011) so a surface can link straight to the new item.
    ///
    /// <para>The name is trimmed; the price is rounded to two decimals before either row is written, so
    /// the row and its event can never disagree about what was set. Returns what was stored.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="priceAmount"/> is negative or does not fit <c>numeric(10,2)</c>.</exception>
    Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames one item and appends a <c>name_changed</c> event carrying the new name. A rename to the
    /// name it already has writes nothing: an append-only log of "somebody pressed Rename" is noise, and
    /// §11.4's per-item history is meant to be read by a person.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reprices one item and appends a <c>price_changed</c> event carrying the new price, on the same
    /// no-op terms as <see cref="RenameMenuItemAsync"/>. The comparison is made after rounding, so
    /// asking for 4.500 when the stored price is 4.50 is correctly a no-op rather than an event that
    /// records nothing having happened.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="priceAmount"/> is negative or does not fit <c>numeric(10,2)</c>.</exception>
    Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuAdministration"/>. One connection and one transaction
/// per operation, one <see cref="IClock.UtcNow"/> instant on both rows of each write, a UUIDv7 event key
/// from <see cref="IIdentifierFactory"/> (ADR-0011) — the shape every write in this layer has, and the
/// shape <see cref="DapperMenuAvailability"/> already established for this table.
///
/// <para>The row is taken <c>FOR UPDATE</c> before it is compared, for the reason
/// <see cref="DapperMenuAvailability"/> takes it: without the lock, two administrators repricing the
/// same item at once could both read 4.50, both write 5.00, and log two <c>price_changed</c> events for
/// one change. The price would still be right and the history would be a lie, which is the worse of the
/// two failures in an append-only system (ADR-0002).</para>
/// </summary>
public sealed class DapperMenuAdministration : IMenuAdministration
{
    /// <summary>Stored spellings of <c>menu_item_event.event_type</c> (§8.2's CHECK).</summary>
    private const string CreatedEventType = "created";

    private const string NameChangedEventType = "name_changed";

    private const string PriceChangedEventType = "price_changed";

    /// <summary>
    /// The column is <c>numeric(10,2)</c>: eight digits before the point, two after. A larger value is
    /// PostgreSQL error 22003 at INSERT time, which would surface as an opaque exception well after the
    /// form that caused it; refusing it here names the problem.
    /// </summary>
    private const decimal PriceExclusiveUpperBound = 100_000_000m;

    private const string InsertMenuItemSql = """
        INSERT INTO menu_item (menu_item_identifier, name, price_amount, is_active, created_at)
        VALUES (@MenuItemIdentifier, @Name, @PriceAmount, true, @CreatedAt);
        """;

    private const string LockMenuItemSql = """
        SELECT menu_item.name         AS Name,
               menu_item.price_amount AS PriceAmount
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

    /// <summary>
    /// One INSERT for all three event types. The §8.2 paired CHECKs tie each nullable payload column to
    /// exactly the types that carry it — <c>created</c> needs both, <c>name_changed</c> the name alone,
    /// <c>price_changed</c> the price alone — so the callers below pass NULL for whichever the type must
    /// not have, and the database refuses any combination this file gets wrong.
    /// </summary>
    private const string InsertMenuItemEventSql = """
        INSERT INTO menu_item_event (
            menu_item_event_identifier, menu_item_identifier, actor_person_identifier,
            event_type, new_name, new_price_amount, occurred_at)
        VALUES (
            @MenuItemEventIdentifier, @MenuItemIdentifier, @ActorPersonIdentifier,
            @EventType, @NewName, @NewPriceAmount, @OccurredAt);
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
        string name,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        decimal normalizedPrice = NormalizePrice(priceAmount);
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertMenuItemSql,
            new
            {
                MenuItemIdentifier = menuItemIdentifier,
                Name = normalizedName,
                PriceAmount = normalizedPrice,
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
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CreateMenuItemResult(menuItemIdentifier, normalizedName, normalizedPrice);
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

        // name is `text`, not citext, so compare ordinally — "Soup" and "soup" are two different names,
        // and renaming between them is a real change somebody meant to make.
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

        // decimal == compares value, not scale, so 4.50 read back from numeric(10,2) equals a 4.5 the
        // caller typed. The comparison is after rounding, so 4.499 is a change and 4.500 is not.
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

    private async Task InsertEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newName,
        decimal? newPriceAmount,
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

    /// <summary>
    /// Rounds to the column's own scale, away from zero — which is what PostgreSQL's <c>numeric</c> does,
    /// so rounding here rather than letting the database do it silently means the value returned to the
    /// caller and the value in both rows are the same number.
    /// </summary>
    private static decimal NormalizePrice(decimal priceAmount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priceAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(priceAmount, PriceExclusiveUpperBound);

        return Math.Round(priceAmount, 2, MidpointRounding.AwayFromZero);
    }

    // Dapper maps this positional record by constructor-parameter name against the aliased columns above.
    private sealed record MenuItemLockRow(string Name, decimal PriceAmount);
}
