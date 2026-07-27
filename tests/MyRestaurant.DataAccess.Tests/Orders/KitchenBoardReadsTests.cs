using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

/// <summary>
/// Integration tests for <see cref="DapperKitchenBoardReads"/> against a real PostgreSQL 17 container —
/// the query behind §11.2's "Undo affordance on recently-fulfilled lines".
///
/// <para>The interesting cases are all about <em>which</em> fulfillment a line reports. A line can be
/// fulfilled, reversed, and fulfilled again, and the Undo list must show the last one; a line whose most
/// recent flip was a reversal must not appear at all, because it is pending again and belongs in the
/// queue instead. That question cannot be answered by <c>order_current_line.is_fulfilled</c>, which is a
/// boolean with the instant thrown away — which is exactly why this query exists rather than a sixth
/// method on the read model.</para>
/// </summary>
public sealed class KitchenBoardReadsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 5, 14, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _tableIdentifier;
    private Guid _sittingIdentifier;
    private Guid _guestIdentifier;
    private Guid _kitchenIdentifier;
    private Guid _counterIdentifier;
    private Guid _menuItemIdentifier;

    public KitchenBoardReadsTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _tableIdentifier = await _world.AddTableAsync("Table 9", cancellationToken);
        _sittingIdentifier = await _world.OpenSittingAsync(_tableIdentifier, cancellationToken);
        _guestIdentifier = await _world.AddPersonAsync("ada", "Ada", cancellationToken);
        await _world.JoinAsync(_sittingIdentifier, _guestIdentifier, cancellationToken);

        _kitchenIdentifier = await _world.AddPersonAsync("kim", "Kim", cancellationToken);
        _counterIdentifier = await _world.AddPersonAsync("cass", "Cass", cancellationToken);
        _menuItemIdentifier = await _world.AddMenuItemAsync("Soup", 4.50m, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task AFulfilledLine_AppearsWithItsFulfillmentInstantAndItsTicketDetails()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid lineIdentifier) = await SendAsync(quantity: 2, note: "no onions", cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
        DateTimeOffset fulfilledAt = _clock.UtcNow;
        await FulfillAsync(orderIdentifier, lineIdentifier, cancellationToken);

        KitchenFulfilledLineView line = Assert.Single(await Reads()
            .ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));

        Assert.Equal(lineIdentifier, line.OrderLineIdentifier);
        Assert.Equal(orderIdentifier, line.GuestOrderIdentifier);
        Assert.Equal(_sittingIdentifier, line.SittingIdentifier);
        Assert.Equal(_tableIdentifier, line.TableIdentifier);
        Assert.Equal("Table 9", line.TableLabel);
        Assert.Equal("Ada", line.PersonName);
        Assert.Equal("Soup", line.MenuItemName);
        Assert.Equal(2, line.Quantity);
        Assert.Equal("no onions", line.CustomizationNote);
        Assert.Equal(fulfilledAt, line.FulfilledAt);
        Assert.True(line.AddedAt < line.FulfilledAt);
    }

    [Fact]
    public async Task APendingLine_IsAbsent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await SendAsync(quantity: 1, note: null, cancellationToken);

        Assert.Empty(await Reads().ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));
    }

    /// <summary>
    /// Once Undo has been pressed the line is pending again, so it belongs on the queue and must leave
    /// this list — otherwise a second Undo would be offered for something that is not fulfilled, and
    /// the transaction would refuse it (§6.5.6) with no way for the cook to know why.
    /// </summary>
    [Fact]
    public async Task ALineWhoseFulfillmentWasReversed_IsAbsent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid lineIdentifier) = await SendAsync(quantity: 1, note: null, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await FulfillAsync(orderIdentifier, lineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await RevertAsync(orderIdentifier, lineIdentifier, cancellationToken);

        Assert.Empty(await Reads().ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));
    }

    /// <summary>A line fulfilled, undone, and fulfilled again reports the <em>second</em> fulfillment.</summary>
    [Fact]
    public async Task ARefulfilledLine_ReportsTheLatestFulfillment()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid lineIdentifier) = await SendAsync(quantity: 1, note: null, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await FulfillAsync(orderIdentifier, lineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await RevertAsync(orderIdentifier, lineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        DateTimeOffset secondFulfillment = _clock.UtcNow;
        await FulfillAsync(orderIdentifier, lineIdentifier, cancellationToken);

        KitchenFulfilledLineView line = Assert.Single(await Reads()
            .ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));

        Assert.Equal(secondFulfillment, line.FulfilledAt);
    }

    [Fact]
    public async Task ALineFulfilledBeforeTheWindow_IsAbsent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid lineIdentifier) = await SendAsync(quantity: 1, note: null, cancellationToken);
        await FulfillAsync(orderIdentifier, lineIdentifier, cancellationToken);

        // Inside the window it is there; an hour later it is not.
        Assert.Single(await Reads().ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));

        _clock.UtcNow = _clock.UtcNow.AddHours(1);

        Assert.Empty(await Reads().ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));
    }

    [Fact]
    public async Task ARemovedLine_IsAbsentEvenThoughItWasFulfilled()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid lineIdentifier) = await SendAsync(quantity: 1, note: null, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await FulfillAsync(orderIdentifier, lineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        AppendOrderEventResult removal = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                _counterIdentifier,
                OrderActorRole.Counter,
                [new LineRemovedOperation(lineIdentifier, "sent back")]),
            cancellationToken);

        Assert.True(removal.IsAppended);

        Assert.Empty(await Reads().ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));
    }

    /// <summary>
    /// The board is the open restaurant. A settled table's history is the counter's business (§11.3),
    /// not something the kitchen should still be able to undo.
    /// </summary>
    [Fact]
    public async Task AClosedSittingsLines_AreAbsent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid lineIdentifier) = await SendAsync(quantity: 1, note: null, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await FulfillAsync(orderIdentifier, lineIdentifier, cancellationToken);

        Assert.Single(await Reads().ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));

        await World().CloseSittingAsync(_sittingIdentifier, _counterIdentifier, 4.50m, cancellationToken);

        Assert.Empty(await Reads().ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));
    }

    [Fact]
    public async Task LinesComeBackMostRecentlyFulfilledFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid orderIdentifier, Guid firstLine) = await SendAsync(quantity: 1, note: null, cancellationToken);
        (_, Guid secondLine) = await SendAsync(quantity: 1, note: null, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await FulfillAsync(orderIdentifier, firstLine, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await FulfillAsync(orderIdentifier, secondLine, cancellationToken);

        IReadOnlyList<KitchenFulfilledLineView> lines = await Reads()
            .ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken);

        Assert.Equal(2, lines.Count);
        Assert.Equal(secondLine, lines[0].OrderLineIdentifier);
        Assert.Equal(firstLine, lines[1].OrderLineIdentifier);
    }

    /// <summary>A staff account with no display name falls back to its username, like the roster does.</summary>
    [Fact]
    public async Task APersonWithoutADisplayName_IsNamedByTheirUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid anonymousGuest = await World().AddPersonAsync("linus", null, cancellationToken);
        await World().JoinAsync(_sittingIdentifier, anonymousGuest, cancellationToken);

        Guid lineIdentifier = _identifiers.Create();
        AppendOrderEventResult send = await Mutations().AppendToLivingOrderAsync(
            _sittingIdentifier,
            anonymousGuest,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                anonymousGuest,
                OrderActorRole.Guest,
                [new LineAddedOperation(lineIdentifier, _menuItemIdentifier, 1, 0m, null)]),
            cancellationToken);

        Assert.True(send.IsAppended);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await FulfillAsync(send.GuestOrderIdentifier!.Value, lineIdentifier, cancellationToken);

        KitchenFulfilledLineView line = Assert.Single(await Reads()
            .ListRecentlyFulfilledLinesAsync(_clock.UtcNow - Window, cancellationToken));

        Assert.Equal("linus", line.PersonName);
    }

    private async Task<(Guid OrderIdentifier, Guid LineIdentifier)> SendAsync(
        int quantity,
        string? note,
        CancellationToken cancellationToken)
    {
        Guid lineIdentifier = _identifiers.Create();

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            _sittingIdentifier,
            _guestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                _guestIdentifier,
                OrderActorRole.Guest,
                [new LineAddedOperation(lineIdentifier, _menuItemIdentifier, quantity, 0m, note)]),
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

    private async Task RevertAsync(Guid orderIdentifier, Guid lineIdentifier, CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.FulfillmentReversal,
                _kitchenIdentifier,
                OrderActorRole.Kitchen,
                [new LineFulfillmentRevertedOperation(lineIdentifier)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private DapperKitchenBoardReads Reads() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;
}
