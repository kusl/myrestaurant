using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;

namespace MyRestaurant.DataAccess.Tests.Orders;

internal sealed class OrderTestWorld
{
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2E$dGFndGFndGFndGFndGFndGFndGFndGFndGE";

    private const string TruncateSql = """
        TRUNCATE TABLE person, restaurant_table, menu_item, menu_section CASCADE;
        """;

    private const string InsertPersonSql = """
        INSERT INTO person (
            person_identifier, username, display_name, email_address, phone_number,
            password_hash, totp_secret_protected, must_change_password, must_enroll_totp,
            security_stamp, failed_access_count, lockout_end_at, is_active, created_at)
        VALUES (
            @PersonIdentifier, @Username, @DisplayName, NULL, NULL,
            @PasswordHash, NULL, false, false,
            @SecurityStamp, 0, NULL, true, @CreatedAt);
        """;

    private const string InsertTableSql = """
        INSERT INTO restaurant_table (
            restaurant_table_identifier, label, join_secret, join_secret_rotated_at, is_active, created_at)
        VALUES (@TableIdentifier, @Label, @JoinSecret, NULL, @IsActive, @CreatedAt);
        """;

    private const string InsertSittingSql = """
        INSERT INTO table_sitting (
            table_sitting_identifier, restaurant_table_identifier, opened_at,
            closed_at, closed_by_person_identifier, settled_total_amount)
        VALUES (@SittingIdentifier, @TableIdentifier, @OpenedAt, NULL, NULL, NULL);
        """;

    private const string CloseSittingSql = """
        UPDATE table_sitting
        SET closed_at = @ClosedAt,
            closed_by_person_identifier = @ClosedByPersonIdentifier,
            settled_total_amount = @SettledTotalAmount
        WHERE table_sitting_identifier = @SittingIdentifier;
        """;

    private const string InsertMemberSql = """
        INSERT INTO table_sitting_member (
            table_sitting_member_identifier, table_sitting_identifier, person_identifier, joined_at)
        VALUES (@MemberIdentifier, @SittingIdentifier, @PersonIdentifier, @JoinedAt);
        """;

    private const string InsertMenuItemSql = """
        INSERT INTO menu_item (
            menu_item_identifier, menu_section_identifier, name, description,
            price_amount, display_order, is_active, created_at)
        VALUES (
            @MenuItemIdentifier, @MenuSectionIdentifier, @Name, @Description,
            @PriceAmount, @DisplayOrder, @IsActive, @CreatedAt);
        """;

    private const string InsertMenuSectionSql = """
        INSERT INTO menu_section (
            menu_section_identifier, name, description, display_order, is_active, created_at)
        VALUES (
            @MenuSectionIdentifier, @Name, @Description, @DisplayOrder, @IsActive, @CreatedAt);
        """;

    private const string InsertVisibilityEventSql = """
        INSERT INTO order_visibility_event (
            order_visibility_event_identifier, guest_order_identifier,
            actor_person_identifier, event_type, occurred_at)
        VALUES (
            @VisibilityEventIdentifier, @GuestOrderIdentifier,
            @ActorPersonIdentifier, @EventType, @OccurredAt);
        """;

    private const string InsertSecurityEventSql = """
        INSERT INTO security_event (
            security_event_identifier, subject_person_identifier, actor_person_identifier,
            event_type, occurred_at)
        VALUES (
            @SecurityEventIdentifier, @SubjectPersonIdentifier, @ActorPersonIdentifier::uuid,
            @EventType, @OccurredAt);
        """;

    private const string InsertMenuItemEventSql = """
        INSERT INTO menu_item_event (
            menu_item_event_identifier, menu_item_identifier, actor_person_identifier,
            event_type, new_name, new_price_amount, new_description, new_display_order,
            new_menu_section_identifier, occurred_at)
        VALUES (
            @MenuItemEventIdentifier, @MenuItemIdentifier, @ActorPersonIdentifier,
            @EventType, @NewName::text, @NewPriceAmount::numeric(10,2), @NewDescription::text,
            @NewDisplayOrder::integer, @NewMenuSectionIdentifier::uuid, @OccurredAt);
        """;

