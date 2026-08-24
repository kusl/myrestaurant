using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuItemReactions"/> and
/// <see cref="DapperMenuItemReactionDirectory"/> against a real PostgreSQL 17 container — Stage 5a's
/// like, and the fold that answers what anybody currently thinks (§7, §8.2, §8.3).
///
/// <para><b>What earns a fact here is a plausible wrong implementation that leaves an artefact reading
/// correctly.</b> That is this repository's standing rule for an append-only table (ADR-0002), and this
/// one has more of them than usual because the obvious schema is not an event table at all: a row per
/// like with a <c>DELETE</c> for unliking gets every visible answer right and destroys the record. Three
/// of the nine below fail against exactly that implementation.</para>
///
/// <para><b>The one that could not have been written any other way is
/// <see cref="TwoPressesAtOneInstantFoldToTheLater"/>.</b> One transaction stamps its rows with one
/// <see cref="Domain.Time.IClock.UtcNow"/> (§8.1) and this fixture's clock is fixed, so a like and an
/// unlike genuinely share an <c>occurred_at</c> — which is not a contrivance here the way it would be on
/// <c>order_visibility_event</c>. Nobody hides an order twice in one millisecond; everybody taps a heart
/// twice. <c>DISTINCT ON</c> with no tie-break returns whichever row the scan reached first, which is the
/// <em>oldest</em>, so the double-tap would read back as the state before it. The tie-break is only an
/// answer because §8.1 requires <see cref="IIdentifierFactory"/> to ascend inside a millisecond, which is
/// the property F-95 found nothing was keeping.</para>
///
/// <para><b><c>OrderTestWorld</c> needed no edit</b>, on the property <c>0008</c> was cut for:
/// <c>TRUNCATE … CASCADE</c> on <c>menu_item</c> and <c>person</c> reaches this table, because both of
/// its references are real foreign keys.</para>
/// </summary>
public sealed class MenuItemReactionTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountReactionEventsSql = """
        SELECT count(*)::int FROM menu_item_reaction_event;
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 5, 14, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _adaIdentifier;
    private Guid _benIdentifier;

    public MenuItemReactionTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        // Three characters minimum: person.username has carried
        // CHECK (char_length(username) BETWEEN 3 AND 64) since 0001, and a two-character name fails in
        // InitializeAsync where the message names the fixture rather than any test (F-85).
        _adaIdentifier = await _world.AddPersonAsync("ada", "Ada", cancellationToken);
        _benIdentifier = await _world.AddPersonAsync("ben", "Ben", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task LikingWritesOneEventAndTheFoldSaysLiked()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        SetMenuItemReactionResult result = await Reactions()
            .SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);

        Assert.Equal(SetMenuItemReactionOutcome.Changed, result.Outcome);
        Assert.True(result.Changed);
        Assert.True(result.ItemExists);
        Assert.True(result.IsLiked);
        Assert.Equal(salmon, result.MenuItemIdentifier);
        Assert.Equal(_adaIdentifier, result.PersonIdentifier);

        Assert.Equal(1, await World().CountAsync(CountReactionEventsSql, cancellationToken));

        string? eventType = await World().ScalarAsync<string>(
            """
            SELECT event_type
            FROM menu_item_reaction_event
            WHERE menu_item_identifier = @MenuItemIdentifier
              AND person_identifier = @PersonIdentifier;
            """,
            new { MenuItemIdentifier = salmon, PersonIdentifier = _adaIdentifier },
            cancellationToken);

        Assert.Equal("liked", eventType);

        IReadOnlyList<Guid> liked = await Directory()
            .ListLikedByAsync(_adaIdentifier, cancellationToken);

        Assert.Equal(salmon, Assert.Single(liked));
    }

    /// <summary>
    /// The no-op rule, and on this table it governs the <em>ordinary</em> gesture rather than an edge
    /// case: a control that is already on, pressed again, is a double-tap. An implementation that
    /// appended every press would leave §11.4 reading a log of thumbs rather than of opinions.
    /// </summary>
    [Fact]
    public async Task LikingSomethingAlreadyLikedWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);

        SetMenuItemReactionResult again = await Reactions()
            .SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);

        Assert.Equal(SetMenuItemReactionOutcome.AlreadyInThatState, again.Outcome);
        Assert.False(again.Changed);
        Assert.True(again.ItemExists);
        Assert.True(again.IsLiked);

        Assert.Equal(1, await World().CountAsync(CountReactionEventsSql, cancellationToken));
    }

    /// <summary>
    /// Unliking <b>appends</b>. This is the fact that fails against the schema Stage 5a rejected — one
    /// row per like with a <c>DELETE</c> to withdraw it — which gets the fold right and leaves nothing
    /// behind to audit (R§6.8, ADR-0002).
    /// </summary>
    [Fact]
    public async Task UnlikingAppendsASecondEventAndReversesTheFold()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);

        SetMenuItemReactionResult undone = await Reactions()
            .SetLikedAsync(salmon, _adaIdentifier, isLiked: false, cancellationToken);

        Assert.Equal(SetMenuItemReactionOutcome.Changed, undone.Outcome);
        Assert.False(undone.IsLiked);

        Assert.Equal(2, await World().CountAsync(CountReactionEventsSql, cancellationToken));
        Assert.Empty(await Directory().ListLikedByAsync(_adaIdentifier, cancellationToken));
    }

    /// <summary>
    /// Absence and <c>'unliked'</c> are one state. A person who has never pressed anything has no row in
    /// the fold, and unliking is therefore a no-op rather than a fourth outcome — the alternative writes
    /// a withdrawal of an opinion never held, which is a row §11.4 would render as a sentence about
    /// somebody changing their mind.
    /// </summary>
    [Fact]
    public async Task UnlikingSomethingNeverLikedWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        SetMenuItemReactionResult result = await Reactions()
            .SetLikedAsync(salmon, _adaIdentifier, isLiked: false, cancellationToken);

        Assert.Equal(SetMenuItemReactionOutcome.AlreadyInThatState, result.Outcome);
        Assert.False(result.Changed);
        Assert.True(result.ItemExists);
        Assert.False(result.IsLiked);

        Assert.Equal(0, await World().CountAsync(CountReactionEventsSql, cancellationToken));
    }

    [Fact]
    public async Task AnUnknownItemReportsNotFoundAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SetMenuItemReactionResult result = await Reactions()
            .SetLikedAsync(_identifiers.Create(), _adaIdentifier, isLiked: true, cancellationToken);

        Assert.Equal(SetMenuItemReactionOutcome.MenuItemNotFound, result.Outcome);
        Assert.False(result.ItemExists);
        Assert.False(result.Changed);
        Assert.False(result.IsLiked);

        Assert.Equal(0, await World().CountAsync(CountReactionEventsSql, cancellationToken));
    }

    /// <summary>
    /// The count is of <b>people</b>, not of presses. The plausible wrong reader counts <c>'liked'</c>
    /// rows in the event table, which is right until the first person changes their mind and then
    /// permanently wrong in the direction that flatters the dish.
    /// </summary>
    [Fact]
    public async Task TheCountExcludesAPersonWhoUnliked()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(salmon, _benIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: false, cancellationToken);

        // Three rows on the table, one person currently liking it. A reader over the event table would
        // answer two.
        Assert.Equal(3, await World().CountAsync(CountReactionEventsSql, cancellationToken));

        IReadOnlyList<MenuItemLikeCount> counts = await Directory()
            .ListLikeCountsAsync(cancellationToken);

        MenuItemLikeCount only = Assert.Single(counts);
        Assert.Equal(salmon, only.MenuItemIdentifier);
        Assert.Equal(1, only.LikeCount);
    }

    /// <summary>
    /// A dish nobody currently likes is <b>absent</b> rather than present with a zero, which is the
    /// contract §11.4's caller reads against — it already holds the menu and is asking which of it is
    /// liked. Asserted with a second dish in the database so that "absent" is a decision the query made
    /// rather than the only row there was.
    /// </summary>
    [Fact]
    public async Task ADishNobodyLikesIsAbsentFromTheCounts()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);
        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(soup, _benIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(soup, _benIdentifier, isLiked: false, cancellationToken);

        IReadOnlyList<MenuItemLikeCount> counts = await Directory()
            .ListLikeCountsAsync(cancellationToken);

        MenuItemLikeCount only = Assert.Single(counts);
        Assert.Equal(salmon, only.MenuItemIdentifier);
        Assert.DoesNotContain(counts, count => count.MenuItemIdentifier == soup);
    }

    /// <summary>
    /// One person's list contains only their own presses. The wrong reader here is one that forgets the
    /// <c>person_identifier</c> filter and returns everything anybody likes — which on a fresh fixture
    /// with one person is indistinguishable from the right answer, and is why there are two people in
    /// this class.
    /// </summary>
    [Fact]
    public async Task OnePersonsLikesDoNotAppearInAnothersList()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);
        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(soup, _benIdentifier, isLiked: true, cancellationToken);

        IReadOnlyList<Guid> adasLikes = await Directory()
            .ListLikedByAsync(_adaIdentifier, cancellationToken);
        IReadOnlyList<Guid> bensLikes = await Directory()
            .ListLikedByAsync(_benIdentifier, cancellationToken);

        Assert.Equal(salmon, Assert.Single(adasLikes));
        Assert.Equal(soup, Assert.Single(bensLikes));
        Assert.Empty(await Directory().ListLikedByAsync(_identifiers.Create(), cancellationToken));
    }

    /// <summary>
    /// The fold's tie-break, which is the fact this class exists for. Both rows carry the same
    /// <c>occurred_at</c> because <see cref="FixedClock"/> does not move and §8.1 stamps one instant per
    /// transaction, so the only thing separating them is
    /// <c>menu_item_reaction_event_identifier DESC</c>. Deliberately two presses rather than three: with
    /// two, a fold ordered by <c>occurred_at</c> alone returns the <em>first</em> row — <c>'liked'</c> —
    /// and the assertion below fails definitely rather than by luck of a scan order.
    /// </summary>
    [Fact]
    public async Task TwoPressesAtOneInstantFoldToTheLater()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: false, cancellationToken);

        // The arrangement is the assertion's premise, so it is checked rather than assumed: two rows,
        // one instant.
        Assert.Equal(2, await World().CountAsync(CountReactionEventsSql, cancellationToken));

        int distinctInstants = await World().CountAsync(
            """
            SELECT count(DISTINCT occurred_at)::int FROM menu_item_reaction_event;
            """,
            cancellationToken);

        Assert.Equal(1, distinctInstants);

        Assert.Empty(await Directory().ListLikedByAsync(_adaIdentifier, cancellationToken));
        Assert.Empty(await Directory().ListLikeCountsAsync(cancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuItemReactions Reactions() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuItemReactionDirectory Directory() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
