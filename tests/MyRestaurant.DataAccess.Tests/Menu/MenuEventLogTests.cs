using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuEventLog"/> against a real PostgreSQL 17 container —
/// §11.4's "event history per item" and the activity feed beside it.
///
/// <para>Two properties carry the weight. The first is that the stream is <b>complete</b>: §11.4 requires
/// administration to render "the complete stored record everywhere — full event streams … never projected
/// or truncated for the administrator", so the per-item read has no cap and no filter, and
/// <see cref="ListForItem_ReturnsEveryEventOldestFirst"/> asserts that against a stream written by both
/// write services. The second is that the payload columns arrive as stored: §8.2's paired CHECKs make each
/// event type carry a different shape, and a reader that mixed them up would show a price argument the
/// wrong way round —
/// <see cref="ListForItem_CarriesExactlyThePayloadEachEventTypeIsAllowed"/> pins all five.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
public sealed class MenuEventLogTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 19, 11, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;
    private Guid _kitchenIdentifier;

    public MenuEventLogTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _administratorIdentifier = await _world.AddPersonAsync("adam", "Adam Osei", cancellationToken);

        // No display name: the reader must fall back to the username rather than render blank.
        _kitchenIdentifier = await _world.AddPersonAsync("kim", null, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListForItem_ReturnsEveryEventOldestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RenameMenuItemAsync(
            soup, "Soup of the day", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RepriceMenuItemAsync(soup, 5.00m, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Availability().SetActiveAsync(soup, isActive: true, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> history = await Log().ListForItemAsync(soup, cancellationToken);

        Assert.Equal(
            new[] { "created", "name_changed", "price_changed", "deactivated", "activated" },
            history.Select(entry => entry.EventType).ToArray());

        // Oldest first, and every entry is about this item under its current name.
        Assert.Equal(history.OrderBy(entry => entry.OccurredAt).ToArray(), history.ToArray());
        Assert.All(history, entry => Assert.Equal(soup, entry.MenuItemIdentifier));
        Assert.All(history, entry => Assert.Equal("Soup of the day", entry.MenuItemName));
    }

    /// <summary>
    /// §8.2: <c>CHECK ((new_name IS NOT NULL) = (event_type IN ('created', 'name_changed')))</c> and the
    /// matching one for the price. The reader has to carry that shape through unchanged, because it is
    /// what the history means.
    /// </summary>
    [Fact]
    public async Task ListForItem_CarriesExactlyThePayloadEachEventTypeIsAllowed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);
        await Administration().RenameMenuItemAsync(soup, "Broth", _administratorIdentifier, cancellationToken);
        await Administration().RepriceMenuItemAsync(soup, 5.00m, _administratorIdentifier, cancellationToken);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);
        await Availability().SetActiveAsync(soup, isActive: true, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> history = await Log().ListForItemAsync(soup, cancellationToken);

        // LINQ's Single, not Assert.Single's predicate overload: it throws just as loudly when the count
        // is wrong, and it is the same call on every xUnit line this file might be read on.
        MenuItemEventEntry Entry(string eventType) => history.Single(entry => entry.EventType == eventType);

        MenuItemEventEntry created = Entry("created");
        Assert.Equal("Soup", created.NewName);
        Assert.Equal(4.50m, created.NewPriceAmount);

        MenuItemEventEntry renamed = Entry("name_changed");
        Assert.Equal("Broth", renamed.NewName);
        Assert.Null(renamed.NewPriceAmount);

        MenuItemEventEntry repriced = Entry("price_changed");
        Assert.Null(repriced.NewName);
        Assert.Equal(5.00m, repriced.NewPriceAmount);

        foreach (string flip in new[] { "deactivated", "activated" })
        {
            MenuItemEventEntry entry = Entry(flip);
            Assert.Null(entry.NewName);
            Assert.Null(entry.NewPriceAmount);
        }
    }

    /// <summary>
    /// The same rendering rule the counter board uses for whoever closed a sitting: the display name when
    /// there is one, the username when there is not. An audit line that says who did it and then leaves
    /// the name blank is not an audit line.
    /// </summary>
    [Fact]
    public async Task ListForItem_NamesTheActorAndFallsBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> history = await Log().ListForItemAsync(soup, cancellationToken);

        MenuItemEventEntry created = history.Single(entry => entry.EventType == "created");
        Assert.Equal(_administratorIdentifier, created.ActorPersonIdentifier);
        Assert.Equal("Adam Osei", created.ActorName);

        MenuItemEventEntry deactivated = history.Single(entry => entry.EventType == "deactivated");
        Assert.Equal(_kitchenIdentifier, deactivated.ActorPersonIdentifier);
        Assert.Equal("kim", deactivated.ActorName);
    }

    /// <summary>Instants come back as UTC <see cref="DateTimeOffset"/>, not as an unspecified-kind <see cref="DateTime"/>.</summary>
    [Fact]
    public async Task ListForItem_ReadsTheInstantBackAsUtc()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        DateTimeOffset createdAt = _clock.UtcNow;
        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        MenuItemEventEntry created = Assert.Single(await Log().ListForItemAsync(soup, cancellationToken));

        Assert.Equal(createdAt, created.OccurredAt);
        Assert.Equal(TimeSpan.Zero, created.OccurredAt.Offset);
    }

    [Fact]
    public async Task ListForItem_OnlyReturnsThatItemsEvents()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);
        Guid steak = await CreateAsync("Steak", 21.00m, cancellationToken);

        MenuItemEventEntry only = Assert.Single(await Log().ListForItemAsync(soup, cancellationToken));

        Assert.Equal(soup, only.MenuItemIdentifier);
        Assert.NotEqual(steak, only.MenuItemIdentifier);
    }

    [Fact]
    public async Task ListForItem_AnUnknownItemIsEmptyRatherThanAnError()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Empty(await Log().ListForItemAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task ListRecent_IsNewestFirstAcrossItemsAndRespectsTheCap()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        Guid steak = await CreateAsync("Steak", 21.00m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Availability().SetActiveAsync(soup, isActive: false, _kitchenIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> recent = await Log().ListRecentAsync(2, cancellationToken);

        Assert.Equal(2, recent.Count);
        Assert.Equal("deactivated", recent[0].EventType);
        Assert.Equal(soup, recent[0].MenuItemIdentifier);
        Assert.Equal("created", recent[1].EventType);
        Assert.Equal(steak, recent[1].MenuItemIdentifier);

        // Uncapped, the whole feed is there — three events over two items.
        Assert.Equal(3, (await Log().ListRecentAsync(50, cancellationToken)).Count);
    }

    [Fact]
    public async Task ListRecent_ANonPositiveCapReturnsNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await CreateAsync("Soup", 4.50m, cancellationToken);

        Assert.Empty(await Log().ListRecentAsync(0, cancellationToken));
        Assert.Empty(await Log().ListRecentAsync(-5, cancellationToken));
    }

    /// <summary>
    /// The item's name is a read-time join and the event's payload is stored, so a renamed item's history
    /// reads under the name it has now while each entry still says what it was set to then. That is the
    /// distinction that lets somebody follow a rename rather than be confused by it.
    /// </summary>
    [Fact]
    public async Task ListRecent_ShowsTheCurrentItemNameBesideTheNameEachEventSet()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await CreateAsync("Soup", 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RenameMenuItemAsync(soup, "Broth", _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuItemEventEntry> recent = await Log().ListRecentAsync(10, cancellationToken);

        Assert.All(recent, entry => Assert.Equal("Broth", entry.MenuItemName));
        Assert.Equal("Broth", recent[0].NewName);
        Assert.Equal("Soup", recent[1].NewName);
    }

    private async Task<Guid> CreateAsync(string name, decimal price, CancellationToken cancellationToken)
    {
        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuItemAsync(
            identifier, name, price, _administratorIdentifier, cancellationToken);

        return identifier;
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuEventLog Log() => new(_connectionFactory!);

    private DapperMenuAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuAvailability Availability() => new(_connectionFactory!, _clock, _identifiers);
}
