using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.Authentication;

namespace MyRestaurant.DataAccess.Events;

public static class EventStream
{
    public const string Security = "security";

    public const string Order = "order";

    public const string Menu = "menu";

    public static IReadOnlyList<string> All { get; } = [Security, Order, Menu];

    public static bool IsKnown(string stream)
        => stream is Security or Order or Menu;
}

public static class EventTypeCatalogue
{
    public static IReadOnlyList<string> SecurityEventTypes { get; } =
    [
        SecurityEventType.AccountCreated,
        SecurityEventType.AccountDeactivated,
        SecurityEventType.AccountReactivated,
        SecurityEventType.PasswordChanged,
        SecurityEventType.PasswordResetByAdministrator,
        SecurityEventType.ForcedPasswordChangeCompleted,
        SecurityEventType.TotpEnrolled,
        SecurityEventType.TotpRemoved,
        SecurityEventType.TotpClearedByAdministrator,
        SecurityEventType.ForcedTotpEnrollmentCompleted,
        SecurityEventType.RecoveryCodeUsed,
        SecurityEventType.RecoveryCodesRegenerated,
        SecurityEventType.PasskeyRegistered,
        SecurityEventType.PasskeyRemoved,
        SecurityEventType.RoleGranted,
        SecurityEventType.RoleRevoked,
        SecurityEventType.SignInSucceeded,
        SecurityEventType.SignInFailed,
        SecurityEventType.AccountLockedOut,
    ];

    public static IReadOnlyList<string> OrderEventTypes { get; } =
    [
        OrderEventVocabulary.GuestSubmission,
        OrderEventVocabulary.StaffEdit,
        OrderEventVocabulary.PriceAdjustment,
        OrderEventVocabulary.Fulfillment,
        OrderEventVocabulary.FulfillmentReversal,
    ];

    public static IReadOnlyList<string> MenuEventTypes { get; } =
    [
        "created",
        "name_changed",
        "price_changed",
        "description_changed",
        "section_changed",
        "reordered",
        "activated",
        "deactivated",
    ];

    public static IReadOnlyList<string> All { get; } = SecurityEventTypes
        .Concat(OrderEventTypes)
        .Concat(MenuEventTypes)
        .ToArray();

    public static bool IsKnown(string eventType) => All.Contains(eventType, StringComparer.Ordinal);

    public static string? StreamFor(string eventType)
    {
        if (SecurityEventTypes.Contains(eventType, StringComparer.Ordinal))
        {
            return EventStream.Security;
        }

        if (OrderEventTypes.Contains(eventType, StringComparer.Ordinal))
        {
            return EventStream.Order;
        }

        return MenuEventTypes.Contains(eventType, StringComparer.Ordinal)
            ? EventStream.Menu
            : null;
    }
}

public sealed record ExplorerEvent(
    string Stream,
    Guid EventIdentifier,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid SubjectIdentifier,
    string SubjectLabel,
    string? SubjectDetail,
    Guid? ContextIdentifier,
    Guid? ActorIdentifier,
    string? ActorName,
    string? ActorRole,
    long? SequenceNumber,
    string? NewName,
    decimal? NewPriceAmount);

public sealed record EventExplorerFilter(
    bool IncludeSecurityEvents = true,
    bool IncludeOrderEvents = true,
    bool IncludeMenuEvents = true,
    string? Subject = null,
    string? Actor = null,
    string? EventType = null,
    DateTimeOffset? OccurredFrom = null,
    DateTimeOffset? OccurredBefore = null)
{
    public static EventExplorerFilter Everything { get; } = new();

    public bool IsNarrowed
        => !IncludeSecurityEvents
           || !IncludeOrderEvents
           || !IncludeMenuEvents
           || !string.IsNullOrWhiteSpace(Subject)
           || !string.IsNullOrWhiteSpace(Actor)
           || !string.IsNullOrWhiteSpace(EventType)
           || OccurredFrom is not null
           || OccurredBefore is not null;

    public bool IncludesNoStream
        => !IncludeSecurityEvents && !IncludeOrderEvents && !IncludeMenuEvents;
}

