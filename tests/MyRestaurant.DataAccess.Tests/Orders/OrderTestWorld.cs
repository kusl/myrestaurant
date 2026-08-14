using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;

namespace MyRestaurant.DataAccess.Tests.Orders;

/// <summary>
/// Seeds the world an order needs to exist in — people, a table, an open sitting, membership, and a
/// menu — and hands back the identifiers, so each test file says what it is testing instead of
/// re-deriving a restaurant from scratch.
///
/// <para>The rows are written with plain SQL rather than through <c>DapperTableAdministration</c> and
/// friends on purpose, unlike <see cref="Displays.DisplayDevicePairingTests"/>: the pairing tests are
/// about a service that reads rows the app wrote, so arranging through the app is the point there,
/// whereas these tests are about the order transaction and the projection views, and routing the
/// arrangement through three other services would make a failure in any of them look like a failure
/// here. Menu items in particular have no write service at all yet — menu administration is M5 (§19) —
/// so there is nothing to arrange through.</para>
/// </summary>
internal sealed class OrderTestWorld
{
    /// <summary>A syntactically valid Argon2id PHC string (§3.2). Nothing here ever verifies it.</summary>
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2E$dGFndGFndGFndGFndGFndGFndGFndGFndGE";

    /// <summary>
    /// Everything downstream of these four hangs off them by foreign key, so CASCADE clears the whole
    /// order graph — guest orders, events, all five operation tables, kitchen notifications, and
    /// visibility events — without this list having to be kept in step with the schema.
    ///
    /// <para><c>menu_section</c> is named rather than reached: nothing references it yet, so CASCADE from
    /// the other three does not clear it, and a section surviving into the next test would make
    /// <c>MAX(display_order) + 1</c> hand out a number the previous test chose. It is named here rather
    /// than truncated locally in the one test class that writes sections, because 0004 gives
    /// <c>menu_item</c> a NOT NULL reference to it and at that point truncating items without their
    /// headings is the wrong order regardless of who asked.</para>
    /// </summary>
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

    /// <summary>
    /// <c>description</c> and <c>display_order</c> are named rather than left to their <c>0004</c>
    /// defaults, so that a caller who wants either can have it and every row this class writes is
    /// explicit about both. <c>menu_section_identifier</c> is absent because the column is: it lands in
    /// <c>0005</c>, and that is the migration that makes this INSERT the file deciding whether every
    /// ordering integration test compiles.
    /// </summary>
    private const string InsertMenuItemSql = """
        INSERT INTO menu_item (
            menu_item_identifier, name, description, price_amount, display_order, is_active, created_at)
        VALUES (
            @MenuItemIdentifier, @Name, @Description, @PriceAmount, @DisplayOrder, @IsActive, @CreatedAt);
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

    /// <summary>
    /// The two <c>0004</c> payload columns are omitted rather than passed as NULL, which is the same
    /// thing to PostgreSQL and a smaller surface here: no test in this project writes a
    /// <c>description_changed</c> or a <c>reordered</c> event by hand — the ones that care about those
    /// verbs drive <c>DapperMenuAdministration</c>, because the pair of rows is the fact worth asserting.
    /// The casts on the two columns that remain are load-bearing: Dapper sends an untyped parameter for a
    /// null, and §8.2's paired CHECKs are evaluated against the column's type.
    /// </summary>
    private const string InsertMenuItemEventSql = """
        INSERT INTO menu_item_event (
            menu_item_event_identifier, menu_item_identifier, actor_person_identifier,
            event_type, new_name, new_price_amount, occurred_at)
        VALUES (
            @MenuItemEventIdentifier, @MenuItemIdentifier, @ActorPersonIdentifier,
            @EventType, @NewName::text, @NewPriceAmount::numeric(10,2), @OccurredAt);
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
        => await ExecuteAsync(TruncateSql, null, cancellationToken);

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
                // The CHECK is octet_length(join_secret) = 32; nothing here derives a token from it.
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

