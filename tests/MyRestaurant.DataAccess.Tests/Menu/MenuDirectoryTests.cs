using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuDirectory"/> (TECHNICAL_SPECIFICATION §7) against a real
/// PostgreSQL 17 container. Small, because the read side is small — but the first fact is the one that
/// matters: §7 requires a deactivated item to stay on the menu marked unavailable rather than vanish, so
/// a directory that filtered inactive items would quietly break the guest experience the specification
/// asks for, and nothing else would catch it.
/// </summary>
public sealed class MenuDirectoryTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 4, 2, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    public MenuDirectoryTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        await _world.TruncateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task List_ReturnsDeactivatedItemsToo_OrderedByName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await _world!.AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        Guid salmon = await _world.AddMenuItemAsync("Salmon", 18.00m, cancellationToken, isActive: false);
        Guid apple = await _world.AddMenuItemAsync("Apple pie", 3.25m, cancellationToken);

        IReadOnlyList<MenuItemSummary> menu = await Directory().ListAsync(cancellationToken);

        // §7: "the guest sees that the salmon exists and is out, rather than watching it silently vanish".
        Assert.Equal(
            new[] { apple, salmon, soup },
            menu.Select(item => item.MenuItemIdentifier).ToArray());

        Assert.Equal(new[] { "Apple pie", "Salmon", "Soup" }, menu.Select(item => item.Name).ToArray());
        Assert.Equal(new[] { true, false, true }, menu.Select(item => item.IsActive).ToArray());
        Assert.Equal(18.00m, menu.Single(item => item.MenuItemIdentifier == salmon).PriceAmount);
    }

    [Fact]
    public async Task Get_ReadsOneItem_AndAnswersNullForAnUnknownIdentifier()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tea = await _world!.AddMenuItemAsync("Tea", 2.00m, cancellationToken);

        MenuItemSummary? found = await Directory().GetAsync(tea, cancellationToken);

        Assert.NotNull(found);
        Assert.Equal("Tea", found!.Name);
        Assert.Equal(2.00m, found.PriceAmount);
        Assert.True(found.IsActive);
        Assert.Equal(_clock.UtcNow, found.CreatedAt);

        Assert.Null(await Directory().GetAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task AnEmptyMenu_IsAnEmptyList_NotAFailure()
    {
        SkipIfNoContainer();

        Assert.Empty(await Directory().ListAsync(TestContext.Current.CancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuDirectory Directory() => new(_connectionFactory!);
}