public interface IEventExplorerReads
{
    Task<IReadOnlyList<ExplorerEvent>> ListAsync(
        EventExplorerFilter filter,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public sealed class DapperEventExplorerReads : IEventExplorerReads
{
    private const char LikeEscape = '\\';

    private static readonly string ExplorerSql = $"""
        SELECT event_row.event_stream       AS Stream,
               event_row.event_identifier   AS EventIdentifier,
               event_row.event_type         AS EventType,
               event_row.occurred_at        AS OccurredAt,
               event_row.subject_identifier AS SubjectIdentifier,
               event_row.subject_label      AS SubjectLabel,
               event_row.subject_detail     AS SubjectDetail,
               event_row.context_identifier AS ContextIdentifier,
               event_row.actor_identifier   AS ActorIdentifier,
               event_row.actor_name         AS ActorName,
               event_row.actor_role         AS ActorRole,
               event_row.sequence_number    AS SequenceNumber,
               event_row.new_name           AS NewName,
               event_row.new_price_amount   AS NewPriceAmount
        FROM (
            SELECT '{EventStream.Security}'::text            AS event_stream,
                   security_event.security_event_identifier  AS event_identifier,
                   security_event.event_type                 AS event_type,
                   security_event.occurred_at                AS occurred_at,
                   security_event.subject_person_identifier  AS subject_identifier,
                   COALESCE(NULLIF(btrim(subject.display_name), ''), subject.username::text)
                                                             AS subject_label,
                   subject.username::text                    AS subject_detail,
                   NULL::uuid                                AS context_identifier,
                   security_event.actor_person_identifier    AS actor_identifier,
                   COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username::text)
                                                             AS actor_name,
                   NULL::text                                AS actor_role,
                   NULL::bigint                              AS sequence_number,
                   NULL::text                                AS new_name,
                   NULL::numeric(10,2)                       AS new_price_amount,
                   concat_ws(' ', subject.username::text, subject.display_name)
                                                             AS subject_search,
                   concat_ws(' ', actor.username::text, actor.display_name)
                                                             AS actor_search
            FROM security_event
            INNER JOIN person AS subject
                    ON subject.person_identifier = security_event.subject_person_identifier
            LEFT JOIN person AS actor
                    ON actor.person_identifier = security_event.actor_person_identifier
            WHERE @IncludeSecurityEvents::boolean

            UNION ALL

            SELECT '{EventStream.Order}'::text               AS event_stream,
                   order_event.order_event_identifier        AS event_identifier,
                   order_event.event_type                    AS event_type,
                   order_event.occurred_at                   AS occurred_at,
                   order_event.guest_order_identifier        AS subject_identifier,
                   COALESCE(NULLIF(btrim(owner.display_name), ''), owner.username::text)
                                                             AS subject_label,
                   restaurant_table.label                    AS subject_detail,
                   guest_order.table_sitting_identifier      AS context_identifier,
                   order_event.actor_person_identifier       AS actor_identifier,
                   COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username::text)
                                                             AS actor_name,
                   order_event.actor_role                    AS actor_role,
                   order_event.sequence_number               AS sequence_number,
                   NULL::text                                AS new_name,
                   NULL::numeric(10,2)                       AS new_price_amount,
                   concat_ws(' ', owner.username::text, owner.display_name, restaurant_table.label)
                                                             AS subject_search,
                   concat_ws(' ', actor.username::text, actor.display_name)
                                                             AS actor_search
            FROM order_event
            INNER JOIN guest_order
                    ON guest_order.guest_order_identifier = order_event.guest_order_identifier
            INNER JOIN table_sitting
                    ON table_sitting.table_sitting_identifier = guest_order.table_sitting_identifier
            INNER JOIN restaurant_table
                    ON restaurant_table.restaurant_table_identifier
                       = table_sitting.restaurant_table_identifier
            INNER JOIN person AS owner
                    ON owner.person_identifier = guest_order.person_identifier
            INNER JOIN person AS actor
                    ON actor.person_identifier = order_event.actor_person_identifier
            WHERE @IncludeOrderEvents::boolean

            UNION ALL

            SELECT '{EventStream.Menu}'::text                AS event_stream,
                   menu_item_event.menu_item_event_identifier
                                                             AS event_identifier,
                   menu_item_event.event_type                AS event_type,
                   menu_item_event.occurred_at               AS occurred_at,
                   menu_item_event.menu_item_identifier      AS subject_identifier,
                   menu_item.name                            AS subject_label,
                   NULL::text                                AS subject_detail,
                   NULL::uuid                                AS context_identifier,
                   menu_item_event.actor_person_identifier   AS actor_identifier,
                   COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username::text)
                                                             AS actor_name,
                   NULL::text                                AS actor_role,
                   NULL::bigint                              AS sequence_number,
                   menu_item_event.new_name                  AS new_name,
                   menu_item_event.new_price_amount          AS new_price_amount,
                   menu_item.name                            AS subject_search,
                   concat_ws(' ', actor.username::text, actor.display_name)
                                                             AS actor_search
            FROM menu_item_event
            INNER JOIN menu_item
                    ON menu_item.menu_item_identifier = menu_item_event.menu_item_identifier
            INNER JOIN person AS actor
                    ON actor.person_identifier = menu_item_event.actor_person_identifier
            WHERE @IncludeMenuEvents::boolean
        ) AS event_row
        WHERE (@SubjectPattern IS NULL
               OR event_row.subject_search ILIKE @SubjectPattern ESCAPE '{LikeEscape}')
          AND (@ActorPattern IS NULL
               OR event_row.actor_search ILIKE @ActorPattern ESCAPE '{LikeEscape}')
          AND (@EventType IS NULL OR event_row.event_type = @EventType)
          AND (@OccurredFrom IS NULL OR event_row.occurred_at >= @OccurredFrom)
          AND (@OccurredBefore IS NULL OR event_row.occurred_at < @OccurredBefore)
        ORDER BY event_row.occurred_at DESC, event_row.event_identifier DESC
        LIMIT @MaximumCount;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public DapperEventExplorerReads(IDatabaseConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<ExplorerEvent>> ListAsync(
        EventExplorerFilter filter,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (maximumCount <= 0 || filter.IncludesNoStream)
        {
            return [];
        }

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ExplorerRow> rows = await connection
            .QueryAsync<ExplorerRow>(new CommandDefinition(
                ExplorerSql,
                new
                {
                    filter.IncludeSecurityEvents,
                    filter.IncludeOrderEvents,
                    filter.IncludeMenuEvents,
                    SubjectPattern = SubstringPattern(filter.Subject),
                    ActorPattern = SubstringPattern(filter.Actor),
                    EventType = NullIfBlank(filter.EventType),
                    filter.OccurredFrom,
                    filter.OccurredBefore,
                    MaximumCount = maximumCount,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(row => new ExplorerEvent(
            row.Stream,
            row.EventIdentifier,
            row.EventType,
            AsUtc(row.OccurredAt),
            row.SubjectIdentifier,
            row.SubjectLabel,
            row.SubjectDetail,
            row.ContextIdentifier,
            row.ActorIdentifier,
            row.ActorName,
            row.ActorRole,
            row.SequenceNumber,
            row.NewName,
            row.NewPriceAmount)).ToArray();
    }

    private static string? SubstringPattern(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        string escaped = term
            .Trim()
            .Replace(LikeEscape.ToString(), $"{LikeEscape}{LikeEscape}", StringComparison.Ordinal)
            .Replace("%", $"{LikeEscape}%", StringComparison.Ordinal)
            .Replace("_", $"{LikeEscape}_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record ExplorerRow(
        string Stream,
        Guid EventIdentifier,
        string EventType,
        DateTime OccurredAt,
        Guid SubjectIdentifier,
        string SubjectLabel,
        string? SubjectDetail,
        Guid? ContextIdentifier,
        Guid? ActorIdentifier,
        string? ActorName,
        string? ActorRole,
        long? SequenceNumber,
        string? NewName,
        decimal? NewPriceAmount);
}
