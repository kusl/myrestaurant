using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Menu;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Menu;

/// <summary>
/// What is recorded about one menu item's picture, without the picture (TECHNICAL_SPECIFICATION §7,
/// §8.2).
///
/// <para><b>The bytes are deliberately absent from this record.</b> §11.1 renders a card per item and
/// needs one thing per image — the identifier, because §7's route is keyed on it — plus enough to write an
/// <c>&lt;img&gt;</c> honestly. A metadata type carrying the bytes would put every image on the guest's
/// menu into memory on every page load, which for sixty dishes is twelve megabytes to render sixty URLs.
/// <see cref="MenuItemImageContent"/> is the other read and it is called once per image, by the route.</para>
///
/// <para><see cref="ByteLength"/> is computed by the read as <c>octet_length(bytes)</c> and is <b>not</b> a
/// stored column, which is F-101's ruling: a length beside the bytes is one fact written twice, in a place
/// where one <c>UPDATE</c> can make the two disagree.</para>
/// </summary>
/// <param name="MenuItemImageIdentifier">The image's UUIDv7 primary key (ADR-0011), and the value §7's route is keyed on — it changes when the picture changes, which is what makes an immutable cache header truthful.</param>
/// <param name="MenuItemIdentifier">The item this picture belongs to. One per item in version 1 (§8.2's UNIQUE).</param>
/// <param name="ContentType">The stored media type, checked against the bytes' own signature when it was attached.</param>
/// <param name="ByteLength">How many bytes the picture is, computed from the bytes.</param>
/// <param name="UploadedAt">When it was attached, in UTC (rendered in the restaurant's zone by a surface, §8.1).</param>
public sealed record MenuItemImageMetadata(
    Guid MenuItemImageIdentifier,
    Guid MenuItemIdentifier,
    string ContentType,
    int ByteLength,
    DateTimeOffset UploadedAt);

/// <summary>
/// One picture, bytes and all — the read §7's route performs and nothing else does.
/// </summary>
/// <param name="MenuItemImageIdentifier">The image that was asked for.</param>
/// <param name="ContentType">What to send it back as. It agreed with the bytes when it was stored, which is the whole reason the response may set this header from a column.</param>
/// <param name="Bytes">The picture, exactly as it arrived.</param>
public sealed record MenuItemImageContent(
    Guid MenuItemImageIdentifier,
    string ContentType,
    byte[] Bytes);

/// <summary>
/// The outcome of <see cref="IMenuItemImageAdministration.AttachMenuItemImageAsync"/> (§7).
///
/// <para><b>Six answers rather than a boolean, and every one of them is a different sentence for the
/// person who chose the file.</b> "It did not work" on an upload surface is the failure §11.1's own
/// data-loaded rule exists to prevent one register up: an operator who cannot tell a file too large from a
/// file that is not an image tries the same file again.</para>
/// </summary>
public enum AttachMenuItemImageOutcome
{
    /// <summary>The item had no picture and now has this one. One <c>attached</c> event.</summary>
    Attached,

    /// <summary>The item had a picture; it is gone and this one replaced it, under a <b>new</b> identifier. One <c>replaced</c> event.</summary>
    Replaced,

    /// <summary>No such item. Nothing written.</summary>
    MenuItemNotFound,

    /// <summary>The declared media type is not one this application serves (<see cref="ImageFormat.RecognisedContentTypes"/>). Nothing written.</summary>
    UnsupportedContentType,

    /// <summary>The declared media type is one this application serves and the bytes are not it. Nothing written.</summary>
    ContentTypeContradictedByBytes,

    /// <summary>Zero bytes. Nothing written — and this is refused before the signature check, so the answer names the empty file rather than blaming its format.</summary>
    BytesEmpty,

    /// <summary>Over the size cap §8.2 declares. Nothing written, and <b>the cap is the database's</b>: this outcome is reported by reading the violated constraint's name, so no number is restated here.</summary>
    BytesOverCap,
}

/// <summary>
/// What <see cref="IMenuItemImageAdministration.AttachMenuItemImageAsync"/> did, and — when it stored
/// something — which identifier it stored it under.
/// </summary>
/// <param name="Outcome">Which of §7's answers this was.</param>
/// <param name="MenuItemImageIdentifier">The stored image's identifier on <see cref="AttachMenuItemImageOutcome.Attached"/> and <see cref="AttachMenuItemImageOutcome.Replaced"/>, and <c>null</c> on every refusal. It is returned rather than assumed from the argument because a caller that generated one and had it refused must not build a URL out of it.</param>
public sealed record AttachMenuItemImageResult(
    AttachMenuItemImageOutcome Outcome,
    Guid? MenuItemImageIdentifier);

