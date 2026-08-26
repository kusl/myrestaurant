using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Menu;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuItemImageMetadata(
    Guid MenuItemImageIdentifier,
    Guid MenuItemIdentifier,
    string ContentType,
    int ByteLength,
    string AltText,
    DateTimeOffset UploadedAt);

public sealed record MenuItemImageContent(
    Guid MenuItemImageIdentifier,
    string ContentType,
    byte[] Bytes);

public enum AttachMenuItemImageOutcome
{
    Attached,
    Replaced,
    MenuItemNotFound,
    UnsupportedContentType,
    ContentTypeContradictedByBytes,
    BytesEmpty,
    BytesOverCap,
}

public sealed record AttachMenuItemImageResult(
    AttachMenuItemImageOutcome Outcome,
    Guid? MenuItemImageIdentifier);

public enum RemoveMenuItemImageOutcome
{
    Removed,
    NoImage,
    MenuItemNotFound,
}

public enum SetMenuItemImageAltTextOutcome
{
    Changed,
    NoChange,
    NoImage,
    MenuItemNotFound,
}

public interface IMenuItemImageDirectory
{
    Task<IReadOnlyList<MenuItemImageMetadata>> ListAsync(CancellationToken cancellationToken = default);

    Task<MenuItemImageMetadata?> FindForItemAsync(
        Guid menuItemIdentifier,
        CancellationToken cancellationToken = default);

    Task<MenuItemImageContent?> ReadContentAsync(
        Guid menuItemImageIdentifier,
        CancellationToken cancellationToken = default);

    Task<int?> ReadDeclaredByteCapAsync(CancellationToken cancellationToken = default);
}

public interface IMenuItemImageAdministration
{
    Task<AttachMenuItemImageResult> AttachMenuItemImageAsync(
        Guid menuItemImageIdentifier,
        Guid menuItemIdentifier,
        string contentType,
        byte[] bytes,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RemoveMenuItemImageOutcome> RemoveMenuItemImageAsync(
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<SetMenuItemImageAltTextOutcome> SetMenuItemImageAltTextAsync(
        Guid menuItemIdentifier,
        string altText,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

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

    private const string ByteCapSql = """
        SELECT (regexp_match(pg_get_constraintdef(pg_constraint.oid), '([0-9]+)'))[1]::int
        FROM pg_constraint
        WHERE pg_constraint.conname = 'menu_item_image_bytes_within_cap';
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

    public async Task<int?> ReadDeclaredByteCapAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                ByteCapSql,
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

public sealed class DapperMenuItemImageAdministration : IMenuItemImageAdministration
{
    private const string AttachedEventType = "attached";

    private const string ReplacedEventType = "replaced";

    private const string RemovedEventType = "removed";

    private const string AltTextChangedEventType = "alt_text_changed";

    private const string ByteCapConstraintName = "menu_item_image_bytes_within_cap";

    private const string LockMenuItemSql = """
        SELECT menu_item.menu_item_identifier
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    private const string LockExistingImageSql = """
        SELECT menu_item_image.menu_item_image_identifier AS MenuItemImageIdentifier,
               menu_item_image.alt_text                   AS AltText
        FROM menu_item_image
        WHERE menu_item_image.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

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

        string carriedAltText = replaced?.AltText ?? string.Empty;

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
