using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Menu;

/// <summary>
/// Integration tests for <see cref="DapperMenuSectionAdministration.ResequenceMenuSectionsAsync"/> against
/// a real PostgreSQL 17 container — §7's whole-list reordering for the menu's headings, which is the write
/// behind the Up and Down controls on <c>/administration/menu</c>.
///
/// <para><b>Its own class rather than nine more facts on
/// <see cref="MenuSectionAdministrationTests"/>, and the reason is what these tests are about.</b> Every
/// verb over there writes one row and one event, so <em>the newest event for this section</em> is unambiguous and
/// every helper it has is built on that. This verb writes several rows and several events in one
/// transaction at one instant, so the facts worth pinning are about <em>sets</em> and <em>sequences</em> —
/// how many events, which sections got them, and in what order they read back. A file whose helpers assume
/// one event per section per instant is the wrong place for that.</para>
///
/// <para><b>Three facts here are about what is NOT written, and they are the point.</b> A resequence that
/// moves one heading in eight must write two rows, not eight, or §11.4's per-heading history becomes a log
/// of button presses; a resequence into the order already stored must write nothing at all; and a list that
/// is not a permutation of the stored set must be refused whole rather than partially obeyed. Each of those
/// fails silently and leaves a menu order nobody chose, which is the worse of the two failures in an
/// append-only system (ADR-0002).</para>
///
/// <para><see cref="TheEventsOfOneResequenceReadInTheOrderTheRowsWereWritten"/> is the one that could not
/// have been written before Slice 45. All the events of one call carry the same
/// <c>occurred_at</c>, because one transaction stamps every row with one <c>IClock.UtcNow</c> reading, so
/// their order in <c>menu_section_event</c> is decided entirely by the identifier tie-break — the property
/// <b>F-95</b> found nothing was keeping. It is asserted here under the reader's own ordering rather than
/// against raw identifiers, because what matters is what §11.4 renders.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
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

    /// <summary>
    /// The ordering is stored as the list's indices, and the read returns the list.
    /// </summary>
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

    /// <summary>
    /// One event per section that actually moved. Three headings reversed leaves the middle one where it
    /// was, so this writes two <c>reordered</c> events and not three — which is the no-op rule applied per
    /// row rather than per call.
    /// </summary>
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

        // Three creates, then two moves: entrees was at 1 and stays at 1.
        Assert.Equal(5, await World().CountAsync(CountEventsSql, cancellationToken));
        Assert.Equal(2, await World().CountAsync(CountReorderedEventsSql, cancellationToken));

        Assert.Empty(await ReorderedPositionsAsync(entrees, cancellationToken));
        Assert.Equal([0], await ReorderedPositionsAsync(puddings, cancellationToken));
        Assert.Equal([2], await ReorderedPositionsAsync(drinks, cancellationToken));
    }

    /// <summary>
    /// The order it already has. Nothing is written, and the outcome says so rather than reporting a
    /// success — this is what pressing Up on a heading somebody else has already moved up looks like.
    /// </summary>
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

    /// <summary>
    /// A list missing a heading is a page rendered before that heading was created. Refused whole: obeying
    /// the part that still resolves would leave the menu in an order nobody chose.
    /// </summary>
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

    /// <summary>
    /// A list naming a section that is not there — deleted, or never created on this database. Same answer,
    /// because from this verb's side it is the same fact: the list is not the stored set.
    /// </summary>
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

    /// <summary>
    /// A list of the right length whose members all exist and one of which appears twice. It is refused,
    /// and this is the case a length check and a resolution check would both let through — which is why the
    /// permutation test de-duplicates.
    /// </summary>
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

    /// <summary>
    /// Every event of one call carries the same instant and the acting administrator, and they read back in
    /// the order the rows were written rather than in an order the random bits chose (<b>F-95</b>).
    /// </summary>
    [Fact]
    public async Task TheEventsOfOneResequenceReadInTheOrderTheRowsWereWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid drinks, Guid entrees, Guid puddings) = await ThreeSectionsAsync(cancellationToken);

        DateTimeOffset moment = _clock.UtcNow.AddMinutes(5);
        _clock.UtcNow = moment;

        // Rotates all three, so all three move and the write order is puddings, drinks, entrees.
        Assert.Equal(
            ResequenceMenuSectionsOutcome.Resequenced,
            await Administration().ResequenceMenuSectionsAsync(
                [puddings, drinks, entrees], _administratorIdentifier, cancellationToken));

        IReadOnlyList<MenuSectionEventEntry> events = await EventLog()
            .ListForSectionAsync(puddings, cancellationToken);

        MenuSectionEventEntry moved = Assert.Single(
            events.Where(entry => string.Equals(entry.EventType, "reordered", StringComparison.Ordinal)));

        Assert.Equal(moment, moved.OccurredAt);
        Assert.Equal(_administratorIdentifier, moved.ActorPersonIdentifier);

        IReadOnlyList<Guid> written = await ReorderedSectionsInReadOrderAsync(moment, cancellationToken);

        Assert.Equal([puddings, drinks, entrees], written);
    }

    /// <summary>
    /// Positions are permitted to be equal and are not required to be contiguous, which is the whole reason
    /// this verb exists rather than an absolute write per heading. Two headings sharing position 0 have an
    /// order nobody assigned; a resequence gives them one, and the read agrees with the list rather than
    /// with the name tie-break that used to decide it.
    /// </summary>
    [Fact]
    public async Task ResequencingSeparatesTwoHeadingsThatSharedAPosition()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid zebra = await CreateAsync("Zebra", cancellationToken);
        Guid apple = await CreateAsync("Apple", cancellationToken);

        // Both at 0: the name tie-break puts Apple first, and no single absolute write can say otherwise.
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

    /// <summary>
    /// Three headings at 0, 1, 2 in the order they were created.
    /// </summary>
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

    /// <summary>The stored order is what it was, and no <c>reordered</c> event exists at all.</summary>
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

    /// <summary>
    /// The positions one section's <c>reordered</c> events recorded, oldest first — read through the log
    /// reader rather than by hand, so the assertion is about what §11.4 renders.
    /// </summary>
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

    /// <summary>
    /// Which sections were reordered at one instant, in the order <c>(occurred_at,
    /// menu_section_event_identifier)</c> puts them — the ordering every §11.4 history reads under.
    /// </summary>
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
