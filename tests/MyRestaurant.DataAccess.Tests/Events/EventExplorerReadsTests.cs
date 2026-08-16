using MyRestaurant.DataAccess.Events;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Events;

/// <summary>
/// Integration tests for <see cref="DapperEventExplorerReads"/> against a real PostgreSQL 17 container —
/// TECHNICAL_SPECIFICATION §11.4's event explorer: "filter security/order/menu events by subject, actor,
/// type, and time".
///
/// <para>The tests with teeth are the ones about what the union does <em>not</em> drop. A sixteen-column
/// <c>UNION ALL</c> over three unrelated tables has exactly one interesting failure mode — a branch that
/// quietly stops contributing — and it does not throw. The security branch is the sharpest case: its
/// actor join is the only LEFT one in the statement, because
/// <c>security_event.actor_person_identifier</c> is the only nullable actor column in the three tables
/// (§8.2), and an INNER join there would silently hide every lockout and every failed sign-in from the
/// one screen an administrator opens to look for them.</para>
///
/// <para>Arrangement writes <c>security_event</c> and <c>menu_item_event</c> rows through
/// <see cref="OrderTestWorld"/> rather than through <c>DapperSecurityEventLog</c> and the menu services,
/// for the reason that class already documents: a bug in a writer must not look like a bug in the
/// reader. Order events do go through <see cref="DapperOrderMutations"/>, because §6.6's transaction is
/// the only thing that assigns a <c>sequence_number</c>, and a hand-written row would be asserting
/// against arrangement rather than against the system.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; the container-dependent tests skip when no container engine
/// is available, and the three catalogue tests at the end need no container at all.</para>
/// </summary>
public sealed class EventExplorerReadsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const int Cap = 100;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 2, 17, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _adaIdentifier;
    private Guid _miraIdentifier;
    private Guid _cassIdentifier;

    /// <summary>A person with no display name — the username fallback every staff surface uses.</summary>
    private Guid _patIdentifier;

    private Guid _soupIdentifier;
    private Guid _steakIdentifier;

    public EventExplorerReadsTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        // Three characters minimum: person.username carries CHECK (char_length BETWEEN 3 AND 64) (§8.2).
        _adaIdentifier = await _world.AddPersonAsync("ada", "Ada Lovelace", cancellationToken);
        _miraIdentifier = await _world.AddPersonAsync("mira", "Mira Adeyemi", cancellationToken);
        _cassIdentifier = await _world.AddPersonAsync("cass", "Cass Okonkwo", cancellationToken);
        _patIdentifier = await _world.AddPersonAsync("pat", null, cancellationToken);

        _soupIdentifier = await _world.AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        _steakIdentifier = await _world.AddMenuItemAsync("Steak", 21.00m, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    // ---- all three streams, one list --------------------------------------------------------------

    /// <summary>
    /// The whole point of the screen: one list, three tables, newest first. If any branch of the union
    /// stops contributing this is the test that notices, and it notices by counting rather than by
    /// throwing — which is the only way a missing branch ever announces itself.
    /// </summary>
    [Fact]
    public async Task Everything_InterleavesTheThreeStreams_NewestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _adaIdentifier, _miraIdentifier, SecurityEventType.RoleGranted, cancellationToken);

        Advance(TimeSpan.FromMinutes(5));
        await SendSoupAsync("Table 1", _adaIdentifier, cancellationToken);

        Advance(TimeSpan.FromMinutes(5));
        await World().AddMenuItemEventAsync(
            _soupIdentifier, _miraIdentifier, "price_changed", null, 5.25m, cancellationToken);

        IReadOnlyList<ExplorerEvent> events =
            await Reads().ListAsync(EventExplorerFilter.Everything, Cap, cancellationToken);

        Assert.Equal(3, events.Count);
        Assert.Equal(EventStream.Menu, events[0].Stream);
        Assert.Equal(EventStream.Order, events[1].Stream);
        Assert.Equal(EventStream.Security, events[2].Stream);
    }

    /// <summary>Each stream can be asked for on its own, and asking excludes the other two.</summary>
    [Fact]
    public async Task Streams_CanBeSelectedOneAtATime()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await OneOfEachAsync(cancellationToken);

        Assert.Equal(
            EventStream.Security,
            Assert.Single(await Reads().ListAsync(SecurityOnly, Cap, cancellationToken)).Stream);

        Assert.Equal(
            EventStream.Order,
            Assert.Single(await Reads().ListAsync(OrderOnly, Cap, cancellationToken)).Stream);

        Assert.Equal(
            EventStream.Menu,
            Assert.Single(await Reads().ListAsync(MenuOnly, Cap, cancellationToken)).Stream);
    }

    /// <summary>
    /// Two streams at once — the case a per-stream reader could never answer in one ordered list, and the
    /// reason this reader exists at all.
    /// </summary>
    [Fact]
    public async Task Streams_CanBeCombined()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await OneOfEachAsync(cancellationToken);

        IReadOnlyList<ExplorerEvent> events = await Reads().ListAsync(
            new EventExplorerFilter(IncludeMenuEvents: false), Cap, cancellationToken);

        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, entry => entry.Stream == EventStream.Menu);
    }

    /// <summary>
    /// No stream selected asks for nothing and gets nothing, without a round trip. The surface never
    /// produces this — an empty checkbox set reads as "everything" before a filter is built — but the
    /// type admits it, so the reader answers it rather than returning the whole restaurant.
    /// </summary>
    [Fact]
    public async Task Streams_NoneSelected_ReturnsNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await OneOfEachAsync(cancellationToken);

        EventExplorerFilter nothing = new(
            IncludeSecurityEvents: false, IncludeOrderEvents: false, IncludeMenuEvents: false);

        Assert.True(nothing.IncludesNoStream);
        Assert.Empty(await Reads().ListAsync(nothing, Cap, cancellationToken));
    }

    // ---- what each stream carries -----------------------------------------------------------------

    /// <summary>
    /// A security event, whole: the subject named and its username beside it, the actor named, the stored
    /// type untranslated, and every per-stream member that does not apply left null rather than filled in
    /// with something plausible.
    /// </summary>
    [Fact]
    public async Task Security_CarriesSubjectActorAndTheStoredTypeUntranslated()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        DateTimeOffset writtenAt = _clock.UtcNow;
        Guid eventIdentifier = await World().AddSecurityEventAsync(
            _adaIdentifier, _miraIdentifier, SecurityEventType.RoleGranted, cancellationToken);

        ExplorerEvent entry = Assert.Single(await Reads().ListAsync(SecurityOnly, Cap, cancellationToken));

        Assert.Equal(EventStream.Security, entry.Stream);
        Assert.Equal(eventIdentifier, entry.EventIdentifier);
        Assert.Equal(SecurityEventType.RoleGranted, entry.EventType);
        Assert.Equal(writtenAt, entry.OccurredAt);
        Assert.Equal(_adaIdentifier, entry.SubjectIdentifier);
        Assert.Equal("Ada Lovelace", entry.SubjectLabel);
        Assert.Equal("ada", entry.SubjectDetail);
        Assert.Equal(_miraIdentifier, entry.ActorIdentifier);
        Assert.Equal("Mira Adeyemi", entry.ActorName);

        // Everything that belongs to another stream stays empty.
        Assert.Null(entry.ContextIdentifier);
        Assert.Null(entry.ActorRole);
        Assert.Null(entry.SequenceNumber);
        Assert.Null(entry.NewName);
        Assert.Null(entry.NewPriceAmount);
    }

    /// <summary>
    /// The one nullable actor in the three tables (§8.2: NULL means the subject acted on themselves, or
    /// the system did — a lockout, a failed sign-in). The join to <c>person</c> is LEFT for exactly this
    /// row, and an INNER one would hide every lockout in the restaurant from the screen built to find
    /// them.
    /// </summary>
    [Fact]
    public async Task Security_WithNoActor_KeepsTheRowAndReportsNoActor()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _adaIdentifier, null, SecurityEventType.AccountLockedOut, cancellationToken);

        ExplorerEvent entry = Assert.Single(await Reads().ListAsync(SecurityOnly, Cap, cancellationToken));

        Assert.Equal(SecurityEventType.AccountLockedOut, entry.EventType);
        Assert.Equal(_adaIdentifier, entry.SubjectIdentifier);
        Assert.Null(entry.ActorIdentifier);
        Assert.Null(entry.ActorName);
    }

    /// <summary>
    /// An order event, whole: the subject is the order, labelled by whose it is and which table, carrying
    /// the sitting so the row can link to the record that holds it, plus §6.6's sequence number and the
    /// capacity the actor acted in.
    /// </summary>
    [Fact]
    public async Task Order_CarriesOwnerTableSittingSequenceAndRole()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 7", cancellationToken, _adaIdentifier);
        Guid orderIdentifier = await SendAsync(
            sittingIdentifier, _adaIdentifier, _soupIdentifier, 2, cancellationToken);

        ExplorerEvent entry = Assert.Single(await Reads().ListAsync(OrderOnly, Cap, cancellationToken));

        Assert.Equal(EventStream.Order, entry.Stream);
        Assert.Equal("guest_submission", entry.EventType);
        Assert.Equal(orderIdentifier, entry.SubjectIdentifier);
        Assert.Equal("Ada Lovelace", entry.SubjectLabel);
        Assert.Equal("Table 7", entry.SubjectDetail);
        Assert.Equal(sittingIdentifier, entry.ContextIdentifier);
        Assert.Equal(_adaIdentifier, entry.ActorIdentifier);
        Assert.Equal("Ada Lovelace", entry.ActorName);
        Assert.Equal("guest", entry.ActorRole);
        Assert.Equal(1L, entry.SequenceNumber);

        Assert.Null(entry.NewName);
        Assert.Null(entry.NewPriceAmount);
    }

    /// <summary>
    /// Two orders in one sitting are two subjects, not one. The explorer's subject for an order event is
    /// the order — which is what makes "who did what" answerable when a party of four are all sending.
    /// </summary>
    [Fact]
    public async Task Order_TwoOrdersInOneSitting_AreTwoSubjectsSharingOneSitting()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync(
            "Table 8", cancellationToken, _adaIdentifier, _cassIdentifier);

        Guid adaOrder = await SendAsync(
            sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, cancellationToken);

        Advance(TimeSpan.FromMinutes(1));
        Guid cassOrder = await SendAsync(
            sittingIdentifier, _cassIdentifier, _steakIdentifier, 1, cancellationToken);

        IReadOnlyList<ExplorerEvent> events = await Reads().ListAsync(OrderOnly, Cap, cancellationToken);

        Assert.Equal(2, events.Count);
        Assert.Equal(cassOrder, events[0].SubjectIdentifier);
        Assert.Equal(adaOrder, events[1].SubjectIdentifier);
        Assert.All(events, entry => Assert.Equal(sittingIdentifier, entry.ContextIdentifier));
    }

    /// <summary>
    /// A menu event, whole, including the typed payload columns §8.2's paired CHECKs allow each type. The
    /// price comes back as a number rather than as text: formatting it is the surface's job and depends on
    /// <c>RESTAURANT_CURRENCY_CODE</c> (§13), which the data layer has no business knowing.
    /// </summary>
    [Fact]
    public async Task Menu_CarriesTheItemNameAndTheTypedPayload()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddMenuItemEventAsync(
            _soupIdentifier, _miraIdentifier, "created", "Soup", 4.50m, cancellationToken);

        Advance(TimeSpan.FromMinutes(1));
        await World().AddMenuItemEventAsync(
            _soupIdentifier, _miraIdentifier, "price_changed", null, 5.25m, cancellationToken);

        IReadOnlyList<ExplorerEvent> events = await Reads().ListAsync(MenuOnly, Cap, cancellationToken);

        Assert.Equal(2, events.Count);

        ExplorerEvent repriced = events[0];
        Assert.Equal("price_changed", repriced.EventType);
        Assert.Equal(_soupIdentifier, repriced.SubjectIdentifier);
        Assert.Equal("Soup", repriced.SubjectLabel);
        Assert.Null(repriced.SubjectDetail);
        Assert.Null(repriced.ContextIdentifier);
        Assert.Equal(_miraIdentifier, repriced.ActorIdentifier);
        Assert.Equal("Mira Adeyemi", repriced.ActorName);
        Assert.Null(repriced.ActorRole);
        Assert.Null(repriced.SequenceNumber);
        Assert.Null(repriced.NewName);
        Assert.Equal(5.25m, repriced.NewPriceAmount);

        ExplorerEvent created = events[1];
        Assert.Equal("created", created.EventType);
        Assert.Equal("Soup", created.NewName);
        Assert.Equal(4.50m, created.NewPriceAmount);
    }

    /// <summary>
    /// A person with no display name is named by their username, as subject and as actor — the same
    /// fallback every other staff-facing reader uses, so two screens never call the same person two
    /// different things.
    /// </summary>
    [Fact]
    public async Task NamelessPerson_ReadsUnderTheirUsername_AsSubjectAndAsActor()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _patIdentifier, _patIdentifier, SecurityEventType.PasswordChanged, cancellationToken);

        ExplorerEvent entry = Assert.Single(await Reads().ListAsync(SecurityOnly, Cap, cancellationToken));

        Assert.Equal("pat", entry.SubjectLabel);
        Assert.Equal("pat", entry.SubjectDetail);
        Assert.Equal("pat", entry.ActorName);
    }

    // ---- the four filters §11.4 names --------------------------------------------------------------

    /// <summary>
    /// Subject means something different in each stream, and the filter has to reach all three: a person
    /// (by username or display name), an order (by its owner or its table), and a menu item (by name).
    /// </summary>
    [Fact]
    public async Task SubjectFilter_ReachesPeopleTablesAndItems()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _patIdentifier, _miraIdentifier, SecurityEventType.AccountCreated, cancellationToken);

        Advance(TimeSpan.FromMinutes(1));
        await SendSoupAsync("Terrace 2", _adaIdentifier, cancellationToken);

        Advance(TimeSpan.FromMinutes(1));
        await World().AddMenuItemEventAsync(
            _steakIdentifier, _miraIdentifier, "activated", null, null, cancellationToken);

        // By username, for somebody with no display name at all.
        Assert.Equal(
            EventStream.Security,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "pat"), Cap, cancellationToken)).Stream);

        // By the order owner's display name.
        Assert.Equal(
            EventStream.Order,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "Lovelace"), Cap, cancellationToken)).Stream);

        // By the table the sitting is on — the thing an administrator actually remembers.
        Assert.Equal(
            EventStream.Order,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "Terrace"), Cap, cancellationToken)).Stream);

        // By the item's name.
        Assert.Equal(
            EventStream.Menu,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "steak"), Cap, cancellationToken)).Stream);
    }

    /// <summary>
    /// <c>%</c>, <c>_</c> and <c>\</c> are matched literally. Without the escaping, searching for the
    /// username <c>a_b</c> would also find <c>axb</c> — two different people, silently conflated on the
    /// audit screen.
    /// </summary>
    [Fact]
    public async Task SubjectFilter_TreatsLikeWildcardsAsLiteralCharacters()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid underscored = await World().AddPersonAsync("a_b", null, cancellationToken);
        Guid similar = await World().AddPersonAsync("axb", null, cancellationToken);

        await World().AddSecurityEventAsync(
            underscored, null, SecurityEventType.AccountCreated, cancellationToken);

        Advance(TimeSpan.FromMinutes(1));
        await World().AddSecurityEventAsync(
            similar, null, SecurityEventType.AccountCreated, cancellationToken);

        ExplorerEvent entry = Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(Subject: "a_b"), Cap, cancellationToken));

        Assert.Equal(underscored, entry.SubjectIdentifier);
    }

    /// <summary>
    /// The subject filter and the actor filter are different questions. An administrator resetting
    /// somebody's password is the actor on an event whose subject is the other person, and asking "what
    /// did Mira do" must not return "what was done to Mira".
    /// </summary>
    [Fact]
    public async Task ActorFilter_MatchesWhoDidIt_NotWhoItWasAbout()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _adaIdentifier, _miraIdentifier,
            SecurityEventType.PasswordResetByAdministrator, cancellationToken);

        Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(Actor: "mira"), Cap, cancellationToken));

        Assert.Empty(await Reads().ListAsync(
            new EventExplorerFilter(Actor: "ada"), Cap, cancellationToken));

        Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(Subject: "ada"), Cap, cancellationToken));
    }

    /// <summary>
    /// An event with no actor matches no actor filter — not even one naming its subject. The searchable
    /// actor text is <c>concat_ws</c> over two NULLs, which is the empty string, and the empty string
    /// matches nothing. "Nobody did this" is a real answer and must not be borrowed from the subject.
    /// </summary>
    [Fact]
    public async Task ActorFilter_NeverMatchesAnEventThatHasNoActor()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _adaIdentifier, null, SecurityEventType.SignInFailed, cancellationToken);

        Assert.Empty(await Reads().ListAsync(
            new EventExplorerFilter(Actor: "ada"), Cap, cancellationToken));

        // Still there when nobody asks about the actor.
        Assert.Single(await Reads().ListAsync(EventExplorerFilter.Everything, Cap, cancellationToken));
    }

    /// <summary>
    /// The type filter is an exact match, and the three vocabularies do not overlap — which together are
    /// what let one flat <c>event_type = @EventType</c> serve all three streams. <c>created</c> is the
    /// menu's word; <c>account_created</c> is security's, and contains it. A substring match would return
    /// both, and an administrator asking "when was this item created" would be handed a list of accounts.
    /// </summary>
    [Fact]
    public async Task EventTypeFilter_IsExact_AndOneWordIsNeverAnotherWordsSubstring()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _adaIdentifier, _miraIdentifier, SecurityEventType.AccountCreated, cancellationToken);

        Advance(TimeSpan.FromMinutes(1));
        await World().AddMenuItemEventAsync(
            _soupIdentifier, _miraIdentifier, "created", "Soup", 4.50m, cancellationToken);

        Advance(TimeSpan.FromMinutes(1));
        await SendSoupAsync("Table 9", _adaIdentifier, cancellationToken);

        Assert.Equal(3, (await Reads().ListAsync(
            EventExplorerFilter.Everything, Cap, cancellationToken)).Count);

        ExplorerEvent onlyMenu = Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(EventType: "created"), Cap, cancellationToken));
        Assert.Equal(EventStream.Menu, onlyMenu.Stream);

        ExplorerEvent onlySecurity = Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(EventType: SecurityEventType.AccountCreated), Cap, cancellationToken));
        Assert.Equal(EventStream.Security, onlySecurity.Stream);

        ExplorerEvent onlyOrder = Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(EventType: "guest_submission"), Cap, cancellationToken));
        Assert.Equal(EventStream.Order, onlyOrder.Stream);

        // A word no stream uses is not an error, and is not everything either.
        Assert.Empty(await Reads().ListAsync(
            new EventExplorerFilter(EventType: "not_a_real_event_type"), Cap, cancellationToken));
    }

    /// <summary>
    /// The time range is half-open: <c>&gt;= from</c> and <c>&lt; before</c>. The boundary instant belongs
    /// to the lower bound and not to the upper one, which is what makes two adjacent days partition the
    /// events between them instead of double-counting the midnight row.
    /// </summary>
    [Fact]
    public async Task TimeRange_IsHalfOpenAtBothEnds()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        DateTimeOffset first = _clock.UtcNow;
        await World().AddSecurityEventAsync(
            _adaIdentifier, null, SecurityEventType.SignInSucceeded, cancellationToken);

        Advance(TimeSpan.FromHours(1));
        DateTimeOffset second = _clock.UtcNow;
        await World().AddSecurityEventAsync(
            _adaIdentifier, null, SecurityEventType.SignInSucceeded, cancellationToken);

        Advance(TimeSpan.FromHours(1));
        DateTimeOffset third = _clock.UtcNow;
        await World().AddSecurityEventAsync(
            _adaIdentifier, null, SecurityEventType.SignInSucceeded, cancellationToken);

        // >= second keeps the boundary row.
        Assert.Equal(2, (await Reads().ListAsync(
            new EventExplorerFilter(OccurredFrom: second), Cap, cancellationToken)).Count);

        // < second drops it.
        Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(OccurredBefore: second), Cap, cancellationToken));

        // Both together select exactly the middle one.
        ExplorerEvent middle = Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(OccurredFrom: second, OccurredBefore: third), Cap, cancellationToken));
        Assert.Equal(second, middle.OccurredAt);

        // And the whole window is still three.
        Assert.Equal(3, (await Reads().ListAsync(
            new EventExplorerFilter(OccurredFrom: first), Cap, cancellationToken)).Count);
    }

    // ---- the cap and the ordering ------------------------------------------------------------------

    /// <summary>
    /// The cap keeps the newest, because the cap is a rendering bound on a newest-first list and taking
    /// the oldest rows of a "what just happened" question would be worse than useless. A non-positive cap
    /// is answered with nothing rather than with an exception a caller has to defend against.
    /// </summary>
    [Fact]
    public async Task Cap_KeepsTheNewest_AndANonPositiveCapReturnsNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        for (int index = 0; index < 5; index++)
        {
            await World().AddSecurityEventAsync(
                _adaIdentifier, null, SecurityEventType.SignInSucceeded, cancellationToken);
            Advance(TimeSpan.FromMinutes(1));
        }

        DateTimeOffset newest = _clock.UtcNow.AddMinutes(-1);

        IReadOnlyList<ExplorerEvent> capped =
            await Reads().ListAsync(EventExplorerFilter.Everything, 2, cancellationToken);

        Assert.Equal(2, capped.Count);
        Assert.Equal(newest, capped[0].OccurredAt);

        Assert.Empty(await Reads().ListAsync(EventExplorerFilter.Everything, 0, cancellationToken));
        Assert.Empty(await Reads().ListAsync(EventExplorerFilter.Everything, -1, cancellationToken));
    }

    /// <summary>
    /// Events that share an instant still have a total order, so a re-read cannot shuffle them.
    ///
    /// <para>The assertion is that two reads agree rather than that the order is any particular one:
    /// PostgreSQL compares <c>uuid</c> as sixteen big-endian bytes and <see cref="Guid.CompareTo(Guid)"/>
    /// does not, so reproducing the expected sequence in C# would mean reimplementing the database's
    /// collation in the test — which would then be the thing under test. Determinism is the property that
    /// matters: without a tiebreak, narrowing a window to page past the cap could skip a row or show one
    /// twice, and neither is visible to the person doing it.</para>
    /// </summary>
    [Fact]
    public async Task Ordering_IsDeterministicWhenEventsShareAnInstant()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        for (int index = 0; index < 4; index++)
        {
            await World().AddSecurityEventAsync(
                _adaIdentifier, null, SecurityEventType.SignInFailed, cancellationToken);
        }

        IReadOnlyList<ExplorerEvent> first =
            await Reads().ListAsync(EventExplorerFilter.Everything, Cap, cancellationToken);
        IReadOnlyList<ExplorerEvent> second =
            await Reads().ListAsync(EventExplorerFilter.Everything, Cap, cancellationToken);

        Assert.Equal(4, first.Count);
        Assert.Equal(
            first.Select(entry => entry.EventIdentifier),
            second.Select(entry => entry.EventIdentifier));

        // All four are distinct rows, not one row read four times.
        Assert.Equal(4, first.Select(entry => entry.EventIdentifier).Distinct().Count());
    }

    // ---- the catalogue the filter offers -----------------------------------------------------------

    /// <summary>
    /// Every menu type the catalogue offers is a word the schema's CHECK accepts, and the explorer
    /// surfaces each of them. This is the drift check for the one vocabulary that has no owner to borrow
    /// from: <c>DapperMenuAdministration</c> and <c>DapperMenuAvailability</c> keep their words in private
    /// constants, so the catalogue spells them again, and only the database can say whether the two
    /// spellings still agree.
    ///
    /// <para><b>Each type is written with exactly the payload §8.2 binds to it, and getting that wrong is
    /// how this test failed rather than how it passes.</b> The five biconditionals are equalities, not
    /// permissions: <c>description_changed</c> without a description is refused by the same constraint
    /// that refuses <c>activated</c> with one. So this loop is also the only place in the suite that
    /// exercises all five payload shapes against the real CHECKs in one pass, which is why it is the
    /// thing that noticed <c>OrderTestWorld</c> could not write three of the eight (F-86).</para>
    /// </summary>
    [Fact]
    public async Task Catalogue_EveryMenuEventType_IsAcceptedByTheSchemaAndSurfaced()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // A real heading, because new_menu_section_identifier is a foreign key rather than a bare uuid
        // (0005) — an event naming a section that does not exist renders as a blank where a heading
        // should be, so the schema refuses it.
        Guid puddings = await World().AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);

        foreach (string eventType in EventTypeCatalogue.MenuEventTypes)
        {
            // §8.2's five paired CHECKs, each an equality between "this column is not null" and "the type
            // is one of these": the name on created and name_changed, the price on created and
            // price_changed, the description on description_changed alone, the position on reordered
            // alone, the heading on section_changed alone, and nothing at all on the two availability
            // types.
            string? newName = eventType is "created" or "name_changed" ? "Soup" : null;
            decimal? newPrice = eventType is "created" or "price_changed" ? 4.50m : null;
            string? newDescription = eventType is "description_changed" ? "Lentil, vegan" : null;
            int? newDisplayOrder = eventType is "reordered" ? 3 : null;
            Guid? newSection = eventType is "section_changed" ? puddings : null;

            await World().AddMenuItemEventAsync(
                _soupIdentifier,
                _miraIdentifier,
                eventType,
                newName,
                newPrice,
                cancellationToken,
                newDescription,
                newDisplayOrder,
                newSection);

            Advance(TimeSpan.FromMinutes(1));
        }

        IReadOnlyList<ExplorerEvent> events = await Reads().ListAsync(MenuOnly, Cap, cancellationToken);

        Assert.Equal(EventTypeCatalogue.MenuEventTypes.Count, events.Count);
        Assert.Equal(
            EventTypeCatalogue.MenuEventTypes.Order(StringComparer.Ordinal),
            events.Select(entry => entry.EventType).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The catalogue's security list is exactly the domain's closed vocabulary — no container needed. A
    /// new event type added to <see cref="SecurityEventType"/> and not to the catalogue would be
    /// invisible in the filter's dropdown while appearing in the list, which is the confusing half of
    /// wrong.
    /// </summary>
    [Fact]
    public void Catalogue_SecurityEventTypes_AreExactlyTheDomainsClosedVocabulary()
    {
        Assert.Equal(
            SecurityEventType.All.Order(StringComparer.Ordinal),
            EventTypeCatalogue.SecurityEventTypes.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// No word appears in two streams' vocabularies. The reader's type filter is a single flat comparison
    /// across the union rather than a (stream, type) pair, and that is only sound while this holds — no
    /// container needed to notice the day it stops.
    /// </summary>
    [Fact]
    public void Catalogue_TheThreeVocabularies_DoNotOverlap()
    {
        Assert.Equal(EventTypeCatalogue.All.Count, EventTypeCatalogue.All.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(EventStream.Security, EventTypeCatalogue.StreamFor(SecurityEventType.SignInFailed));
        Assert.Equal(EventStream.Order, EventTypeCatalogue.StreamFor("guest_submission"));
        Assert.Equal(EventStream.Menu, EventTypeCatalogue.StreamFor("created"));
        Assert.Null(EventTypeCatalogue.StreamFor("not_a_real_event_type"));
    }

    // ---- arrangement -------------------------------------------------------------------------------

    /// <summary>One event in each stream, five minutes apart, security oldest.</summary>
    private async Task OneOfEachAsync(CancellationToken cancellationToken)
    {
        await World().AddSecurityEventAsync(
            _adaIdentifier, _miraIdentifier, SecurityEventType.SignInSucceeded, cancellationToken);

        Advance(TimeSpan.FromMinutes(5));
        await SendSoupAsync("Table 4", _adaIdentifier, cancellationToken);

        Advance(TimeSpan.FromMinutes(5));
        await World().AddMenuItemEventAsync(
            _soupIdentifier, _miraIdentifier, "deactivated", null, null, cancellationToken);
    }

    private async Task<Guid> SendSoupAsync(
        string tableLabel,
        Guid guestIdentifier,
        CancellationToken cancellationToken)
    {
        Guid sittingIdentifier = await OpenTableAsync(tableLabel, cancellationToken, guestIdentifier);
        return await SendAsync(sittingIdentifier, guestIdentifier, _soupIdentifier, 1, cancellationToken);
    }

    private async Task<Guid> OpenTableAsync(
        string label,
        CancellationToken cancellationToken,
        params Guid[] members)
    {
        Guid tableIdentifier = await World().AddTableAsync(label, cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);

        foreach (Guid member in members)
        {
            await World().JoinAsync(sittingIdentifier, member, cancellationToken);
        }

        return sittingIdentifier;
    }

    private async Task<Guid> SendAsync(
        Guid sittingIdentifier,
        Guid guestIdentifier,
        Guid menuItemIdentifier,
        int quantity,
        CancellationToken cancellationToken)
    {
        // The zero unit price is deliberate: §6.5.4 has the transaction price the line from the menu row
        // it reads under the lock, so anything sent here is discarded.
        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            guestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                guestIdentifier,
                OrderActorRole.Guest,
                [new LineAddedOperation(_identifiers.Create(), menuItemIdentifier, quantity, 0m, null)]),
            cancellationToken);

        Assert.True(result.IsAppended);
        return result.GuestOrderIdentifier!.Value;
    }

    private void Advance(TimeSpan interval) => _clock.UtcNow = _clock.UtcNow.Add(interval);

    private static EventExplorerFilter SecurityOnly { get; } =
        new(IncludeOrderEvents: false, IncludeMenuEvents: false);

    private static EventExplorerFilter OrderOnly { get; } =
        new(IncludeSecurityEvents: false, IncludeMenuEvents: false);

    private static EventExplorerFilter MenuOnly { get; } =
        new(IncludeSecurityEvents: false, IncludeOrderEvents: false);

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperEventExplorerReads Reads() => new(_connectionFactory!);

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;
}
