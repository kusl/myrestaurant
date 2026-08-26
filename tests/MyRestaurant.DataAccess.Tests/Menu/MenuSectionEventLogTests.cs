using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuSectionEventLogTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 16, 9, 30, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

    private Guid _managerIdentifier;

    public MenuSectionEventLogTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
        _managerIdentifier = await _world.AddPersonAsync("moe", null, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListForSection_ReturnsEveryEventOldestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().RenameMenuSectionAsync(
            drinks, "Drinks & cordials", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().DescribeMenuSectionAsync(
            drinks, "Served all day", _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().ReorderMenuSectionAsync(
            drinks, 3, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: false, _administratorIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: true, _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuSectionEventEntry> history =
            await Log().ListForSectionAsync(drinks, cancellationToken);

        Assert.Equal(
            new[] { "created", "renamed", "described", "reordered", "deactivated", "activated" },
            history.Select(entry => entry.EventType).ToArray());

        Assert.Equal(history.OrderBy(entry => entry.OccurredAt).ToArray(), history.ToArray());
        Assert.All(history, entry => Assert.Equal(drinks, entry.MenuSectionIdentifier));

        Assert.All(history, entry => Assert.Equal("Drinks & cordials", entry.MenuSectionName));
    }

    [Fact]
    public async Task ListForSection_CarriesExactlyThePayloadEachEventTypeIsAllowed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        await Administration().RenameMenuSectionAsync(
            drinks, "Cordials", _administratorIdentifier, cancellationToken);
        await Administration().DescribeMenuSectionAsync(
            drinks, "Served all day", _administratorIdentifier, cancellationToken);
        await Administration().ReorderMenuSectionAsync(
            drinks, 2, _administratorIdentifier, cancellationToken);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: false, _administratorIdentifier, cancellationToken);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: true, _administratorIdentifier, cancellationToken);

        IReadOnlyList<MenuSectionEventEntry> history =
            await Log().ListForSectionAsync(drinks, cancellationToken);

        MenuSectionEventEntry Entry(string eventType) => history.Single(entry => entry.EventType == eventType);

        MenuSectionEventEntry created = Entry("created");
        Assert.Equal("Drinks", created.NewName);
        Assert.Equal("Cold things", created.NewDescription);
        Assert.Equal(0, created.NewDisplayOrder);

        MenuSectionEventEntry renamed = Entry("renamed");
        Assert.Equal("Cordials", renamed.NewName);
        Assert.Null(renamed.NewDescription);
        Assert.Null(renamed.NewDisplayOrder);

        MenuSectionEventEntry described = Entry("described");
        Assert.Null(described.NewName);
        Assert.Equal("Served all day", described.NewDescription);
        Assert.Null(described.NewDisplayOrder);

        MenuSectionEventEntry reordered = Entry("reordered");
        Assert.Null(reordered.NewName);
        Assert.Null(reordered.NewDescription);
        Assert.Equal(2, reordered.NewDisplayOrder);

        foreach (string activation in new[] { "deactivated", "activated" })
        {
            MenuSectionEventEntry entry = Entry(activation);
            Assert.Null(entry.NewName);
            Assert.Null(entry.NewDescription);
            Assert.Null(entry.NewDisplayOrder);
        }
    }

    [Fact]
    public async Task ListForSection_AClearedDescriptionIsAnEventCarryingTheEmptyString()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        await Administration().DescribeMenuSectionAsync(
            drinks, null, _administratorIdentifier, cancellationToken);

        MenuSectionEventEntry described = (await Log().ListForSectionAsync(drinks, cancellationToken))
            .Single(entry => entry.EventType == "described");

        Assert.Equal(string.Empty, described.NewDescription);
    }

    [Fact]
    public async Task ListForSection_NamesTheActorAndFallsBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", null, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: false, _managerIdentifier, cancellationToken);

        IReadOnlyList<MenuSectionEventEntry> history =
            await Log().ListForSectionAsync(drinks, cancellationToken);

        MenuSectionEventEntry created = history.Single(entry => entry.EventType == "created");
        Assert.Equal(_administratorIdentifier, created.ActorPersonIdentifier);
        Assert.Equal("Adam Osei", created.ActorName);

        MenuSectionEventEntry hidden = history.Single(entry => entry.EventType == "deactivated");
        Assert.Equal(_managerIdentifier, hidden.ActorPersonIdentifier);
        Assert.Equal("moe", hidden.ActorName);
    }

    [Fact]
    public async Task ListForSection_ReturnsNothingFromAnotherHeadingAndNothingForAnUnknownOne()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", null, cancellationToken);
        Guid puddings = await CreateAsync("Puddings", null, cancellationToken);

        await Administration().RenameMenuSectionAsync(
            puddings, "Sweets", _administratorIdentifier, cancellationToken);

        Assert.Single(await Log().ListForSectionAsync(drinks, cancellationToken));
        Assert.Equal(2, (await Log().ListForSectionAsync(puddings, cancellationToken)).Count);

        Assert.Empty(await Log().ListForSectionAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task ListForSection_ShowsNothingForAWriteThatChangedNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid drinks = await CreateAsync("Drinks", "Cold things", cancellationToken);

        await Administration().RenameMenuSectionAsync(
            drinks, "Drinks", _administratorIdentifier, cancellationToken);
        await Administration().DescribeMenuSectionAsync(
            drinks, "Cold things", _administratorIdentifier, cancellationToken);
        await Administration().ReorderMenuSectionAsync(
            drinks, 0, _administratorIdentifier, cancellationToken);
        await Administration().SetMenuSectionActiveAsync(
            drinks, isActive: true, _administratorIdentifier, cancellationToken);

        MenuSectionEventEntry only = Assert.Single(await Log().ListForSectionAsync(drinks, cancellationToken));
        Assert.Equal("created", only.EventType);
    }

    private async Task<Guid> CreateAsync(
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        Guid identifier = _identifiers.Create();
        await Administration().CreateMenuSectionAsync(
            identifier, name, description, _administratorIdentifier, cancellationToken);

        return identifier;
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuSectionEventLog Log() => new(_connectionFactory!);

    private DapperMenuSectionAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);
}
