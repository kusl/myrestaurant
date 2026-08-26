using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

public sealed class OrderHistoryReadsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string Hidden = "hidden";

    private const string Unhidden = "unhidden";

    private const int Cap = 50;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 2, 17, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _adaIdentifier;
    private Guid _bodeIdentifier;
    private Guid _counterIdentifier;
    private Guid _administratorIdentifier;

    private Guid _namelessIdentifier;

    private Guid _soupIdentifier;
    private Guid _steakIdentifier;

    public OrderHistoryReadsTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
        _bodeIdentifier = await _world.AddPersonAsync("bode", "Bo", cancellationToken);
        _counterIdentifier = await _world.AddPersonAsync("cass", "Cass Okonkwo", cancellationToken);
        _administratorIdentifier = await _world.AddPersonAsync("mira", "Mira Adeyemi", cancellationToken);
        _namelessIdentifier = await _world.AddPersonAsync("pat", null, cancellationToken);

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
    public async Task PersonHistory_ListsSettledSittingsNewestFirst_AndExcludesTheOpenOne()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid firstOrder = await SettledMealAsync("Table 1", _adaIdentifier, _soupIdentifier, 1, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddDays(1);
        Guid secondOrder = await SettledMealAsync("Table 2", _adaIdentifier, _steakIdentifier, 2, cancellationToken);

        Guid openSitting = await OpenTableAsync("Table 3", cancellationToken, _adaIdentifier);
        await SendAsync(openSitting, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);

        IReadOnlyList<PersonOrderHistoryEntry> history =
            await Reads().ListVisibleHistoryForPersonAsync(_adaIdentifier, Cap, cancellationToken);

        Assert.Equal(2, history.Count);
        Assert.Equal(secondOrder, history[0].GuestOrderIdentifier);
        Assert.Equal("Table 2", history[0].TableLabel);
        Assert.Equal(firstOrder, history[1].GuestOrderIdentifier);
        Assert.Equal("Table 1", history[1].TableLabel);

        Assert.Equal(42.00m, history[0].PersonTotalAmount);
        Assert.Equal(4.50m, history[1].PersonTotalAmount);

        Assert.True(history[0].ClosedAt >= history[1].ClosedAt);
    }

    [Fact]
    public async Task PersonHistory_ExcludesAHiddenOrder()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid kept = await SettledMealAsync("Table 4", _adaIdentifier, _soupIdentifier, 1, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        Guid hidden = await SettledMealAsync("Table 5", _adaIdentifier, _steakIdentifier, 1, cancellationToken);

        await World().AddVisibilityEventAsync(hidden, _adaIdentifier, Hidden, cancellationToken);

        IReadOnlyList<PersonOrderHistoryEntry> history =
            await Reads().ListVisibleHistoryForPersonAsync(_adaIdentifier, Cap, cancellationToken);

        Assert.Equal(kept, Assert.Single(history).GuestOrderIdentifier);
    }

    [Fact]
    public async Task PersonHistory_IncludesAnOrderThatWasUnhiddenAgain()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledMealAsync(
            "Table 6", _adaIdentifier, _soupIdentifier, 1, cancellationToken);

        await World().AddVisibilityEventAsync(orderIdentifier, _adaIdentifier, Hidden, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(30);
        await World().AddVisibilityEventAsync(
            orderIdentifier, _administratorIdentifier, Unhidden, cancellationToken);

        IReadOnlyList<PersonOrderHistoryEntry> history =
            await Reads().ListVisibleHistoryForPersonAsync(_adaIdentifier, Cap, cancellationToken);

        Assert.Equal(orderIdentifier, Assert.Single(history).GuestOrderIdentifier);
    }

    [Fact]
    public async Task PersonHistory_ShowsOnlyThatPersonsOwnOrders()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync(
            "Table 7", cancellationToken, _adaIdentifier, _bodeIdentifier);

        Guid adaOrder = await SendAsync(
            sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);
        await SendAsync(sittingIdentifier, _bodeIdentifier, _steakIdentifier, 1, null, cancellationToken);

        await World().CloseSittingAsync(sittingIdentifier, _counterIdentifier, 25.50m, cancellationToken);

        IReadOnlyList<PersonOrderHistoryEntry> ada =
            await Reads().ListVisibleHistoryForPersonAsync(_adaIdentifier, Cap, cancellationToken);

        Assert.Equal(adaOrder, Assert.Single(ada).GuestOrderIdentifier);
        Assert.Equal(4.50m, ada[0].PersonTotalAmount);
    }

    [Fact]
    public async Task PersonHistory_CarriesTheCurrentLines_WithRemovalsGoneAndAdjustmentsApplied()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 8", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, Guid soupLine) = await SendWithLineAsync(
            sittingIdentifier, _adaIdentifier, _soupIdentifier, 2, "no cream", cancellationToken);

        (_, Guid steakLine) = await SendWithLineAsync(
            sittingIdentifier, _adaIdentifier, _steakIdentifier, 1, null, cancellationToken);

        await AdjustAsync(orderIdentifier, soupLine, 3.00m, "cold when it arrived", cancellationToken);
        await RemoveAsync(orderIdentifier, steakLine, "sent back", cancellationToken);

        await World().CloseSittingAsync(sittingIdentifier, _counterIdentifier, 6.00m, cancellationToken);

        PersonOrderHistoryEntry entry = Assert.Single(
            await Reads().ListVisibleHistoryForPersonAsync(_adaIdentifier, Cap, cancellationToken));

        OrderLineView line = Assert.Single(entry.Lines);
        Assert.Equal(soupLine, line.OrderLineIdentifier);
        Assert.Equal("Soup", line.MenuItemName);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(3.00m, line.CurrentUnitPriceAmount);
        Assert.Equal(6.00m, line.LineTotalAmount);
        Assert.Equal("no cream", line.CustomizationNote);

        Assert.Equal(1, entry.LineCount);
        Assert.Equal(6.00m, entry.PersonTotalAmount);
    }

    [Fact]
    public async Task PersonHistory_IsEmptyForSomebodyWhoNeverOrdered()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync(
            "Table 9", cancellationToken, _adaIdentifier, _bodeIdentifier);

        await SendAsync(sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);
        await World().CloseSittingAsync(sittingIdentifier, _counterIdentifier, 4.50m, cancellationToken);

        Assert.Empty(await Reads().ListVisibleHistoryForPersonAsync(_bodeIdentifier, Cap, cancellationToken));
        Assert.Empty(await Reads().ListVisibleHistoryForPersonAsync(
            _namelessIdentifier, Cap, cancellationToken));
    }

    [Fact]
    public async Task PersonHistory_RespectsTheCap()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        for (int visit = 1; visit <= 3; visit++)
        {
            _clock.UtcNow = _clock.UtcNow.AddDays(1);
            await SettledMealAsync($"Table 1{visit}", _adaIdentifier, _soupIdentifier, 1, cancellationToken);
        }

        Assert.Equal(
            2,
            (await Reads().ListVisibleHistoryForPersonAsync(_adaIdentifier, 2, cancellationToken)).Count);

        Assert.Empty(await Reads().ListVisibleHistoryForPersonAsync(_adaIdentifier, 0, cancellationToken));
    }

    [Fact]
    public async Task HiddenOrders_ListsEveryHiddenOrderSystemWide_MostRecentlyHiddenFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid adaOrder = await SettledMealAsync(
            "Table 20", _adaIdentifier, _soupIdentifier, 1, cancellationToken);
        Guid namelessOrder = await SettledMealAsync(
            "Table 21", _namelessIdentifier, _steakIdentifier, 1, cancellationToken);

        await World().AddVisibilityEventAsync(adaOrder, _adaIdentifier, Hidden, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        await World().AddVisibilityEventAsync(
            namelessOrder, _namelessIdentifier, Hidden, cancellationToken);

        IReadOnlyList<HiddenOrderSummary> hidden = await Reads().ListHiddenOrdersAsync(
            HiddenOrderFilter.Everything, Cap, cancellationToken);

        Assert.Equal(2, hidden.Count);

        Assert.Equal(namelessOrder, hidden[0].GuestOrderIdentifier);
        Assert.Equal("pat", hidden[0].Username);
        Assert.Null(hidden[0].DisplayName);

        Assert.Equal("pat", hidden[0].OwnerName);
        Assert.Equal("pat", hidden[0].HiddenByName);
        Assert.Equal(_namelessIdentifier, hidden[0].HiddenByPersonIdentifier);

        Assert.Equal(adaOrder, hidden[1].GuestOrderIdentifier);
        Assert.Equal("Ada Lovelace", hidden[1].OwnerName);
        Assert.Equal("Table 20", hidden[1].TableLabel);
        Assert.True(hidden[0].HiddenAt > hidden[1].HiddenAt);
    }

    [Fact]
    public async Task HiddenOrders_CarryTheSittingContextAndBothTotals()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 22", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        await World().JoinAsync(sittingIdentifier, _adaIdentifier, cancellationToken);
        await World().JoinAsync(sittingIdentifier, _bodeIdentifier, cancellationToken);

        Guid adaOrder = await SendAsync(
            sittingIdentifier, _adaIdentifier, _steakIdentifier, 1, null, cancellationToken);
        await SendAsync(sittingIdentifier, _bodeIdentifier, _soupIdentifier, 2, null, cancellationToken);

        await World().CloseSittingAsync(sittingIdentifier, _counterIdentifier, 30.00m, cancellationToken);
        await World().AddVisibilityEventAsync(adaOrder, _adaIdentifier, Hidden, cancellationToken);

        HiddenOrderSummary summary = Assert.Single(
            await Reads().ListHiddenOrdersAsync(HiddenOrderFilter.Everything, Cap, cancellationToken));

        Assert.Equal(sittingIdentifier, summary.SittingIdentifier);
        Assert.Equal(tableIdentifier, summary.TableIdentifier);
        Assert.Equal("Table 22", summary.TableLabel);
        Assert.Equal(_adaIdentifier, summary.OwnerPersonIdentifier);
        Assert.NotNull(summary.ClosedAt);

        Assert.Equal(30.00m, summary.SettledTotalAmount);
        Assert.Equal(21.00m, summary.PersonTotalAmount);
        Assert.Equal(1, summary.LineCount);
    }

    [Fact]
    public async Task HiddenOrders_ExcludeAnOrderThatWasUnhidden()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledMealAsync(
            "Table 23", _adaIdentifier, _soupIdentifier, 1, cancellationToken);

        await World().AddVisibilityEventAsync(orderIdentifier, _adaIdentifier, Hidden, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await World().AddVisibilityEventAsync(
            orderIdentifier, _administratorIdentifier, Unhidden, cancellationToken);

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            HiddenOrderFilter.Everything, Cap, cancellationToken));
    }

    [Fact]
    public async Task HiddenOrders_ReportTheHideCurrentlyInForce_AfterARoundTrip()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledMealAsync(
            "Table 24", _adaIdentifier, _soupIdentifier, 1, cancellationToken);

        await World().AddVisibilityEventAsync(orderIdentifier, _adaIdentifier, Hidden, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await World().AddVisibilityEventAsync(
            orderIdentifier, _administratorIdentifier, Unhidden, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        DateTimeOffset secondHide = _clock.UtcNow;
        await World().AddVisibilityEventAsync(orderIdentifier, _adaIdentifier, Hidden, cancellationToken);

        HiddenOrderSummary summary = Assert.Single(
            await Reads().ListHiddenOrdersAsync(HiddenOrderFilter.Everything, Cap, cancellationToken));

        Assert.Equal(orderIdentifier, summary.GuestOrderIdentifier);
        Assert.Equal(secondHide, summary.HiddenAt);
    }

    [Fact]
    public async Task HiddenOrders_FilterByUsername_MatchesASubstringCaseInsensitively()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid adaOrder = await HiddenMealAsync("Table 25", _adaIdentifier, cancellationToken);
        await HiddenMealAsync("Table 26", _bodeIdentifier, cancellationToken);

        Assert.Equal(
            adaOrder,
            Assert.Single(await Reads().ListHiddenOrdersAsync(
                new HiddenOrderFilter(Username: "AD"), Cap, cancellationToken)).GuestOrderIdentifier);

        Assert.Equal(
            2,
            (await Reads().ListHiddenOrdersAsync(
                new HiddenOrderFilter(Username: "   "), Cap, cancellationToken)).Count);

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            new HiddenOrderFilter(Username: "nobody"), Cap, cancellationToken));
    }

    [Fact]
    public async Task HiddenOrders_FilterByUsername_TreatsWildcardCharactersLiterally()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await HiddenMealAsync("Table 27", _adaIdentifier, cancellationToken);

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            new HiddenOrderFilter(Username: "%"), Cap, cancellationToken));

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            new HiddenOrderFilter(Username: "a_a"), Cap, cancellationToken));
    }

    [Fact]
    public async Task HiddenOrders_FilterByTable()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid wanted = await HiddenMealAsync("Table 28", _adaIdentifier, cancellationToken);
        await HiddenMealAsync("Table 29", _bodeIdentifier, cancellationToken);

        Guid tableIdentifier = Assert.Single(
            await Reads().ListHiddenOrdersAsync(
                new HiddenOrderFilter(Username: "ada"), Cap, cancellationToken)).TableIdentifier;

        HiddenOrderSummary summary = Assert.Single(await Reads().ListHiddenOrdersAsync(
            new HiddenOrderFilter(TableIdentifier: tableIdentifier), Cap, cancellationToken));

        Assert.Equal(wanted, summary.GuestOrderIdentifier);
        Assert.Equal("Table 28", summary.TableLabel);

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            new HiddenOrderFilter(TableIdentifier: _identifiers.Create()), Cap, cancellationToken));
    }

    [Fact]
    public async Task HiddenOrders_FilterByDateRange_OnWhenTheSittingOpened()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        DateTimeOffset monday = new(2026, 6, 8, 18, 0, 0, TimeSpan.Zero);
        DateTimeOffset tuesday = new(2026, 6, 9, 18, 0, 0, TimeSpan.Zero);

        _clock.UtcNow = monday;
        Guid mondayOrder = await HiddenMealAsync("Table 30", _adaIdentifier, cancellationToken);

        _clock.UtcNow = tuesday;
        Guid tuesdayOrder = await HiddenMealAsync("Table 31", _adaIdentifier, cancellationToken);

        Assert.Equal(
            mondayOrder,
            Assert.Single(await Reads().ListHiddenOrdersAsync(
                new HiddenOrderFilter(
                    OpenedFrom: new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero),
                    OpenedBefore: new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero)),
                Cap,
                cancellationToken)).GuestOrderIdentifier);

        Assert.Equal(
            tuesdayOrder,
            Assert.Single(await Reads().ListHiddenOrdersAsync(
                new HiddenOrderFilter(OpenedFrom: tuesday), Cap, cancellationToken))
                .GuestOrderIdentifier);

        Assert.Equal(
            mondayOrder,
            Assert.Single(await Reads().ListHiddenOrdersAsync(
                new HiddenOrderFilter(OpenedBefore: tuesday), Cap, cancellationToken))
                .GuestOrderIdentifier);

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            new HiddenOrderFilter(OpenedFrom: monday.AddDays(-3), OpenedBefore: monday.AddDays(-2)),
            Cap,
            cancellationToken));
    }

    [Fact]
    public async Task HiddenOrders_FiltersCompose()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid wanted = await HiddenMealAsync("Table 32", _adaIdentifier, cancellationToken);
        await HiddenMealAsync("Table 33", _bodeIdentifier, cancellationToken);

        Guid tableIdentifier = Assert.Single(await Reads().ListHiddenOrdersAsync(
            new HiddenOrderFilter(Username: "ada"), Cap, cancellationToken)).TableIdentifier;

        HiddenOrderFilter narrow = new(
            Username: "ada",
            OpenedFrom: _clock.UtcNow.AddDays(-1),
            OpenedBefore: _clock.UtcNow.AddDays(1),
            TableIdentifier: tableIdentifier);

        Assert.True(narrow.IsNarrowed);
        Assert.False(HiddenOrderFilter.Everything.IsNarrowed);

        Assert.Equal(
            wanted,
            Assert.Single(await Reads().ListHiddenOrdersAsync(narrow, Cap, cancellationToken))
                .GuestOrderIdentifier);

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            narrow with { Username = "bode" }, Cap, cancellationToken));
    }

    [Fact]
    public async Task HiddenOrders_StillReportARowWhoseSittingIsSomehowOpen()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 34", cancellationToken, _adaIdentifier);
        Guid orderIdentifier = await SendAsync(
            sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);

        await World().AddVisibilityEventAsync(orderIdentifier, _adaIdentifier, Hidden, cancellationToken);

        HiddenOrderSummary summary = Assert.Single(
            await Reads().ListHiddenOrdersAsync(HiddenOrderFilter.Everything, Cap, cancellationToken));

        Assert.Equal(orderIdentifier, summary.GuestOrderIdentifier);
        Assert.Null(summary.ClosedAt);
        Assert.Null(summary.SettledTotalAmount);
        Assert.Equal(4.50m, summary.PersonTotalAmount);
    }

    [Fact]
    public async Task HiddenOrders_RespectTheCap()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        for (int visit = 1; visit <= 3; visit++)
        {
            _clock.UtcNow = _clock.UtcNow.AddHours(1);
            await HiddenMealAsync($"Table 4{visit}", _adaIdentifier, cancellationToken);
        }

        Assert.Equal(
            2,
            (await Reads().ListHiddenOrdersAsync(HiddenOrderFilter.Everything, 2, cancellationToken))
                .Count);

        Assert.Empty(await Reads().ListHiddenOrdersAsync(
            HiddenOrderFilter.Everything, 0, cancellationToken));
    }

    [Fact]
    public async Task VisibilityLog_ListsEveryEventOldestFirst_WithTheStoredWordAndTheActorNamed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledMealAsync(
            "Table 50", _namelessIdentifier, _soupIdentifier, 1, cancellationToken);

        await World().AddVisibilityEventAsync(
            orderIdentifier, _namelessIdentifier, Hidden, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(20);
        await World().AddVisibilityEventAsync(
            orderIdentifier, _administratorIdentifier, Unhidden, cancellationToken);

        IReadOnlyList<OrderVisibilityEntry> log =
            await Reads().ListVisibilityLogAsync(orderIdentifier, cancellationToken);

        Assert.Equal(2, log.Count);

        Assert.Equal(Hidden, log[0].EventType);
        Assert.Equal(_namelessIdentifier, log[0].ActorPersonIdentifier);
        Assert.Equal("pat", log[0].ActorName);

        Assert.Equal(Unhidden, log[1].EventType);
        Assert.Equal("Mira Adeyemi", log[1].ActorName);
        Assert.True(log[1].OccurredAt > log[0].OccurredAt);

        Assert.All(log, entry => Assert.Equal(orderIdentifier, entry.GuestOrderIdentifier));
    }

    [Fact]
    public async Task VisibilityLog_IsEmptyForAnOrderNobodyEverHid_AndForAnUnknownOrder()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid orderIdentifier = await SettledMealAsync(
            "Table 51", _adaIdentifier, _soupIdentifier, 1, cancellationToken);

        Assert.Empty(await Reads().ListVisibilityLogAsync(orderIdentifier, cancellationToken));
        Assert.Empty(await Reads().ListVisibilityLogAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task VisibilityLog_ExcludesOtherOrdersEvents()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid mine = await HiddenMealAsync("Table 52", _adaIdentifier, cancellationToken);
        Guid theirs = await HiddenMealAsync("Table 53", _bodeIdentifier, cancellationToken);

        Assert.Equal(
            mine,
            Assert.Single(await Reads().ListVisibilityLogAsync(mine, cancellationToken))
                .GuestOrderIdentifier);

        Assert.Equal(
            theirs,
            Assert.Single(await Reads().ListVisibilityLogAsync(theirs, cancellationToken))
                .GuestOrderIdentifier);
    }

    private async Task<Guid> HiddenMealAsync(
        string tableLabel,
        Guid guestIdentifier,
        CancellationToken cancellationToken)
    {
        Guid orderIdentifier = await SettledMealAsync(
            tableLabel, guestIdentifier, _soupIdentifier, 1, cancellationToken);

        await World().AddVisibilityEventAsync(
            orderIdentifier, guestIdentifier, Hidden, cancellationToken);

        return orderIdentifier;
    }

    private async Task<Guid> SettledMealAsync(
        string tableLabel,
        Guid guestIdentifier,
        Guid menuItemIdentifier,
        int quantity,
        CancellationToken cancellationToken)
    {
        Guid sittingIdentifier = await OpenTableAsync(tableLabel, cancellationToken, guestIdentifier);

        Guid orderIdentifier = await SendAsync(
            sittingIdentifier, guestIdentifier, menuItemIdentifier, quantity, null, cancellationToken);

        await World().CloseSittingAsync(sittingIdentifier, _counterIdentifier, 0m, cancellationToken);

        return orderIdentifier;
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
        string? customizationNote,
        CancellationToken cancellationToken)
    {
        (Guid orderIdentifier, _) = await SendWithLineAsync(
            sittingIdentifier, guestIdentifier, menuItemIdentifier, quantity, customizationNote,
            cancellationToken);

        return orderIdentifier;
    }

    private async Task<(Guid OrderIdentifier, Guid LineIdentifier)> SendWithLineAsync(
        Guid sittingIdentifier,
        Guid guestIdentifier,
        Guid menuItemIdentifier,
        int quantity,
        string? customizationNote,
        CancellationToken cancellationToken)
    {
        Guid lineIdentifier = _identifiers.Create();

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            guestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                guestIdentifier,
                OrderActorRole.Guest,
                [new LineAddedOperation(lineIdentifier, menuItemIdentifier, quantity, 0m, customizationNote)]),
            cancellationToken);

        Assert.True(result.IsAppended);
        return (result.GuestOrderIdentifier!.Value, lineIdentifier);
    }

    private async Task AdjustAsync(
        Guid orderIdentifier,
        Guid lineIdentifier,
        decimal newUnitPrice,
        string reason,
        CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.PriceAdjustment,
                _counterIdentifier,
                OrderActorRole.Counter,
                [new LinePriceAdjustedOperation(lineIdentifier, newUnitPrice, reason)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private async Task RemoveAsync(
        Guid orderIdentifier,
        Guid lineIdentifier,
        string? reason,
        CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                _counterIdentifier,
                OrderActorRole.Counter,
                [new LineRemovedOperation(lineIdentifier, reason)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperOrderHistoryReads Reads() => new(_connectionFactory!);

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;
}