/// <summary>The outcome of <see cref="IMenuItemImageAdministration.RemoveMenuItemImageAsync"/> (§7).</summary>
public enum RemoveMenuItemImageOutcome
{
    /// <summary>The picture is gone and a <c>removed</c> event says what it was.</summary>
    Removed,

    /// <summary>The item exists and had no picture. Nothing written, on the no-op rule every menu verb follows.</summary>
    NoImage,

    /// <summary>No such item. Nothing written.</summary>
    MenuItemNotFound,
}

/// <summary>
/// Reads what is stored about menu item pictures (TECHNICAL_SPECIFICATION §7, §11.1, §11.4).
///
/// <para><b>Three reads, and each names the surface it exists for</b>, because a read with no caller is
/// the same defect this project keeps recording about workflow verbs — a code path no test can reach
/// through the interface meant to protect it. <see cref="ListAsync"/> is §11.1's guest menu and §11.4's
/// index, which decorate a whole list and must not ask per card; <see cref="FindForItemAsync"/> is the
/// item's own administration page, which renders one; <see cref="ReadContentAsync"/> is §7's route, which
/// is the only caller in the application that wants the bytes.</para>
///
/// <para><b>There is deliberately no event-log reader here yet.</b> <c>menu_item_image_event</c> is
/// written from the first day, because §7 requires every menu mutation to leave one and R§6.8 has since
/// rev 1; but nothing renders it until §11.4 grows the panel that does, and shipping the reader now would
/// be the read-with-no-caller defect in the same slice as the sentence above. It arrives with its
/// surface, exactly as <see cref="IMenuSectionEventLog"/> arrived with the section editor.</para>
/// </summary>
public interface IMenuItemImageDirectory
{
    /// <summary>
    /// Every picture on the menu, as metadata, ordered by the item it belongs to so the result is stable
    /// across calls. Items with no picture are simply absent — this is not a left join over
    /// <c>menu_item</c>, because the caller already holds the menu and wants to know which of it is
    /// decorated.
    /// </summary>
    Task<IReadOnlyList<MenuItemImageMetadata>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One item's picture, as metadata, or <c>null</c> when it has none. An unknown item and an
    /// undecorated one are the same answer, deliberately: the caller is a page that already knows whether
    /// the item exists, and inventing a second null-ish outcome would make every call site handle a case
    /// it cannot reach.
    /// </summary>
    Task<MenuItemImageMetadata?> FindForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes, by image identifier, or <c>null</c> when no such image exists — which is the 404 §7's
    /// route returns for a URL that named a picture since replaced or removed.
    /// </summary>
    Task<MenuItemImageContent?> ReadContentAsync(
        Guid menuItemImageIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Attaches and removes menu item pictures (TECHNICAL_SPECIFICATION §7, §8.2). Administrator-only, on the
/// same reading of §3.7 that makes create, rename and reprice administrator-only: a picture is what the
/// menu says about a dish, not whether the kitchen has any of it left.
///
/// <para><b>A replace mints a new identifier and deletes the old row, and that is a requirement rather
/// than an implementation choice.</b> §7's route is <c>/menu/image/{menu_item_image_identifier}</c>
/// precisely so that the URL changes when the picture does and
/// <c>Cache-Control: public, max-age=31536000, immutable</c> is a true statement. Updating the bytes under
/// a stable identifier would need an ETag and a revalidation round trip per image per page load, on
/// phones, to avoid serving last week's photograph.</para>
///
/// <para><b>Both verbs take the <c>menu_item</c> row <c>FOR UPDATE</c> first</b>, which does two jobs at
/// once: it establishes that the item exists inside the transaction that is about to reference it, and it
/// serialises two administrators uploading to the same dish, so the loser waits rather than the two racing
/// the UNIQUE constraint. It is also the nesting this file family already runs in — a refile takes an item
/// lock and then a section lock (§7) — so nothing here introduces a new lock ordering.</para>
///
/// <para><b>The size cap is not restated in this file.</b> §8.2 declares it in a named CHECK; a byte array
/// that exceeds it is refused by PostgreSQL and this implementation reports
/// <see cref="AttachMenuItemImageOutcome.BytesOverCap"/> by reading the constraint's name off the error.
/// A second copy in C# would be F-65's mechanism and, worse, would be the belt that hides the buckle —
/// F-64, F-69 and F-75 are each an instance of a second check making the first one's absence
/// invisible.</para>
/// </summary>
public interface IMenuItemImageAdministration
{
    /// <summary>
    /// Puts a picture on an item, replacing whatever was there. The caller mints the identifier (§8.1: no
    /// database defaults for identifiers) and is told whether it was used.
    /// </summary>
    /// <param name="menuItemImageIdentifier">The UUIDv7 to store this picture under, from <see cref="IIdentifierFactory"/>.</param>
    /// <param name="menuItemIdentifier">The item to decorate.</param>
    /// <param name="contentType">What the uploader says the bytes are. Checked against the bytes.</param>
    /// <param name="bytes">The picture. Stored verbatim: nothing here decodes, resizes or re-encodes it (§7).</param>
    /// <param name="actorPersonIdentifier">Who did it — recorded on the event, not on the picture, which is the same arrangement <c>menu_item</c> and <c>menu_section</c> have.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<AttachMenuItemImageResult> AttachMenuItemImageAsync(
        Guid menuItemImageIdentifier,
        Guid menuItemIdentifier,
        string contentType,
        byte[] bytes,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the picture off an item. <b>The row is deleted rather than flagged</b>, which is a stated
    /// exception to §6.8's hide-never-delete rule and not an oversight: that rule exists so that history
    /// is never orphaned, and here the history is in <c>menu_item_image_event</c> rather than in the row —
    /// the <c>removed</c> event records which picture it was, how large, in what format, by whom and when.
    /// Keeping the bytes would grow §15's recovery set by half a megabyte for every photograph anybody
    /// ever retook, for no reader.
    /// </summary>
    Task<RemoveMenuItemImageOutcome> RemoveMenuItemImageAsync(
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuItemImageDirectory"/>. One connection per call from the
/// singleton <see cref="IDatabaseConnectionFactory"/>, no transaction (these are lone reads), columns
/// aliased to the record's member names, every column reference table-qualified, and rows read into an
/// internal row type with a <see cref="DateTime"/> member before being projected — because Npgsql
/// materialises <c>timestamptz</c> as <see cref="DateTime"/> and Dapper's constructor binding will not
/// feed one into a <see cref="DateTimeOffset"/> parameter (the same fix every other reader in this layer
/// carries).
///
/// <para><b>The metadata reads compute the length and never select the bytes</b>, and the honest account
/// of what that costs is this: PostgreSQL stores a <c>bytea</c> this size out of line and compressed, so a
/// scan of <c>menu_item_image</c> for its other columns does not touch the images at all — but
/// <c>octet_length</c> does detoast the value it measures. At the cardinality of one restaurant's menu
/// that is the right trade against a stored integer that can drift from the bytes it describes
/// (F-101). It is written here rather than left implicit because a claim beside a computation is exactly
/// the kind of sentence this project has had to make true twice.</para>
/// </summary>
public sealed class DapperMenuItemImageDirectory : IMenuItemImageDirectory
{
    private const string MetadataColumns = """
        menu_item_image.menu_item_image_identifier  AS MenuItemImageIdentifier,
        menu_item_image.menu_item_identifier        AS MenuItemIdentifier,
        menu_item_image.content_type                AS ContentType,
        octet_length(menu_item_image.bytes)         AS ByteLength,
        menu_item_image.uploaded_at                 AS UploadedAt
        """;

    // Ordered by the item, so two calls against an unchanged table return the same sequence. It is not
    // §11.1's menu ordering and does not pretend to be: a caller decorating a menu holds the menu's own
    // order already and looks this up by identifier.
    private static readonly string ListSql = $"""
        SELECT {MetadataColumns}
        FROM menu_item_image
        ORDER BY menu_item_image.menu_item_identifier;
        """;

    private static readonly string ForItemSql = $"""
        SELECT {MetadataColumns}
        FROM menu_item_image
        WHERE menu_item_image.menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string ContentSql = """
        SELECT menu_item_image.menu_item_image_identifier AS MenuItemImageIdentifier,
               menu_item_image.content_type               AS ContentType,
               menu_item_image.bytes                      AS Bytes
        FROM menu_item_image
        WHERE menu_item_image.menu_item_image_identifier = @MenuItemImageIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuItemImageDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuItemImageMetadata>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemImageMetadataRow> rows = await connection
            .QueryAsync<MenuItemImageMetadataRow>(new CommandDefinition(
                ListSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToMetadata).ToArray();
    }

    public async Task<MenuItemImageMetadata?> FindForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        MenuItemImageMetadataRow? row = await connection
            .QuerySingleOrDefaultAsync<MenuItemImageMetadataRow>(new CommandDefinition(
                ForItemSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : ToMetadata(row);
    }

    public async Task<MenuItemImageContent?> ReadContentAsync(
        Guid menuItemImageIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .QuerySingleOrDefaultAsync<MenuItemImageContent>(new CommandDefinition(
                ContentSql,
                new { MenuItemImageIdentifier = menuItemImageIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static MenuItemImageMetadata ToMetadata(MenuItemImageMetadataRow row) => new(
        row.MenuItemImageIdentifier,
        row.MenuItemIdentifier,
        row.ContentType,
        row.ByteLength,
        new DateTimeOffset(DateTime.SpecifyKind(row.UploadedAt, DateTimeKind.Utc)));

    private sealed record MenuItemImageMetadataRow(
        Guid MenuItemImageIdentifier,
        Guid MenuItemIdentifier,
        string ContentType,
        int ByteLength,
        DateTime UploadedAt);
}

/// <summary>
/// The Dapper implementation of <see cref="IMenuItemImageAdministration"/>. One connection and one
/// transaction per operation, one <see cref="IClock.UtcNow"/> instant on every row of each write, and a
/// UUIDv7 event key from <see cref="IIdentifierFactory"/> (ADR-0011) — the shape every write in this layer
/// has.
///
/// <para><b>Everything decidable without the database is decided before the transaction opens.</b> An
/// empty upload, a media type this application does not serve, and bytes that contradict their own
/// declaration are all refused with no connection taken at all: they are properties of the arguments, and
/// opening a transaction to reject one would put a lock on a menu item for the duration of a refusal
/// nothing about the database could have changed.</para>
///
/// <para><b>The one thing left to the database is the size cap</b>, and that is deliberate rather than
/// lazy — see the interface. The two constraint names below are the interface to that decision, which is
/// the same standing this project gives constraint names elsewhere: <c>SchemaMigrationRunnerTests</c>
/// asserts them by name and <c>0005</c> drops one by name.</para>
/// </summary>
public sealed class DapperMenuItemImageAdministration : IMenuItemImageAdministration
{
    /// <summary>Stored spellings of <c>menu_item_image_event.event_type</c> (§8.2's CHECK).</summary>
    private const string AttachedEventType = "attached";

    private const string ReplacedEventType = "replaced";

    private const string RemovedEventType = "removed";

    /// <summary>
    /// §8.2's named cap on <c>octet_length(bytes)</c>. The <em>name</em> is here; the number is not, and
    /// that asymmetry is the whole point — a caller learns the upload was too large without this file
    /// having an opinion about how large is too large.
    /// </summary>
    private const string ByteCapConstraintName = "menu_item_image_bytes_within_cap";

    /// <summary>
    /// Establishes the item inside the transaction and serialises two uploads to the same dish. Selecting
    /// the identifier rather than <c>1</c> so the row-not-found case is a null rather than a count.
    /// </summary>
    private const string LockMenuItemSql = """
        SELECT menu_item.menu_item_identifier
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    /// <summary>
    /// Which picture is already attached, if any, locked. <b>One column, and nothing more</b> — the write
    /// needs the identifier it is about to replace or remove, the bytes are irrelevant to both verbs, and
    /// the format and size are already on the event that attached it, which is the whole reason that event
    /// carries a payload.
    /// </summary>
    private const string LockExistingImageSql = """
        SELECT menu_item_image.menu_item_image_identifier
        FROM menu_item_image
        WHERE menu_item_image.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    private const string DeleteImageSql = """
        DELETE FROM menu_item_image
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string InsertImageSql = """
        INSERT INTO menu_item_image (
            menu_item_image_identifier, menu_item_identifier, content_type, bytes, uploaded_at)
        VALUES (
            @MenuItemImageIdentifier, @MenuItemIdentifier, @ContentType, @Bytes, @UploadedAt);
        """;

    /// <summary>
    /// One INSERT for all three event types. §8.2's two paired CHECKs tie both payload columns to
    /// <c>attached</c> and <c>replaced</c> together, so <c>removed</c> passes NULL for both and the
    /// database refuses any combination this file gets wrong.
    /// </summary>
    private const string InsertImageEventSql = """
        INSERT INTO menu_item_image_event (
            menu_item_image_event_identifier, menu_item_identifier, menu_item_image_identifier,
            actor_person_identifier, event_type, new_content_type, new_byte_length, occurred_at)
        VALUES (
            @MenuItemImageEventIdentifier, @MenuItemIdentifier, @MenuItemImageIdentifier,
            @ActorPersonIdentifier, @EventType, @NewContentType, @NewByteLength, @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperMenuItemImageAdministration(
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

    public async Task<AttachMenuItemImageResult> AttachMenuItemImageAsync(
        Guid menuItemImageIdentifier,
        Guid menuItemIdentifier,
        string contentType,
        byte[] bytes,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // Empty first, and the order matters for the sentence the operator reads: zero bytes carry no
        // signature, so the mismatch check below would blame the format of a file that has none.
        if (bytes.Length == 0)
        {
            return Refused(AttachMenuItemImageOutcome.BytesEmpty);
        }

        if (!ImageFormat.RecognisedContentTypes.Contains(contentType, StringComparer.Ordinal))
        {
            return Refused(AttachMenuItemImageOutcome.UnsupportedContentType);
        }

        if (!ImageFormat.BytesMatchDeclaredContentType(bytes, contentType))
        {
            return Refused(AttachMenuItemImageOutcome.ContentTypeContradictedByBytes);
        }

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await MenuItemExistsAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Refused(AttachMenuItemImageOutcome.MenuItemNotFound);
        }

        Guid? replaced = await ReadAttachedImageAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        // Delete before insert rather than update in place: menu_item_identifier is UNIQUE, and the new
        // picture must land under a NEW identifier so that §7's route key changes with the bytes.
        if (replaced is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                DeleteImageSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertImageSql,
                new
                {
                    MenuItemImageIdentifier = menuItemImageIdentifier,
                    MenuItemIdentifier = menuItemIdentifier,
                    ContentType = contentType,
                    Bytes = bytes,
                    UploadedAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.CheckViolation
                  && string.Equals(
                      exception.ConstraintName, ByteCapConstraintName, StringComparison.Ordinal))
        {
            // The one refusal this file cannot make for itself, because the number belongs to §8.2.
            // Any OTHER check violation is rethrown deliberately: it means this file wrote a shape the
            // schema forbids, which is a defect rather than a rejected upload.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Refused(AttachMenuItemImageOutcome.BytesOverCap);
        }

        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            menuItemImageIdentifier,
            actorPersonIdentifier,
            replaced is null ? AttachedEventType : ReplacedEventType,
            contentType,
            bytes.Length,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AttachMenuItemImageResult(
            replaced is null
                ? AttachMenuItemImageOutcome.Attached
                : AttachMenuItemImageOutcome.Replaced,
            menuItemImageIdentifier);
    }

    public async Task<RemoveMenuItemImageOutcome> RemoveMenuItemImageAsync(
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await MenuItemExistsAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RemoveMenuItemImageOutcome.MenuItemNotFound;
        }

        Guid? attached = await ReadAttachedImageAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (attached is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RemoveMenuItemImageOutcome.NoImage;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            DeleteImageSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The removal names the picture that went away and carries neither payload column, which is what
        // §8.2's two biconditionals require of this type. What it WAS — the format and the size — is on
        // the attach or replace event that put it there, which is the reason those two carry a payload at
        // all: after this row is gone, that event is the only record of what a guest was looking at.
        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            attached.Value,
            actorPersonIdentifier,
            RemovedEventType,
            newContentType: null,
            newByteLength: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return RemoveMenuItemImageOutcome.Removed;
    }

    private static AttachMenuItemImageResult Refused(AttachMenuItemImageOutcome outcome)
        => new(outcome, MenuItemImageIdentifier: null);

    private static async Task<bool> MenuItemExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
    {
        Guid? found = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            LockMenuItemSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return found is not null;
    }

    private static async Task<Guid?> ReadAttachedImageAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            LockExistingImageSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private async Task InsertEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        Guid menuItemImageIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newContentType,
        int? newByteLength,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            InsertImageEventSql,
            new
            {
                MenuItemImageEventIdentifier = _identifierFactory.Create(),
                MenuItemIdentifier = menuItemIdentifier,
                MenuItemImageIdentifier = menuItemImageIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                NewContentType = newContentType,
                NewByteLength = newByteLength,
                OccurredAt = occurredAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
}
