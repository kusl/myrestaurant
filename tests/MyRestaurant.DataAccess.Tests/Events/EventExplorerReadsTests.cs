using MyRestaurant.DataAccess.Events;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Events;

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

        Assert.Null(entry.ContextIdentifier);
        Assert.Null(entry.ActorRole);
        Assert.Null(entry.SequenceNumber);
        Assert.Null(entry.NewName);
        Assert.Null(entry.NewPriceAmount);
    }

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

        Assert.Equal(
            EventStream.Security,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "pat"), Cap, cancellationToken)).Stream);

        Assert.Equal(
            EventStream.Order,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "Lovelace"), Cap, cancellationToken)).Stream);

        Assert.Equal(
            EventStream.Order,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "Terrace"), Cap, cancellationToken)).Stream);

        Assert.Equal(
            EventStream.Menu,
            Assert.Single(await Reads().ListAsync(
                new EventExplorerFilter(Subject: "steak"), Cap, cancellationToken)).Stream);
    }

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

    [Fact]
    public async Task ActorFilter_NeverMatchesAnEventThatHasNoActor()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await World().AddSecurityEventAsync(
            _adaIdentifier, null, SecurityEventType.SignInFailed, cancellationToken);

        Assert.Empty(await Reads().ListAsync(
            new EventExplorerFilter(Actor: "ada"), Cap, cancellationToken));

        Assert.Single(await Reads().ListAsync(EventExplorerFilter.Everything, Cap, cancellationToken));
    }

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

        Assert.Empty(await Reads().ListAsync(
            new EventExplorerFilter(EventType: "not_a_real_event_type"), Cap, cancellationToken));
    }

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

        Assert.Equal(2, (await Reads().ListAsync(
            new EventExplorerFilter(OccurredFrom: second), Cap, cancellationToken)).Count);

        Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(OccurredBefore: second), Cap, cancellationToken));

        ExplorerEvent middle = Assert.Single(await Reads().ListAsync(
            new EventExplorerFilter(OccurredFrom: second, OccurredBefore: third), Cap, cancellationToken));
        Assert.Equal(second, middle.OccurredAt);

        Assert.Equal(3, (await Reads().ListAsync(
            new EventExplorerFilter(OccurredFrom: first), Cap, cancellationToken)).Count);
    }

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

        Assert.Equal(4, first.Select(entry => entry.EventIdentifier).Distinct().Count());
    }

    [Fact]
    public async Task Catalogue_EveryMenuEventType_IsAcceptedByTheSchemaAndSurfaced()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid puddings = await World().AddMenuSectionAsync("Puddings", cancellationToken, displayOrder: 1);

        foreach (string eventType in EventTypeCatalogue.MenuEventTypes)
        {
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

    [Fact]
    public void Catalogue_SecurityEventTypes_AreExactlyTheDomainsClosedVocabulary()
    {
        Assert.Equal(
            SecurityEventType.All.Order(StringComparer.Ordinal),
            EventTypeCatalogue.SecurityEventTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Catalogue_TheThreeVocabularies_DoNotOverlap()
    {
        Assert.Equal(EventTypeCatalogue.All.Count, EventTypeCatalogue.All.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(EventStream.Security, EventTypeCatalogue.StreamFor(SecurityEventType.SignInFailed));
        Assert.Equal(EventStream.Order, EventTypeCatalogue.StreamFor("guest_submission"));
        Assert.Equal(EventStream.Menu, EventTypeCatalogue.StreamFor("created"));
        Assert.Null(EventTypeCatalogue.StreamFor("not_a_real_event_type"));
    }

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
