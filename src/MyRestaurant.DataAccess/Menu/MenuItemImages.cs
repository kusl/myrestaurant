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
/// <param name="AltText">The sentence a screen reader reads instead of the picture; <c>""</c> when nobody has written one, which is <em>not</em> the same as an absent attribute — see <see cref="IMenuItemImageAdministration.SetMenuItemImageAltTextAsync"/>.</param>
/// <param name="UploadedAt">When it was attached, in UTC (rendered in the restaurant's zone by a surface, §8.1).</param>
public sealed record MenuItemImageMetadata(
    Guid MenuItemImageIdentifier,
    Guid MenuItemIdentifier,
    string ContentType,
    int ByteLength,
    string AltText,
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
/// <para><b>An answer per refusal rather than a boolean, and every one of them is a different sentence
/// for the person who chose the file.</b> "It did not work" on an upload surface is the failure §11.1's
/// own data-loaded rule exists to prevent one register up: an operator who cannot tell a file too large
/// from a file that is not an image tries the same file again.</para>
///
/// <para><b>No count of the members is stated, and the number that used to be here was wrong (F-102).</b>
/// It said <em>six</em> and there were seven, three lines below it, in the summary of the very enum it
/// was counting — which is F-77's ruling arriving in a new shape: a census in prose that no gate reaches,
/// beside the only honest copy of itself. It is <b>deleted</b> rather than corrected, on that ruling, and
/// the members below are the census. It was found by reading this type in order to write the surface that
/// renders one sentence per member, which is <b>F-93's</b> timing for the third time.</para>
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
/// The outcome of <see cref="IMenuItemImageAdministration.SetMenuItemImageAltTextAsync"/> (§7).
///
/// <para><b><see cref="NoImage"/> and <see cref="MenuItemNotFound"/> are two answers rather than one</b>,
/// on <see cref="RemoveMenuItemImageOutcome"/>'s reading: a caption for a picture that is not there is an
/// operator whose page went stale under them, and a caption for an item that is not there is a page left
/// open after somebody else deleted the dish. The surfaces do different things with the two — one reports
/// in place, the other navigates away — so collapsing them would make the second unreachable.</para>
/// </summary>
public enum SetMenuItemImageAltTextOutcome
{
    /// <summary>The caption moved, and one <c>alt_text_changed</c> event says what to.</summary>
    Changed,

    /// <summary>It was already exactly that, character for character. Nothing written, on the no-op rule every menu verb follows.</summary>
    NoChange,

    /// <summary>The item exists and has no picture, so there is nothing for a caption to describe. Nothing written.</summary>
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
/// <para><b><see cref="ListAsync"/> now has the caller it was named for, and the obligation it was named
/// under is discharged.</b> It shipped with <c>0006</c> having none, was recorded as a read with no caller
/// in three consecutive slices, and §11.1's guest menu is what it was for: <b>one</b> read per page load,
/// decorating a whole list of cards, where a per-card lookup would have turned a sixty-dish menu into
/// sixty queries inside a render loop.</para>
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
    /// <remarks>
    /// <b>A replace carries the caption forward onto the new row, and this signature is unchanged by
    /// <c>0007</c> because of it.</b> The alternative was a sixth parameter and a field on the upload form,
    /// and it is refused for a concrete reason rather than for brevity: the attach form requires a file, so
    /// a caption settable only there would make correcting a typo cost a re-upload — a new
    /// <c>menu_item_image_identifier</c>, every cached copy of a photograph invalidated across the
    /// building, and a <c>replaced</c> event recording a replacement that replaced nothing. So the caption
    /// is written by <see cref="SetMenuItemImageAltTextAsync"/>, and this verb moves it from the row it
    /// deletes to the row it writes, which is also the honest default: somebody replacing a photograph of
    /// the salmon with a better photograph of the salmon has not withdrawn what they wrote about it. It
    /// does <b>not</b> write an <c>alt_text_changed</c> event for the carry, on the no-op rule — nothing
    /// about the caption changed.
    /// </remarks>
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

    /// <summary>
    /// Writes the sentence a screen reader reads instead of the picture (§7, §11.1).
    ///
    /// <para><b>Why the caption is a verb of its own rather than a field on the upload.</b> See the remark
    /// on <see cref="AttachMenuItemImageAsync"/>: a caption settable only at upload time would make fixing
    /// a typo cost a new identifier and a building's worth of invalidated caches. This verb touches one
    /// <c>text</c> column and leaves the bytes, the identifier and therefore <em>every URL</em> exactly
    /// where they were, which is the whole point — §7's route is a content address, and a caption is not
    /// content.</para>
    ///
    /// <para><b>It is on the picture rather than on the item, and that is <c>0007</c>'s ruling.</b>
    /// Alternative text describes a photograph: <em>"served on a bed of wilted greens with a lemon
    /// wedge"</em> is true of one picture and false of the next one somebody takes. A column on
    /// <c>menu_item</c> would outlive the picture it described and nothing could tell that it had stopped
    /// being true, where a column on <c>menu_item_image</c> is deleted with the bytes it belongs to.</para>
    ///
    /// <para><b><c>""</c> is a supported value and means <em>no caption</em></b> (§7), so there is no
    /// separate clearing verb — the same arrangement <c>menu_item.description</c> has. It is emphatically
    /// not the same thing as an <c>&lt;img&gt;</c> with no <c>alt</c> attribute at all: a missing attribute
    /// makes a screen reader announce a URL, where <c>alt=""</c> makes it skip an image whose surroundings
    /// already say what it is. §11.1's card is a button holding the dish's name and price, so <c>""</c> is
    /// the <em>correct</em> answer there for most pictures and a caption earns its place only when it says
    /// something the name does not (<b>F-103</b>).</para>
    ///
    /// <para>Takes the item <c>FOR UPDATE</c> and then the picture row, on the same lock ordering both
    /// verbs above already run in, so nothing here introduces a new one.</para>
    /// </summary>
    /// <param name="menuItemIdentifier">The item whose picture is being captioned.</param>
    /// <param name="altText">The caption. Stored verbatim; <c>""</c> means none. Trimming, if any, belongs to the surface — the same division <c>RenameMenuItemAsync</c> already draws.</param>
    /// <param name="actorPersonIdentifier">Who wrote it, recorded on the event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SetMenuItemImageAltTextOutcome> SetMenuItemImageAltTextAsync(
        Guid menuItemIdentifier,
        string altText,
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
        menu_item_image.alt_text                    AS AltText,
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
        row.AltText,
        new DateTimeOffset(DateTime.SpecifyKind(row.UploadedAt, DateTimeKind.Utc)));

    private sealed record MenuItemImageMetadataRow(
        Guid MenuItemImageIdentifier,
        Guid MenuItemIdentifier,
        string ContentType,
        int ByteLength,
        string AltText,
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

    private const string AltTextChangedEventType = "alt_text_changed";

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
    /// Which picture is already attached, if any, locked. <b>Two columns, and still not the bytes</b> — the
    /// write needs the identifier it is about to replace or remove, and it needs the caption because
    /// <c>0007</c>'s ruling is that a replace carries one forward rather than resetting it. The format and
    /// the size are deliberately still absent: both are on the event that attached the picture, which is
    /// the whole reason that event carries a payload.
    /// </summary>
    private const string LockExistingImageSql = """
        SELECT menu_item_image.menu_item_image_identifier AS MenuItemImageIdentifier,
               menu_item_image.alt_text                   AS AltText
        FROM menu_item_image
        WHERE menu_item_image.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    /// <summary>
    /// The caption, and nothing else. <b>No <c>uploaded_at</c> touch</b>, which is a decision rather than
    /// an omission: that column says when the <em>picture</em> arrived, and a surface renders it beside the
    /// file's format and size as a fact about the upload. Writing a caption is not an upload, and moving
    /// that timestamp would make §11.4's panel report a photograph as newer than it is.
    /// </summary>
    private const string UpdateAltTextSql = """
        UPDATE menu_item_image
        SET alt_text = @AltText
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string DeleteImageSql = """
        DELETE FROM menu_item_image
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private const string InsertImageSql = """
        INSERT INTO menu_item_image (
            menu_item_image_identifier, menu_item_identifier, content_type, bytes, alt_text, uploaded_at)
        VALUES (
            @MenuItemImageIdentifier, @MenuItemIdentifier, @ContentType, @Bytes, @AltText, @UploadedAt);
        """;

    /// <summary>
    /// One INSERT for all four event types. §8.2's three paired CHECKs tie each payload column to the
    /// types that carry it, so every other type passes NULL and the database refuses any combination this
    /// file gets wrong — <c>removed</c> carries none of the three, and <c>alt_text_changed</c> carries only
    /// the caption, which is why <c>0007</c> had to widen no constraint but its own.
    /// </summary>
    private const string InsertImageEventSql = """
        INSERT INTO menu_item_image_event (
            menu_item_image_event_identifier, menu_item_identifier, menu_item_image_identifier,
            actor_person_identifier, event_type, new_content_type, new_byte_length, new_alt_text,
            occurred_at)
        VALUES (
            @MenuItemImageEventIdentifier, @MenuItemIdentifier, @MenuItemImageIdentifier,
            @ActorPersonIdentifier, @EventType, @NewContentType, @NewByteLength, @NewAltText,
            @OccurredAt);
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

        AttachedImageRow? replaced = await ReadAttachedImageAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        // 0007's ruling, and the one line of behaviour that migration exists to permit: the caption moves
        // from the row about to be deleted onto the row about to be written. Somebody replacing a
        // photograph of the salmon with a better photograph of the salmon has not withdrawn what they
        // wrote about it, and the alternative — resetting to '' — would silently strip alternative text
        // off a guest's menu as a side effect of improving a picture. A first attach has no row to carry
        // from and therefore lands at '', which is what "no caption yet" is spelled as (§7).
        //
        // NO alt_text_changed EVENT IS WRITTEN FOR THE CARRY. Nothing about the caption changed, and the
        // no-op rule every verb in this file follows says an event records a change rather than a
        // transaction.
        string carriedAltText = replaced?.AltText ?? string.Empty;

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
                    AltText = carriedAltText,
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
            newAltText: null,
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

        AttachedImageRow? attached = await ReadAttachedImageAsync(
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
            attached.MenuItemImageIdentifier,
            actorPersonIdentifier,
            RemovedEventType,
            newContentType: null,
            newByteLength: null,
            newAltText: null,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return RemoveMenuItemImageOutcome.Removed;
    }

    public async Task<SetMenuItemImageAltTextOutcome> SetMenuItemImageAltTextAsync(
        Guid menuItemIdentifier,
        string altText,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(altText);

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await MenuItemExistsAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return SetMenuItemImageAltTextOutcome.MenuItemNotFound;
        }

        AttachedImageRow? attached = await ReadAttachedImageAsync(
            connection, transaction, menuItemIdentifier, cancellationToken).ConfigureAwait(false);

        if (attached is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return SetMenuItemImageAltTextOutcome.NoImage;
        }

        // The no-op check is INSIDE the transaction and under the lock, which is the arrangement every
        // comparing verb in this layer uses and is not interchangeable with checking it on the way in: a
        // caption read before the lock is a caption another administrator may have changed in between, and
        // this verb would then write an alt_text_changed event recording a move that did not happen.
        //
        // Ordinal, because a caption is text somebody typed and two strings differing only in case are two
        // different captions. A culture-sensitive comparison here would make "Wilted greens" and "wilted
        // greens" the same edit on one operator's machine and different edits on another's.
        if (string.Equals(attached.AltText, altText, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return SetMenuItemImageAltTextOutcome.NoChange;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateAltTextSql,
            new { MenuItemIdentifier = menuItemIdentifier, AltText = altText },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The event names the picture the caption belongs to, which is the same identifier the row carries
        // and NOT a new one: nothing about the bytes changed, so §7's route key must not change either —
        // that is the whole reason this is a verb rather than a re-upload.
        await InsertEventAsync(
            connection,
            transaction,
            menuItemIdentifier,
            attached.MenuItemImageIdentifier,
            actorPersonIdentifier,
            AltTextChangedEventType,
            newContentType: null,
            newByteLength: null,
            altText,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return SetMenuItemImageAltTextOutcome.Changed;
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

    private static async Task<AttachedImageRow?> ReadAttachedImageAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        CancellationToken cancellationToken)
        => await connection.QuerySingleOrDefaultAsync<AttachedImageRow>(new CommandDefinition(
            LockExistingImageSql,
            new { MenuItemIdentifier = menuItemIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// The locked picture row as both write verbs need it: which picture, and what it is captioned. A
    /// record rather than a tuple so that the caller reads <c>attached.AltText</c> rather than
    /// <c>attached.Item2</c>, and a positional record is safe here where it would not be for a stored
    /// timestamp — no member is a <see cref="DateTimeOffset"/>, so Npgsql's <c>timestamptz</c>
    /// materialisation is not in play.
    /// </summary>
    private sealed record AttachedImageRow(Guid MenuItemImageIdentifier, string AltText);

    private async Task InsertEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid menuItemIdentifier,
        Guid menuItemImageIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newContentType,
        int? newByteLength,
        string? newAltText,
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
                NewAltText = newAltText,
                OccurredAt = occurredAt,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
}
