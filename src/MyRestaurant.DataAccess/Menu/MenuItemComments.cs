using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using Npgsql;

namespace MyRestaurant.DataAccess.Menu;

public sealed record MenuItemComment(
    Guid MenuItemIdentifier,
    Guid PersonIdentifier,
    string AuthorName,
    string Body,
    DateTimeOffset OccurredAt);

public interface IMenuItemCommentDirectory
{
    Task<IReadOnlyList<MenuItemComment>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MenuItemComment>> ListForPersonAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default);

    Task<int?> ReadDeclaredBodyCapAsync(CancellationToken cancellationToken = default);
}

public enum SubmitMenuItemCommentOutcome
{
    Submitted,
    NoChange,
    MenuItemNotFound,
    BodyBlank,
    BodyOverCap,
}

public sealed record SubmitMenuItemCommentResult(
    SubmitMenuItemCommentOutcome Outcome,
    Guid MenuItemIdentifier,
    Guid PersonIdentifier,
    string? Body)
{
    public bool Submitted => Outcome is SubmitMenuItemCommentOutcome.Submitted;

    public bool ItemExists => Outcome is not SubmitMenuItemCommentOutcome.MenuItemNotFound;
}

public enum WithdrawMenuItemCommentOutcome
{
    Withdrawn,
    NoComment,
    MenuItemNotFound,
}

public interface IMenuItemComments
{
    Task<SubmitMenuItemCommentResult> SubmitAsync(
        Guid menuItemIdentifier,
        Guid personIdentifier,
        string body,
        CancellationToken cancellationToken = default);