    /// <summary>
    /// Closes a sitting the way §5.3 will: a closing instant, the person who closed it, and a stamped
    /// settled total, all three of which the schema's paired CHECKs require together.
    /// </summary>
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

    /// <summary>
    /// Writes one <c>menu_item</c> row and returns its identifier.
    ///
    /// <para><paramref name="description"/> and <paramref name="displayOrder"/> are trailing optional
    /// parameters with the column defaults as their values, so every existing call site reads exactly as
    /// it did and means exactly what it did. That is deliberate rather than lazy: eleven call sites across
    /// four test classes arrange a menu they have no opinion about, and making them all restate <c>""</c>
    /// and <c>0</c> would be eleven edits that assert nothing.</para>
    /// </summary>
    public async Task<Guid> AddMenuItemAsync(
        string name,
        decimal priceAmount,
        CancellationToken cancellationToken,
        bool isActive = true,
        string description = "",
        int displayOrder = 0)
    {
        Guid menuItemIdentifier = _identifierFactory.Create();
        await ExecuteAsync(
            InsertMenuItemSql,
            new
            {
                MenuItemIdentifier = menuItemIdentifier,
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

    public async Task SetMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        bool isActive,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            UpdateMenuItemSql,
            new { MenuItemIdentifier = menuItemIdentifier, PriceAmount = priceAmount, IsActive = isActive },
            cancellationToken);

    /// <summary>
    /// Appends an <c>order_visibility_event</c> row directly (§6.8), stamped with the current
    /// <see cref="FixedClock"/> instant.
    ///
    /// <para>Plain SQL rather than <c>DapperOrderVisibility</c>, for the reason this whole class prefers
    /// SQL: the readers under test in <c>OrderHistoryReadsTests</c> are about which rows they select, and
    /// arranging them through the write service would make a bug in that service look like a bug in the
    /// reader. It also reaches states the service deliberately refuses to create — a hide on an open
    /// sitting — which is the only way to assert what the readers do when they meet one.</para>
    /// </summary>
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

    /// <summary>
    /// Appends a <c>security_event</c> row directly (§8.2), stamped with the current
    /// <see cref="FixedClock"/> instant and returning its identifier.
    ///
    /// <para>Plain SQL rather than <c>DapperSecurityEventLog</c>, for the reason this whole class prefers
    /// SQL: the reader under test in <c>EventExplorerReadsTests</c> is about which rows it selects and how
    /// it joins them, and arranging through the writer would make a bug there look like a bug here. It
    /// also mints the identifier locally so a test can assert on the exact row it wrote — the writer keeps
    /// its identifier to itself.</para>
    ///
    /// <para><paramref name="actorPersonIdentifier"/> may be <c>null</c>:
    /// <c>security_event.actor_person_identifier</c> is the one nullable actor column in the three event
    /// tables (§8.2), and the explorer has to render that case rather than drop the row.</para>
    /// </summary>
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

    /// <summary>
    /// Appends a <c>menu_item_event</c> row directly (§7, §8.2), on the same terms.
    ///
    /// <para>The two payload columns are passed through rather than derived from the type, because §8.2's
    /// paired CHECKs already enforce which types carry which — <c>created</c> both, <c>name_changed</c>
    /// the name, <c>price_changed</c> the price, the two availability types neither — and a helper that
    /// second-guessed them would make it impossible to write the row that proves the reader carries a
    /// payload through untouched.</para>
    /// </summary>
    public async Task<Guid> AddMenuItemEventAsync(
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        string eventType,
        string? newName,
        decimal? newPriceAmount,
        CancellationToken cancellationToken)
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
                OccurredAt = _clock.UtcNow,
            },
            cancellationToken);

        return menuItemEventIdentifier;
    }

    /// <summary>A raw count, for the "nothing was written" assertions §6.5.9 lives on.</summary>
    public async Task<int> CountAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
    }

    /// <summary>A raw scalar, for reading a stored column back without going through a service.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, object? parameters, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken));
    }

    private async Task ExecuteAsync(string sql, object? parameters, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }
}
