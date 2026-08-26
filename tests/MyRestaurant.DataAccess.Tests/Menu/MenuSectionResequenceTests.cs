using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

public sealed class MenuSectionResequenceTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string CountEventsSql = """
        SELECT count(*)::int FROM menu_section_event;
        """;

    private const string CountReorderedEventsSql = """
        SELECT count(*)::int FROM menu_section_event WHERE event_type = 'reordered';
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 18, 16, 45, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _administratorIdentifier;

    public MenuSectionResequenceTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _administratorIdentifier = await _world.AddPersonAsync("adam", "Adam", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ResequencingAssignsPositionsFromThePlaceInTheList()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        Assert.Equal(
            ResequenceMenuSectionsOutcome.Resequenced,
            await Administration().ResequenceMenuSectionsAsync(
                [puddings, drinks, entrees], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuSectionSummary> stored = await Directory().ListAsync(cancellationToken);

        Assert.Equal(
            [puddings, drinks, entrees],
            stored.Select(summary => summary.MenuSectionIdentifier));

        Assert.Equal([0, 1, 2], stored.Select(summary => summary.DisplayOrder));
    }

    [Fact]
    public async Task OnlyTheSectionsThatMovedGetAnEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal(
            ResequenceMenuSectionsOutcome.Resequenced,
            await Administration().ResequenceMenuSectionsAsync(
                [puddings, entrees, drinks], _administratorIdentifier, cancellationToken));

        Assert.Equal(5, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(2, await World().CountAsync(CountReorderedEventsSql, cancellationToken));

        Assert.Empty(await ReorderedPositionsAsync(entrees, cancellationToken));
        Assert.Equal([0], await ReorderedPositionsAsync(puddings, cancellationToken));
        Assert.Equal([2], await ReorderedPositionsAsync(drinks, cancellationToken));
    }

    [Fact]
    public async Task ResequencingIntoTheStoredOrderIsANoOpAndWritesNoEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        Assert.Equal(
            ResequenceMenuSectionsOutcome.NoChange,
            await Administration().ResequenceMenuSectionsAsync(
                [drinks, entrees, puddings], _administratorIdentifier, cancellationToken));

        Assert.Equal(3, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountReorderedEventsSql, cancellationToken));
    }

    [Fact]
    public async Task AListThatIsNotTheWholeSetIsRefusedAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        Assert.Equal(
            ResequenceMenuSectionsOutcome.MenuSectionSetChanged,
            await Administration().ResequenceMenuSectionsAsync(
                [puddings, drinks], _administratorIdentifier, cancellationToken));

        await AssertNothingMovedAsync(drinks, entrees, puddings, cancellationToken);
    }

    [Fact]
    public async Task AListNamingASectionThatDoesNotExistIsRefusedAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        Assert.Equal(
            ResequenceMenuSectionsOutcome.MenuSectionSetChanged,
            await Administration().ResequenceMenuSectionsAsync(
                [puddings, drinks, _identifiers.Create()], _administratorIdentifier, cancellationToken));

        await AssertNothingMovedAsync(drinks, entrees, puddings, cancellationToken);
    }

    [Fact]
    public async Task AListWithARepeatedSectionIsRefusedAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        Assert.Equal(
            ResequenceMenuSectionsOutcome.MenuSectionSetChanged,
            await Administration().ResequenceMenuSectionsAsync(
                [puddings, drinks, drinks], _administratorIdentifier, cancellationToken));

        await AssertNothingMovedAsync(drinks, entrees, puddings, cancellationToken);
    }

    [Fact]
    public async Task TheEventsOfOneResequenceReadInTheOrderTheRowsWereWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        DateTimeOffset moment = _clock.UtcNow.AddMinutes(5);
        _clock.UtcNow = moment;

        Assert.Equal(
            ResequenceMenuSectionsOutcome.Resequenced,
            await Administration().ResequenceMenuSectionsAsync(
                [puddings, drinks, entrees], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuSectionEventEntry> events = await EventLog()
            .ListForSectionAsync(puddings, cancellationToken);

        MenuSectionEventEntry moved = Assert.Single(
            events,
            entry => string.Equals(entry.EventType, "reordered", StringComparison.Ordinal));

        Assert.Equal(moment, moved.OccurredAt);
        Assert.Equal(_administratorIdentifier, moved.ActorPersonIdentifier);

        IReadOnlyList<Guid> written = await ReorderedSectionsInReadOrderAsync(moment, cancellationToken);

        Assert.Equal([puddings, drinks, entrees], written);
    }

    [Fact]
    public async Task ResequencingSeparatesTwoHeadingsThatSharedAPosition()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid zebra = await CreateAsync("Zebra", cancellationToken);
        Guid apple = await CreateAsync("Apple", cancellationToken);

        Assert.Equal(
            ReorderMenuSectionOutcome.Reordered,
            await Administration().ReorderMenuSectionAsync(
                apple, 0, _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuSectionSummary> tied = await Directory().ListAsync(cancellationToken);
        Assert.Equal([apple, zebra], tied.Select(summary => summary.MenuSectionIdentifier));
        Assert.Equal([0, 0], tied.Select(summary => summary.DisplayOrder));

        Assert.Equal(
            ResequenceMenuSectionsOutcome.Resequenced,
            await Administration().ResequenceMenuSectionsAsync(
                [zebra, apple], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuSectionSummary> ordered = await Directory().ListAsync(cancellationToken);
        Assert.Equal([zebra, apple], ordered.Select(summary => summary.MenuSectionIdentifier));
        Assert.Equal([0, 1], ordered.Select(summary => summary.DisplayOrder));
    }

    private async Task<(Guid Drinks, Guid Entrees, Guid Puddings)> ThreeSectionsAsync(
        CancellationToken cancellationToken)
        => (await CreateAsync("Drinks", cancellationToken),
            await CreateAsync("Entrees", cancellationToken),
            await CreateAsync("Puddings", cancellationToken));

    private async Task<Guid> CreateAsync(string name, CancellationToken cancellationToken)
    {
        Guid identifier = _identifiers.Create();

        await Administration().CreateMenuSectionAsync(
            identifier, name, null, _administratorIdentifier, cancellationToken);

        return identifier;
    }

    private async Task AssertNothingMovedAsync(
        Guid first,
        Guid second,
        Guid third,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MenuSectionSummary> stored = await Directory().ListAsync(cancellationToken);

        Assert.Equal([first, second, third], stored.Select(summary => summary.MenuSectionIdentifier));
        Assert.Equal([0, 1, 2], stored.Select(summary => summary.DisplayOrder));
        Assert.Equal(0, await World().CountAsync(CountReorderedEventsSql, cancellationToken));
    }

    private async Task<IReadOnlyList<int>> ReorderedPositionsAsync(
        Guid menuSectionIdentifier,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MenuSectionEventEntry> events = await EventLog()
            .ListForSectionAsync(menuSectionIdentifier, cancellationToken);

        return
        [
            .. events
                .Where(entry => string.Equals(entry.EventType, "reordered", StringComparison.Ordinal))
                .Select(entry => entry.NewDisplayOrder ?? -1),
        ];
    }

    private async Task<IReadOnlyList<Guid>> ReorderedSectionsInReadOrderAsync(
        DateTimeOffset moment,
        CancellationToken cancellationToken)
        => await World().QueryAsync<Guid>(
            """
            SELECT menu_section_identifier
            FROM menu_section_event
            WHERE event_type = 'reordered' AND occurred_at = @Moment
            ORDER BY occurred_at, menu_section_event_identifier;
            """,
            new { Moment = moment },
            cancellationToken);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperMenuSectionAdministration Administration() => new(_connectionFactory!, _clock, _identifiers);

    private DapperMenuSectionDirectory Directory() => new(_connectionFactory!);

    private DapperMenuSectionEventLog EventLog() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
