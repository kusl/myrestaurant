using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Sittings;

/// <summary>
/// Integration tests for <see cref="DapperCounterBoardReads"/> against a real PostgreSQL 17 container —
/// TECHNICAL_SPECIFICATION §11.3, the two lists the counter works from, and §5.4's last-activity column.
///
/// <para>Most of what can go wrong here is arithmetic in SQL rather than logic in C#: the roll-up sums
/// <c>order_current_state</c> through a LATERAL, and every one of those sums is a type the reader will
/// not hand to a constructor parameter without a cast (<c>count(*)</c> is <c>bigint</c>, and
/// <c>sum()</c> over it widens to <c>numeric</c>). An aggregate with no GROUP BY returns one row even
/// over nothing, which is what makes a table where everybody joined and nobody ordered appear with
/// zeroes rather than vanish — a table missing from the counter's list is a table nobody bills.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
public sealed class CounterBoardReadsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _guestIdentifier;
    private Guid _counterIdentifier;
    private Guid _kitchenIdentifier;
    private Guid _soupIdentifier;
    private Guid _steakIdentifier;

    public CounterBoardReadsTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _guestIdentifier = await _world.AddPersonAsync("ada", "Ada", cancellationToken);
        _counterIdentifier = await _world.AddPersonAsync("cass", "Cass Okonkwo", cancellationToken);
        _kitchenIdentifier = await _world.AddPersonAsync("kim", "Kim", cancellationToken);

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
    public async Task ListOpenSittings_RollsUpTheTableTheGuestsTheLinesAndTheMoney()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 7", cancellationToken);
        DateTimeOffset openedAt = _clock.UtcNow;
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);

        await World().JoinAsync(sittingIdentifier, _guestIdentifier, cancellationToken);

        Guid second = await World().AddPersonAsync("bo", "Bo", cancellationToken);
        await World().JoinAsync(sittingIdentifier, second, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(4);
        DateTimeOffset lastEventAt = _clock.UtcNow;
        await SendAsync(sittingIdentifier, _guestIdentifier, _soupIdentifier, quantity: 2, cancellationToken);

        CounterSittingSummary sitting = Assert.Single(await Reads().ListOpenSittingsAsync(cancellationToken));

        Assert.Equal(sittingIdentifier, sitting.SittingIdentifier);
        Assert.Equal(tableIdentifier, sitting.TableIdentifier);
        Assert.Equal("Table 7", sitting.TableLabel);
        Assert.Equal(openedAt, sitting.OpenedAt);
        Assert.True(sitting.IsOpen);
        Assert.Null(sitting.ClosedAt);
        Assert.Null(sitting.SettledTotalAmount);

        // Two people joined; only one of them has an order (§6.1 — the row is created by the first send).
        Assert.Equal(2, sitting.MemberCount);
        Assert.Equal(1, sitting.OrderCount);

        Assert.Equal(1, sitting.PendingLineCount);
        Assert.Equal(0, sitting.FulfilledLineCount);
        Assert.True(sitting.HasPendingLines);
        Assert.Equal(9.00m, sitting.CurrentTotalAmount);
        Assert.Equal(9.00m, sitting.AmountToShow);
        Assert.Equal(lastEventAt, sitting.LastEventAt);
        Assert.False(sitting.HasPostCloseCorrections);
    }

    /// <summary>
    /// A table where everybody joined and nobody has ordered has no <c>order_current_state</c> rows at
    /// all. It must still appear — it is a real table with real people at it — with zeroes rather than
    /// nulls, which is what the aggregate-with-no-GROUP-BY in the LATERAL buys.
    /// </summary>
    [Fact]
    public async Task ListOpenSittings_ASittingNobodyOrderedIn_AppearsWithZeroes()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 1", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        await World().JoinAsync(sittingIdentifier, _guestIdentifier, cancellationToken);

        CounterSittingSummary sitting = Assert.Single(await Reads().ListOpenSittingsAsync(cancellationToken));

        Assert.Equal(1, sitting.MemberCount);
        Assert.Equal(0, sitting.OrderCount);
        Assert.Equal(0, sitting.PendingLineCount);
        Assert.Equal(0, sitting.FulfilledLineCount);
        Assert.Equal(0m, sitting.CurrentTotalAmount);
        Assert.False(sitting.HasPendingLines);
        Assert.Null(sitting.LastEventAt);
    }

    [Fact]
    public async Task ListOpenSittings_CountPendingAndFulfilledLinesSeparately()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 2", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        await World().JoinAsync(sittingIdentifier, _guestIdentifier, cancellationToken);

        (Guid orderIdentifier, Guid soupLine) = await SendAsync(
            sittingIdentifier, _guestIdentifier, _soupIdentifier, quantity: 1, cancellationToken);
        await SendAsync(sittingIdentifier, _guestIdentifier, _steakIdentifier, quantity: 1, cancellationToken);

        await FulfillAsync(orderIdentifier, soupLine, cancellationToken);

        CounterSittingSummary sitting = Assert.Single(await Reads().ListOpenSittingsAsync(cancellationToken));

        Assert.Equal(1, sitting.PendingLineCount);
        Assert.Equal(1, sitting.FulfilledLineCount);

        // §8.3: the running total includes the pending line, because that is what the table owes.
        Assert.Equal(25.50m, sitting.CurrentTotalAmount);
    }

    [Fact]
    public async Task ListOpenSittings_ExcludeClosedOnesAndAreOrderedOldestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid firstTable = await World().AddTableAsync("Table A", cancellationToken);
        Guid firstSitting = await World().OpenSittingAsync(firstTable, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(20);
        Guid secondTable = await World().AddTableAsync("Table B", cancellationToken);
        Guid secondSitting = await World().OpenSittingAsync(secondTable, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(20);
        Guid thirdTable = await World().AddTableAsync("Table C", cancellationToken);
        Guid thirdSitting = await World().OpenSittingAsync(thirdTable, cancellationToken);
        await Settlement().CloseAndSettleAsync(thirdSitting, _counterIdentifier, cancellationToken);

        IReadOnlyList<CounterSittingSummary> open = await Reads().ListOpenSittingsAsync(cancellationToken);

        Assert.Equal(2, open.Count);
        Assert.Equal(firstSitting, open[0].SittingIdentifier);
        Assert.Equal(secondSitting, open[1].SittingIdentifier);
    }

    [Fact]
    public async Task ListRecentlyClosedSittings_CarryTheStampedTotalAndWhoClosedIt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 3", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        await World().JoinAsync(sittingIdentifier, _guestIdentifier, cancellationToken);
        await SendAsync(sittingIdentifier, _guestIdentifier, _steakIdentifier, quantity: 1, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(30);
        DateTimeOffset closedAt = _clock.UtcNow;
        await Settlement().CloseAndSettleAsync(sittingIdentifier, _counterIdentifier, cancellationToken);

        CounterSittingSummary sitting = Assert.Single(await Reads()
            .ListRecentlyClosedSittingsAsync(_clock.UtcNow.AddHours(-12), 25, cancellationToken));

        Assert.False(sitting.IsOpen);
        Assert.Equal(closedAt, sitting.ClosedAt);
        Assert.Equal(_counterIdentifier, sitting.ClosedByPersonIdentifier);
        Assert.Equal("Cass Okonkwo", sitting.ClosedByName);
        Assert.Equal(21.00m, sitting.SettledTotalAmount);
        Assert.Equal(21.00m, sitting.AmountToShow);
        Assert.False(sitting.HasPostCloseCorrections);
    }

    /// <summary>
    /// The name on a closed row falls back to the username, exactly as every other roster and bill in the
    /// system does — a staff account with no display name must not produce a blank line at the till.
    /// </summary>
    [Fact]
    public async Task ListRecentlyClosedSittings_FallBackToTheUsernameWhenThereIsNoDisplayName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid anonymous = await World().AddPersonAsync("till01", null, cancellationToken);

        Guid tableIdentifier = await World().AddTableAsync("Table 5", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        await Settlement().CloseAndSettleAsync(sittingIdentifier, anonymous, cancellationToken);

        CounterSittingSummary sitting = Assert.Single(await Reads()
            .ListRecentlyClosedSittingsAsync(_clock.UtcNow.AddHours(-12), 25, cancellationToken));

        Assert.Equal("till01", sitting.ClosedByName);
    }

    [Fact]
    public async Task ListRecentlyClosedSittings_RespectTheWindowAndReturnTheMostRecentFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid oldTable = await World().AddTableAsync("Table Old", cancellationToken);
        Guid oldSitting = await World().OpenSittingAsync(oldTable, cancellationToken);
        await Settlement().CloseAndSettleAsync(oldSitting, _counterIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddHours(20);
        Guid middleTable = await World().AddTableAsync("Table Middle", cancellationToken);
        Guid middleSitting = await World().OpenSittingAsync(middleTable, cancellationToken);
        await Settlement().CloseAndSettleAsync(middleSitting, _counterIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        Guid newTable = await World().AddTableAsync("Table New", cancellationToken);
        Guid newSitting = await World().OpenSittingAsync(newTable, cancellationToken);
        await Settlement().CloseAndSettleAsync(newSitting, _counterIdentifier, cancellationToken);

        IReadOnlyList<CounterSittingSummary> recent = await Reads()
            .ListRecentlyClosedSittingsAsync(_clock.UtcNow.AddHours(-12), 25, cancellationToken);

        // The one closed 22 hours ago is outside the window; the other two come back newest first.
        Assert.Equal(2, recent.Count);
        Assert.Equal(newSitting, recent[0].SittingIdentifier);
        Assert.Equal(middleSitting, recent[1].SittingIdentifier);
    }

    [Fact]
    public async Task ListRecentlyClosedSittings_RespectTheCap()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid newest = Guid.Empty;

        for (int index = 0; index < 4; index++)
        {
            Guid tableIdentifier = await World().AddTableAsync($"Table {index}", cancellationToken);
            Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
            await Settlement().CloseAndSettleAsync(sittingIdentifier, _counterIdentifier, cancellationToken);

            newest = sittingIdentifier;
            _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        }

        IReadOnlyList<CounterSittingSummary> recent = await Reads()
            .ListRecentlyClosedSittingsAsync(_clock.UtcNow.AddHours(-12), 2, cancellationToken);

        Assert.Equal(2, recent.Count);
        Assert.Equal(newest, recent[0].SittingIdentifier);
    }

    /// <summary>Asking for nothing is answered without a round trip rather than by a SQL error.</summary>
    [Fact]
    public async Task ListRecentlyClosedSittings_ANonPositiveCap_ReturnsNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 6", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        await Settlement().CloseAndSettleAsync(sittingIdentifier, _counterIdentifier, cancellationToken);

        Assert.Empty(await Reads()
            .ListRecentlyClosedSittingsAsync(_clock.UtcNow.AddHours(-12), 0, cancellationToken));
    }

    [Fact]
    public async Task GetSitting_UnknownIdentifier_IsNull()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await Reads().GetSittingAsync(_identifiers.Create(), cancellationToken));
    }

    /// <summary>
    /// §5.3: the settled total is never rewritten, so once a §6.7 correction lands the two numbers
    /// differ — and the UI is required to show both. This is the read that lets it.
    /// </summary>
    [Fact]
    public async Task GetSitting_AfterAPostCloseCorrection_ReportsBothTotals()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 8", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        await World().JoinAsync(sittingIdentifier, _guestIdentifier, cancellationToken);

        (Guid orderIdentifier, _) = await SendAsync(
            sittingIdentifier, _guestIdentifier, _soupIdentifier, quantity: 1, cancellationToken);

        await Settlement().CloseAndSettleAsync(sittingIdentifier, _counterIdentifier, cancellationToken);

        CounterSittingSummary beforeCorrection =
            (await Reads().GetSittingAsync(sittingIdentifier, cancellationToken))!;
        Assert.Equal(4.50m, beforeCorrection.SettledTotalAmount);
        Assert.Equal(4.50m, beforeCorrection.CurrentTotalAmount);
        Assert.False(beforeCorrection.HasPostCloseCorrections);

        // §6.5.8: after a close only an administrator may append, and never a guest submission.
        AppendOrderEventResult correction = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                _counterIdentifier,
                OrderActorRole.Administrator,
                [new LineAddedOperation(_identifiers.Create(), _steakIdentifier, 1, 0m, "billed late")]),
            cancellationToken);
        Assert.True(correction.IsAppended);

        CounterSittingSummary afterCorrection =
            (await Reads().GetSittingAsync(sittingIdentifier, cancellationToken))!;

        Assert.Equal(4.50m, afterCorrection.SettledTotalAmount);
        Assert.Equal(25.50m, afterCorrection.CurrentTotalAmount);
        Assert.True(afterCorrection.HasPostCloseCorrections);

        // What the counter shows as the amount is still what was charged.
        Assert.Equal(4.50m, afterCorrection.AmountToShow);
    }

    private async Task<(Guid OrderIdentifier, Guid LineIdentifier)> SendAsync(
        Guid sittingIdentifier,
        Guid guestIdentifier,
        Guid menuItemIdentifier,
        int quantity,
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
                [new LineAddedOperation(lineIdentifier, menuItemIdentifier, quantity, 0m, null)]),
            cancellationToken);

        Assert.True(result.IsAppended);
        return (result.GuestOrderIdentifier!.Value, lineIdentifier);
    }

    private async Task FulfillAsync(Guid orderIdentifier, Guid lineIdentifier, CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment,
                _kitchenIdentifier,
                OrderActorRole.Kitchen,
                [new LineFulfilledOperation(lineIdentifier)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperCounterBoardReads Reads() => new(_connectionFactory!);

    private DapperSittingSettlement Settlement() => new(_connectionFactory!, _clock);

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;
}
