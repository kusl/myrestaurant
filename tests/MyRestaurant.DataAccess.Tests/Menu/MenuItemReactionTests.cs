using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

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

    [Fact]
    public async Task TheCountExcludesAPersonWhoUnliked()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(salmon, _benIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: false, cancellationToken);

        Assert.Equal(3, await World().CountAsync(CountReactionEventsSql, cancellationToken));

        IReadOnlyList<MenuItemLikeCount> counts = await Directory()
            .ListLikeCountsAsync(cancellationToken);

        MenuItemLikeCount only = Assert.Single(counts);
        Assert.Equal(salmon, only.MenuItemIdentifier);
        Assert.Equal(1, only.LikeCount);
    }

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

    [Fact]
    public async Task TwoPressesAtOneInstantFoldToTheLater()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid salmon = await World().AddMenuItemAsync("Salmon", 18.00m, cancellationToken);

        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: true, cancellationToken);
        await Reactions().SetLikedAsync(salmon, _adaIdentifier, isLiked: false, cancellationToken);

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