    private const string UpdateMenuItemSql = """
        UPDATE menu_item
        SET price_amount = @PriceAmount,
            is_active = @IsActive
        WHERE menu_item_identifier = @MenuItemIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly FixedClock _clock;
    private readonly IIdentifierFactory _identifierFactory;

    private Guid? _defaultMenuSectionIdentifier;

    public OrderTestWorld(
        IDatabaseConnectionFactory connectionFactory,
        FixedClock clock,
        IIdentifierFactory identifierFactory)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _identifierFactory = identifierFactory;
    }

    public async Task TruncateAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(TruncateSql, null, cancellationToken);

        _defaultMenuSectionIdentifier = null;
    }

    public async Task<Guid> AddMenuSectionAsync(
        string name,
        CancellationToken cancellationToken,
        string description = "",
        int displayOrder = 0,
        bool isActive = true)
    {
        Guid menuSectionIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertMenuSectionSql,
            new
            {
                MenuSectionIdentifier = menuSectionIdentifier,
                Name = name,
                Description = description,
                DisplayOrder = displayOrder,
                IsActive = isActive,
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken);

        return menuSectionIdentifier;
    }

    public async Task<Guid> AddPersonAsync(
        string username,
        string? displayName,
        CancellationToken cancellationToken)
    {
        Guid personIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertPersonSql,
            new
            {
                PersonIdentifier = personIdentifier,
                Username = username,
                DisplayName = displayName,
                PasswordHash = SamplePasswordHash,
                SecurityStamp = Guid.NewGuid(),
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken);

        return personIdentifier;
    }

    public async Task<Guid> AddTableAsync(string label, CancellationToken cancellationToken, bool isActive = true)
    {
        Guid tableIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertTableSql,
            new
            {
                TableIdentifier = tableIdentifier,
                Label = label,

                JoinSecret = new byte[32],
                IsActive = isActive,
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken);

        return tableIdentifier;
    }

    public async Task<Guid> OpenSittingAsync(Guid tableIdentifier, CancellationToken cancellationToken)
    {
        Guid sittingIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertSittingSql,
            new
            {
                SittingIdentifier = sittingIdentifier,
                TableIdentifier = tableIdentifier,
                OpenedAt = _clock.UtcNow,
            },
            cancellationToken);

        return sittingIdentifier;
    }

    public async Task CloseSittingAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        decimal settledTotalAmount,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            CloseSittingSql,
            new
            {
                SittingIdentifier = sittingIdentifier,
                ClosedAt = _clock.UtcNow,
                ClosedByPersonIdentifier = closedByPersonIdentifier,
                SettledTotalAmount = settledTotalAmount,
            },
            cancellationToken);

    public async Task JoinAsync(Guid sittingIdentifier, Guid personIdentifier, CancellationToken cancellationToken)
        => await ExecuteAsync(
            InsertMemberSql,
            new
            {
                MemberIdentifier = _identifierFactory.Create(),
                SittingIdentifier = sittingIdentifier,
                PersonIdentifier = personIdentifier,
                JoinedAt = _clock.UtcNow,
            },
            cancellationToken);

    public async Task<Guid> AddMenuItemAsync(
        string name,
        decimal priceAmount,
        CancellationToken cancellationToken,
        bool isActive = true,
        string description = "",
        int displayOrder = 0,
        Guid? menuSectionIdentifier = null)
    {
        Guid section = menuSectionIdentifier
            ?? await EnsureDefaultMenuSectionAsync(cancellationToken);

        Guid menuItemIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertMenuItemSql,
            new
            {
                MenuItemIdentifier = menuItemIdentifier,
                MenuSectionIdentifier = section,
                Name = name,
                Description = description,
                PriceAmount = priceAmount,
                DisplayOrder = displayOrder,
                IsActive = isActive,
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken);

        return menuItemIdentifier;
    }

    private async Task<Guid> EnsureDefaultMenuSectionAsync(CancellationToken cancellationToken)
    {
        if (_defaultMenuSectionIdentifier is { } existing)
        {
            return existing;
        }

        Guid created = await AddMenuSectionAsync("Menu", cancellationToken);
        _defaultMenuSectionIdentifier = created;

        return created;
    }

    public async Task SetMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        bool isActive,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            UpdateMenuItemSql,
            new { MenuItemIdentifier = menuItemIdentifier, PriceAmount = priceAmount, IsActive = isActive },
            cancellationToken);

    public async Task AddVisibilityEventAsync(
        Guid guestOrderIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            InsertVisibilityEventSql,
            new
            {
                VisibilityEventIdentifier = _identifierFactory.Create(),
                GuestOrderIdentifier = guestOrderIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                OccurredAt = _clock.UtcNow,
            },
            cancellationToken);

    public async Task<Guid> AddSecurityEventAsync(
        Guid subjectPersonIdentifier,
        Guid? actorPersonIdentifier,
        string eventType,
        CancellationToken cancellationToken)
    {
        Guid securityEventIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertSecurityEventSql,
            new
            {
                SecurityEventIdentifier = securityEventIdentifier,
                SubjectPersonIdentifier = subjectPersonIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                OccurredAt = _clock.UtcNow,
            },
            cancellationToken);

        return securityEventIdentifier;
    }

    public async Task<Guid> AddMenuItemEventAsync(
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newName,
        decimal? newPriceAmount,
        CancellationToken cancellationToken,
        string? newDescription = null,
        int? newDisplayOrder = null,
        Guid? newMenuSectionIdentifier = null)
    {
        Guid menuItemEventIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertMenuItemEventSql,
            new
            {
                MenuItemEventIdentifier = menuItemEventIdentifier,
                MenuItemIdentifier = menuItemIdentifier,
                ActorPersonIdentifier = actorPersonIdentifier,
                EventType = eventType,
                NewName = newName,
                NewPriceAmount = newPriceAmount,
                NewDescription = newDescription,
                NewDisplayOrder = newDisplayOrder,
                NewMenuSectionIdentifier = newMenuSectionIdentifier,
                OccurredAt = _clock.UtcNow,
            },
            cancellationToken);

        return menuItemEventIdentifier;
    }

    public async Task<int> CountAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? parameters, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        IEnumerable<T> rows = await connection.QueryAsync<T>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken));

        return rows.ToArray();
    }

    private async Task ExecuteAsync(string sql, object? parameters, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }
}