    Task<WithdrawMenuItemCommentOutcome> WithdrawAsync(
        Guid menuItemIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperMenuItemCommentDirectory : IMenuItemCommentDirectory
{
    private const string CommentColumns = """
        menu_item_comment_current.menu_item_identifier AS MenuItemIdentifier,
        menu_item_comment_current.person_identifier    AS PersonIdentifier,
        COALESCE(NULLIF(btrim(author.display_name), ''), author.username)
                                                       AS AuthorName,
        menu_item_comment_current.body                 AS Body,
        menu_item_comment_current.occurred_at          AS OccurredAt
        """;

    private const string CommentFrom = """
        FROM menu_item_comment_current
        INNER JOIN person AS author
                ON author.person_identifier = menu_item_comment_current.person_identifier
        """;

    private const string CommentOrder = """
        ORDER BY menu_item_comment_current.menu_item_identifier,
                 menu_item_comment_current.occurred_at DESC,
                 menu_item_comment_current.menu_item_comment_event_identifier DESC
        """;

    private static readonly string ListSql = $"""
        SELECT {CommentColumns}
        {CommentFrom}
        WHERE menu_item_comment_current.body IS NOT NULL
        {CommentOrder};
        """;

    private static readonly string ListForPersonSql = $"""
        SELECT {CommentColumns}
        {CommentFrom}
        WHERE menu_item_comment_current.body IS NOT NULL
          AND menu_item_comment_current.person_identifier = @PersonIdentifier
        {CommentOrder};
        """;

    private const string BodyCapSql = """
        SELECT (regexp_match(pg_get_constraintdef(pg_constraint.oid), '([0-9]+)'))[1]::int
        FROM pg_constraint
        WHERE pg_constraint.conname = 'menu_item_comment_event_body_within_cap';
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperMenuItemCommentDirectory(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<MenuItemComment>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemCommentRow> rows = await connection
            .QueryAsync<MenuItemCommentRow>(new CommandDefinition(
                ListSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToComment).ToArray();
    }

    public async Task<IReadOnlyList<MenuItemComment>> ListForPersonAsync(
        Guid personIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MenuItemCommentRow> rows = await connection
            .QueryAsync<MenuItemCommentRow>(new CommandDefinition(
                ListForPersonSql,
                new { PersonIdentifier = personIdentifier },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToComment).ToArray();
    }

    public async Task<int?> ReadDeclaredBodyCapAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                BodyCapSql,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static MenuItemComment ToComment(MenuItemCommentRow row) => new(
        row.MenuItemIdentifier,
        row.PersonIdentifier,
        row.AuthorName,
        row.Body,
        new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)));

    private sealed record MenuItemCommentRow(
        Guid MenuItemIdentifier,
        Guid PersonIdentifier,
        string AuthorName,
        string Body,
        DateTime OccurredAt);
}

public sealed class DapperMenuItemComments : IMenuItemComments
{
    private const string SubmittedEventType = "submitted";

    private const string WithdrawnEventType = "withdrawn";

    private const string BodyCapConstraintName = "menu_item_comment_event_body_within_cap";

    private const string LockMenuItemSql = """
        SELECT menu_item.menu_item_identifier
        FROM menu_item
        WHERE menu_item.menu_item_identifier = @MenuItemIdentifier
        FOR UPDATE;
        """;

    private const string ReadStandingBodySql = """
        SELECT body
        FROM menu_item_comment_current
        WHERE menu_item_identifier = @MenuItemIdentifier
          AND person_identifier = @PersonIdentifier;
        """;

    private const string InsertCommentEventSql = """
        INSERT INTO menu_item_comment_event (
            menu_item_comment_event_identifier, menu_item_identifier,
            person_identifier, event_type, body, occurred_at)
        VALUES (
            @MenuItemCommentEventIdentifier, @MenuItemIdentifier,
            @PersonIdentifier, @EventType, @Body::text, @OccurredAt);
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    public DapperMenuItemComments(
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

    public async Task<SubmitMenuItemCommentResult> SubmitAsync(
        Guid menuItemIdentifier,
        Guid personIdentifier,
        string body,
        CancellationToken cancellationToken = default)
    {
        string trimmed = (body ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return new SubmitMenuItemCommentResult(
                SubmitMenuItemCommentOutcome.BodyBlank,
                menuItemIdentifier,
                personIdentifier,
                Body: null);
        }

        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid? item = await connection
            .ExecuteScalarAsync<Guid?>(new CommandDefinition(
                LockMenuItemSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SubmitMenuItemCommentResult(
                SubmitMenuItemCommentOutcome.MenuItemNotFound,
                menuItemIdentifier,
                personIdentifier,
                Body: null);
        }

        string? standing = await connection
            .ExecuteScalarAsync<string?>(new CommandDefinition(
                ReadStandingBodySql,
                new { MenuItemIdentifier = menuItemIdentifier, PersonIdentifier = personIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (string.Equals(standing, trimmed, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SubmitMenuItemCommentResult(
                SubmitMenuItemCommentOutcome.NoChange,
                menuItemIdentifier,
                personIdentifier,
                trimmed);
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertCommentEventSql,
                new
                {
                    MenuItemCommentEventIdentifier = _identifierFactory.Create(),
                    MenuItemIdentifier = menuItemIdentifier,
                    PersonIdentifier = personIdentifier,
                    EventType = SubmittedEventType,
                    Body = trimmed,
                    OccurredAt = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.CheckViolation
                  && string.Equals(
                      exception.ConstraintName, BodyCapConstraintName, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SubmitMenuItemCommentResult(
                SubmitMenuItemCommentOutcome.BodyOverCap,
                menuItemIdentifier,
                personIdentifier,
                Body: null);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SubmitMenuItemCommentResult(
            SubmitMenuItemCommentOutcome.Submitted,
            menuItemIdentifier,
            personIdentifier,
            trimmed);
    }

    public async Task<WithdrawMenuItemCommentOutcome> WithdrawAsync(
        Guid menuItemIdentifier,
        Guid personIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid? item = await connection
            .ExecuteScalarAsync<Guid?>(new CommandDefinition(
                LockMenuItemSql,
                new { MenuItemIdentifier = menuItemIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WithdrawMenuItemCommentOutcome.MenuItemNotFound;
        }

        string? standing = await connection
            .ExecuteScalarAsync<string?>(new CommandDefinition(
                ReadStandingBodySql,
                new { MenuItemIdentifier = menuItemIdentifier, PersonIdentifier = personIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (standing is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WithdrawMenuItemCommentOutcome.NoComment;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            InsertCommentEventSql,
            new
            {
                MenuItemCommentEventIdentifier = _identifierFactory.Create(),
                MenuItemIdentifier = menuItemIdentifier,
                PersonIdentifier = personIdentifier,
                EventType = WithdrawnEventType,
                Body = (string?)null,
                OccurredAt = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return WithdrawMenuItemCommentOutcome.Withdrawn;
    }
}
