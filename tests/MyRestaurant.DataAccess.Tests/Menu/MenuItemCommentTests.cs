using Dapper;
using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Npgsql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuItemCommentTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountCommentEventsSql = """
        SELECT count(*)::int FROM menu_item_comment_event;
        """;

    private const string InsertCommentEventSql = """
        INSERT INTO menu_item_comment_event (
            menu_item_comment_event_identifier, menu_item_identifier,
            person_identifier, event_type, body, occurred_at)
        VALUES (
            @MenuItemCommentEventIdentifier, @MenuItemIdentifier,
            @PersonIdentifier, @EventType, @Body::text, @OccurredAt);
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 5, 14, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _adaIdentifier;
    private Guid _benIdentifier;

    public MenuItemCommentTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is null)
        {
            return;
        }

        new SchemaMigrationRunner(_fixture.ConnectionString)
        {
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        }.Run();

        _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);
        _world = new OrderTestWorld(_connectionFactory, _clock, _identifiers);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _world.TruncateAsync(cancellationToken);

        _adaIdentifier = await _world.AddPersonAsync("ada", "Ada Lovelace", cancellationToken);
        _benIdentifier = await _world.AddPersonAsync("ben", "  ", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubmittingWritesOneEventAndTheFoldReturnsTheBody()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        SubmitMenuItemCommentResult result = await Comments()
            .SubmitAsync(salmon, _adaIdentifier, "  Best thing here  ", cancellationToken);

        Assert.Equal(SubmitMenuItemCommentOutcome.Submitted, result.Outcome);
        Assert.True(result.Submitted);
        Assert.True(result.ItemExists);
        Assert.Equal("Best thing here", result.Body);
        Assert.Equal(salmon, result.MenuItemIdentifier);
        Assert.Equal(_adaIdentifier, result.PersonIdentifier);

        Assert.Equal(1, await World().CountAsync(CountCommentEventsSql, cancellationToken));

        string? stored = await World().ScalarAsync<string>(
            """
            SELECT body
            FROM menu_item_comment_event
            WHERE menu_item_identifier = @MenuItemIdentifier
              AND person_identifier = @PersonIdentifier;
            """,
            new { MenuItemIdentifier = salmon, PersonIdentifier = _adaIdentifier },
            cancellationToken);

        Assert.Equal("Best thing here", stored);

        MenuItemComment only = Assert.Single(
            await Directory().ListForPersonAsync(_adaIdentifier, cancellationToken));

        Assert.Equal(salmon, only.MenuItemIdentifier);
        Assert.Equal("Best thing here", only.Body);
        Assert.Equal(_clock.UtcNow, only.OccurredAt);
    }

    [Fact]
    public async Task ResubmittingTheSameBodyWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Comments().SubmitAsync(salmon, _adaIdentifier, "Best thing here", cancellationToken);

        SubmitMenuItemCommentResult again = await Comments()
            .SubmitAsync(salmon, _adaIdentifier, "   Best thing here ", cancellationToken);

        Assert.Equal(SubmitMenuItemCommentOutcome.NoChange, again.Outcome);
        Assert.False(again.Submitted);
        Assert.True(again.ItemExists);
        Assert.Equal("Best thing here", again.Body);

        Assert.Equal(1, await World().CountAsync(CountCommentEventsSql, cancellationToken));
    }

    [Fact]
    public async Task ResubmittingADifferentBodyAppendsAndTheFoldTakesTheLater()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Comments().SubmitAsync(salmon, _adaIdentifier, "Too salty", cancellationToken);

        SubmitMenuItemCommentResult second = await Comments()
            .SubmitAsync(salmon, _adaIdentifier, "Perfect tonight", cancellationToken);

        Assert.Equal(SubmitMenuItemCommentOutcome.Submitted, second.Outcome);
        Assert.Equal(2, await World().CountAsync(CountCommentEventsSql, cancellationToken));

        MenuItemComment standing = Assert.Single(
            await Directory().ListForPersonAsync(_adaIdentifier, cancellationToken));

        Assert.Equal("Perfect tonight", standing.Body);
    }

    [Fact]
    public async Task WithdrawingAppendsAnEventAndTheCommentStopsStanding()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Comments().SubmitAsync(salmon, _adaIdentifier, "Too salty", cancellationToken);

        Assert.Equal(
            WithdrawMenuItemCommentOutcome.Withdrawn,
            await Comments().WithdrawAsync(salmon, _adaIdentifier, cancellationToken));

        Assert.Equal(2, await World().CountAsync(CountCommentEventsSql, cancellationToken));
        Assert.Empty(await Directory().ListForPersonAsync(_adaIdentifier, cancellationToken));
        Assert.Empty(await Directory().ListAsync(cancellationToken));

        int keptBodies = await World().CountAsync(
            """
            SELECT count(*)::int FROM menu_item_comment_event WHERE body IS NOT NULL;
            """,
            cancellationToken);

        Assert.Equal(1, keptBodies);
    }

    [Fact]
    public async Task WithdrawingWhenNothingStandsWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        Assert.Equal(
            WithdrawMenuItemCommentOutcome.NoComment,
            await Comments().WithdrawAsync(salmon, _adaIdentifier, cancellationToken));

        await Comments().SubmitAsync(salmon, _adaIdentifier, "Too salty", cancellationToken);
        await Comments().WithdrawAsync(salmon, _adaIdentifier, cancellationToken);

        Assert.Equal(
            WithdrawMenuItemCommentOutcome.NoComment,
            await Comments().WithdrawAsync(salmon, _adaIdentifier, cancellationToken));

        Assert.Equal(2, await World().CountAsync(CountCommentEventsSql, cancellationToken));
    }

    [Fact]
    public async Task AWithdrawnCommentCanBeSubmittedAgain()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Comments().SubmitAsync(salmon, _adaIdentifier, "Too salty", cancellationToken);
        await Comments().WithdrawAsync(salmon, _adaIdentifier, cancellationToken);

        SubmitMenuItemCommentResult again = await Comments()
            .SubmitAsync(salmon, _adaIdentifier, "Too salty", cancellationToken);

        Assert.Equal(SubmitMenuItemCommentOutcome.Submitted, again.Outcome);
        Assert.Equal(3, await World().CountAsync(CountCommentEventsSql, cancellationToken));

        MenuItemComment standing = Assert.Single(
            await Directory().ListForPersonAsync(_adaIdentifier, cancellationToken));

        Assert.Equal("Too salty", standing.Body);
    }

    [Fact]
    public async Task AnUnknownItemReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid missing = _identifiers.Create();

        SubmitMenuItemCommentResult submitted = await Comments()
            .SubmitAsync(missing, _adaIdentifier, "Nowhere", cancellationToken);

        Assert.Equal(SubmitMenuItemCommentOutcome.MenuItemNotFound, submitted.Outcome);
        Assert.False(submitted.ItemExists);
        Assert.False(submitted.Submitted);
        Assert.Null(submitted.Body);

        Assert.Equal(
            WithdrawMenuItemCommentOutcome.MenuItemNotFound,
            await Comments().WithdrawAsync(missing, _adaIdentifier, cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountCommentEventsSql, cancellationToken));
    }

    [Fact]
    public async Task ABlankBodyIsRefusedWithoutReachingTheDatabase()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        foreach (string blank in new[] { "", "   ", "\t\n " })
        {
            SubmitMenuItemCommentResult refused = await Comments()
                .SubmitAsync(salmon, _adaIdentifier, blank, cancellationToken);

            Assert.Equal(SubmitMenuItemCommentOutcome.BodyBlank, refused.Outcome);
            Assert.True(refused.ItemExists);
            Assert.Null(refused.Body);
        }

        Assert.Equal(0, await World().CountAsync(CountCommentEventsSql, cancellationToken));
    }

    [Fact]
    public async Task ABodyOverTheDeclaredCapIsRefusedAndTheCapIsReadFromTheConstraint()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        int? cap = await Directory().ReadDeclaredBodyCapAsync(cancellationToken);

        Assert.NotNull(cap);
        Assert.True(cap > 0);

        SubmitMenuItemCommentResult atCap = await Comments()
            .SubmitAsync(salmon, _adaIdentifier, new string('a', cap.Value), cancellationToken);

        Assert.Equal(SubmitMenuItemCommentOutcome.Submitted, atCap.Outcome);

        SubmitMenuItemCommentResult overCap = await Comments()
            .SubmitAsync(salmon, _benIdentifier, new string('b', cap.Value + 1), cancellationToken);

        Assert.Equal(SubmitMenuItemCommentOutcome.BodyOverCap, overCap.Outcome);
        Assert.True(overCap.ItemExists);
        Assert.Null(overCap.Body);

        Assert.Equal(1, await World().CountAsync(CountCommentEventsSql, cancellationToken));
    }

    [Fact]
    public async Task TheSchemaRefusesEveryForbiddenShapeOfAnEventRow()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        Assert.Equal(
            "menu_item_comment_event_type_vocabulary",
            await RefusedConstraintAsync(salmon, "loved", "Anything", cancellationToken));

        Assert.Equal(
            "menu_item_comment_event_body_payload",
            await RefusedConstraintAsync(salmon, "submitted", null, cancellationToken));

        Assert.Equal(
            "menu_item_comment_event_body_payload",
            await RefusedConstraintAsync(salmon, "withdrawn", "Anything", cancellationToken));

        Assert.Equal(
            "menu_item_comment_event_body_not_blank",
            await RefusedConstraintAsync(salmon, "submitted", "   ", cancellationToken));

        Assert.Equal(0, await World().CountAsync(CountCommentEventsSql, cancellationToken));
    }

    [Fact]
    public async Task OnePersonsCommentsDoNotAppearInAnothersList()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);
        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        await Comments().SubmitAsync(salmon, _adaIdentifier, "Ada on salmon", cancellationToken);
        await Comments().SubmitAsync(soup, _benIdentifier, "Ben on soup", cancellationToken);

        MenuItemComment adas = Assert.Single(
            await Directory().ListForPersonAsync(_adaIdentifier, cancellationToken));
        MenuItemComment bens = Assert.Single(
            await Directory().ListForPersonAsync(_benIdentifier, cancellationToken));

        Assert.Equal("Ada on salmon", adas.Body);
        Assert.Equal("Ben on soup", bens.Body);
        Assert.Empty(await Directory().ListForPersonAsync(_identifiers.Create(), cancellationToken));

        Assert.Equal(2, (await Directory().ListAsync(cancellationToken)).Count);
    }

    [Fact]
    public async Task TheStaffReadNamesTheAuthorAndFallsBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Comments().SubmitAsync(salmon, _adaIdentifier, "Ada on salmon", cancellationToken);
        await Comments().SubmitAsync(salmon, _benIdentifier, "Ben on salmon", cancellationToken);

        IReadOnlyList<MenuItemComment> all = await Directory().ListAsync(cancellationToken);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, comment => comment.AuthorName == "Ada Lovelace");
        Assert.Contains(all, comment => comment.AuthorName == "ben");
        Assert.All(all, comment => Assert.Equal(salmon, comment.MenuItemIdentifier));
    }

    [Fact]
    public async Task TwoSubmissionsAtOneInstantFoldToTheLater()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Comments().SubmitAsync(salmon, _adaIdentifier, "First", cancellationToken);
        await Comments().SubmitAsync(salmon, _adaIdentifier, "Second", cancellationToken);

        Assert.Equal(2, await World().CountAsync(CountCommentEventsSql, cancellationToken));

        int distinctInstants = await World().CountAsync(
            """
            SELECT count(DISTINCT occurred_at)::int FROM menu_item_comment_event;
            """,
            cancellationToken);

        Assert.Equal(1, distinctInstants);

        MenuItemComment standing = Assert.Single(await Directory().ListAsync(cancellationToken));

        Assert.Equal("Second", standing.Body);
    }

    private async Task<string?> RefusedConstraintAsync(
        Guid menuItemIdentifier,
        string eventType,
        string? body,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString!);
        await connection.OpenAsync(cancellationToken);

        PostgresException refusal = await Assert.ThrowsAsync<PostgresException>(
            async () => await connection.ExecuteAsync(new CommandDefinition(
                InsertCommentEventSql,
                new
                {
                    MenuItemCommentEventIdentifier = _identifiers.Create(),
                    MenuItemIdentifier = menuItemIdentifier,
                    PersonIdentifier = _adaIdentifier,
                    EventType = eventType,
                    Body = body,
                    OccurredAt = _clock.UtcNow,
                },
                cancellationToken: cancellationToken)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refusal.SqlState);

        return refusal.ConstraintName;
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuItemComments Comments() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuItemCommentDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
