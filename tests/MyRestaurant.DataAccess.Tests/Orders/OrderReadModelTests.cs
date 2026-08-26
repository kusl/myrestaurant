using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

public sealed class OrderReadModelTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 4, 2, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    public OrderReadModelTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    public async Task Views_AndTheDomainFold_AgreeOnARandomisedEventSequence()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 7", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);

        Guid[] guests =
        [
            await World().AddPersonAsync("ada", "Ada", cancellationToken),
            await World().AddPersonAsync("grace", "Grace", cancellationToken),
            await World().AddPersonAsync("linus", null, cancellationToken),
        ];

        foreach (Guid guest in guests)
        {
            await World().JoinAsync(sittingIdentifier, guest, cancellationToken);
        }

        Guid kitchen = await World().AddPersonAsync("kim", "Kim", cancellationToken);
        Guid counter = await World().AddPersonAsync("cass", "Cass", cancellationToken);

        Guid[] menu =
        [
            await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken),
            await World().AddMenuItemAsync("Salad", 6.25m, cancellationToken),
            await World().AddMenuItemAsync("Pie", 3.75m, cancellationToken),
            await World().AddMenuItemAsync("Tea", 2.00m, cancellationToken),
        ];

        Random random = new(20260726);

        for (int step = 0; step < 60; step++)
        {
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);

            Guid guest = guests[random.Next(guests.Length)];
            Guid? orderIdentifier = await ReadModel()
                .FindLivingOrderAsync(sittingIdentifier, guest, cancellationToken);

            IReadOnlyList<OrderLineView> lines = orderIdentifier is { } known
                ? await ReadModel().ListLinesForOrderAsync(known, cancellationToken)
                : [];

            switch (random.Next(6))
            {
                case 0:
                case 1:
                    await SendAsync(sittingIdentifier, guest, menu, random, cancellationToken);
                    break;

                case 2 when lines.Count > 0:
                    await Mutations().AppendToLivingOrderAsync(
                        sittingIdentifier,
                        guest,
                        new ProposedOrderEvent(
                            OrderEventType.GuestSubmission,
                            guest,
                            OrderActorRole.Guest,
                            [new LineRemovedOperation(Pick(lines, random).OrderLineIdentifier, "changed my mind")]),
                        cancellationToken);
                    break;

                case 3 when orderIdentifier is { } toFulfil && lines.Count > 0:
                    await Mutations().AppendToOrderAsync(
                        toFulfil,
                        new ProposedOrderEvent(
                            OrderEventType.Fulfillment,
                            kitchen,
                            OrderActorRole.Kitchen,
                            [new LineFulfilledOperation(Pick(lines, random).OrderLineIdentifier)]),
                        cancellationToken);
                    break;

                case 4 when orderIdentifier is { } toRevert && lines.Count > 0:
                    await Mutations().AppendToOrderAsync(
                        toRevert,
                        new ProposedOrderEvent(
                            OrderEventType.FulfillmentReversal,
                            kitchen,
                            OrderActorRole.Kitchen,
                            [new LineFulfillmentRevertedOperation(Pick(lines, random).OrderLineIdentifier)]),
                        cancellationToken);
                    break;

                case 5 when orderIdentifier is { } toAdjust && lines.Count > 0:
                    await Mutations().AppendToOrderAsync(
                        toAdjust,
                        new ProposedOrderEvent(
                            OrderEventType.PriceAdjustment,
                            counter,
                            OrderActorRole.Counter,
                            [new LinePriceAdjustedOperation(
                                Pick(lines, random).OrderLineIdentifier,
                                random.Next(1, 900) / 100m,
                                "goodwill")]),
                        cancellationToken);
                    break;

                default:

                    if (orderIdentifier is { } toEdit)
                    {
                        await Mutations().AppendToOrderAsync(
                            toEdit,
                            new ProposedOrderEvent(
                                OrderEventType.StaffEdit,
                                counter,
                                OrderActorRole.Counter,
                                [new LineAddedOperation(
                                    _identifiers.Create(),
                                    menu[random.Next(menu.Length)],
                                    random.Next(1, 4),
                                    0m,
                                    null)]),
                            cancellationToken);
                    }

                    break;
            }
        }

        IReadOnlyList<OrderStateView> states = await ReadModel()
            .ListOrderStatesForSittingAsync(sittingIdentifier, cancellationToken);

        Assert.NotEmpty(states);
        Assert.True(
            states.Sum(state => state.PendingLineCount + state.FulfilledLineCount) > 5,
            "the randomised sequence should leave several live lines behind");

        foreach (OrderStateView state in states)
        {
            IReadOnlyList<OrderEvent> log = await EventLog()
                .ReadEventsAsync(state.GuestOrderIdentifier, cancellationToken);

            ProjectedOrder folded = OrderProjection.FromEvents(log);
            IReadOnlyList<OrderLineView> viewed = await ReadModel()
                .ListLinesForOrderAsync(state.GuestOrderIdentifier, cancellationToken);

            Assert.Equal(folded.Lines.Count, viewed.Count);

            Dictionary<Guid, OrderLineView> viewedByLine = viewed.ToDictionary(line => line.OrderLineIdentifier);

            foreach (ProjectedOrderLine expected in folded.Lines)
            {
                Assert.True(
                    viewedByLine.TryGetValue(expected.OrderLineIdentifier, out OrderLineView? actual),
                    $"the view is missing line {expected.OrderLineIdentifier} that the fold produced");

                Assert.Equal(expected.MenuItemIdentifier, actual!.MenuItemIdentifier);
                Assert.Equal(expected.Quantity, actual.Quantity);
                Assert.Equal(expected.CurrentUnitPriceAmount, actual.CurrentUnitPriceAmount);
                Assert.Equal(expected.CustomizationNote, actual.CustomizationNote);
                Assert.Equal(expected.IsFulfilled, actual.IsFulfilled);
                Assert.Equal(expected.AddedAt, actual.AddedAt);
                Assert.Equal(expected.AddedByOrderEventIdentifier, actual.AddedByOrderEventIdentifier);
                Assert.Equal(expected.LineTotalAmount, actual.LineTotalAmount);
            }

            Assert.Equal(folded.PendingLineCount, state.PendingLineCount);
            Assert.Equal(folded.FulfilledLineCount, state.FulfilledLineCount);
            Assert.Equal(folded.CurrentTotalAmount, state.CurrentTotalAmount);
            Assert.Equal(folded.FirstSubmittedAt, state.FirstSubmittedAt);
            Assert.Equal(folded.LastEventAt, state.LastEventAt);

            Assert.Equal(
                Enumerable.Range(1, log.Count).Select(number => (long)number).ToArray(),
                log.Select(orderEvent => orderEvent.SequenceNumber).ToArray());
        }

        IReadOnlyList<SittingBillEntry> bill = await ReadModel()
            .ListSittingBillAsync(sittingIdentifier, cancellationToken);

        Assert.Equal(states.Count, bill.Count);
        Assert.Equal(
            states.Sum(state => state.CurrentTotalAmount),
            bill.Sum(entry => entry.PersonTotalAmount));
    }

    [Fact]
    public async Task ALineKeepsThePriceItWasAddedAt_WhenTheMenuPriceMovesUnderneathIt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 2", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        Guid ada = await World().AddPersonAsync("ada", "Ada", cancellationToken);
        await World().JoinAsync(sittingIdentifier, ada, cancellationToken);
        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        AppendOrderEventResult sent = await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            ada,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                ada,
                OrderActorRole.Guest,
                [new LineAddedOperation(_identifiers.Create(), soup, 2, 0m, null)]),
            cancellationToken);

        await World().SetMenuItemAsync(soup, 9.99m, isActive: true, cancellationToken);

        OrderLineView line = Assert.Single(
            await ReadModel().ListLinesForOrderAsync(sent.GuestOrderIdentifier!.Value, cancellationToken));

        Assert.Equal(4.50m, line.CurrentUnitPriceAmount);
        Assert.Equal(9.00m, line.LineTotalAmount);

        Assert.Equal("Soup", line.MenuItemName);
    }

    [Fact]
    public async Task TheKitchenQueue_ShowsOnlyPendingLinesOfOpenSittings_AndNamesEveryoneItLists()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        Guid kitchen = await World().AddPersonAsync("kim", "Kim", cancellationToken);
        Guid counter = await World().AddPersonAsync("cass", "Cass", cancellationToken);

        Guid tableA = await World().AddTableAsync("Table A", cancellationToken);
        Guid sittingA = await World().OpenSittingAsync(tableA, cancellationToken);
        Guid ada = await World().AddPersonAsync("ada", "Ada", cancellationToken);
        await World().JoinAsync(sittingA, ada, cancellationToken);

        Guid pending = _identifiers.Create();
        Guid fulfilled = _identifiers.Create();
        Guid removed = _identifiers.Create();

        AppendOrderEventResult sentA = await Mutations().AppendToLivingOrderAsync(
            sittingA,
            ada,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                ada,
                OrderActorRole.Guest,
                [
                    new LineAddedOperation(pending, soup, 1, 0m, "no croutons"),
                    new LineAddedOperation(fulfilled, soup, 1, 0m, null),
                    new LineAddedOperation(removed, soup, 1, 0m, null),
                ]),
            cancellationToken);

        Guid orderA = sentA.GuestOrderIdentifier!.Value;

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Mutations().AppendToOrderAsync(
            orderA,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment, kitchen, OrderActorRole.Kitchen, [new LineFulfilledOperation(fulfilled)]),
            cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Mutations().AppendToLivingOrderAsync(
            sittingA,
            ada,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission, ada, OrderActorRole.Guest, [new LineRemovedOperation(removed, null)]),
            cancellationToken);

        Guid tableB = await World().AddTableAsync("Table B", cancellationToken);
        Guid sittingB = await World().OpenSittingAsync(tableB, cancellationToken);
        Guid linus = await World().AddPersonAsync("linus", null, cancellationToken);
        await World().JoinAsync(sittingB, linus, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Mutations().AppendToLivingOrderAsync(
            sittingB,
            linus,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                linus,
                OrderActorRole.Guest,
                [new LineAddedOperation(_identifiers.Create(), soup, 1, 0m, null)]),
            cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await World().CloseSittingAsync(sittingB, counter, 4.50m, cancellationToken);

        IReadOnlyList<KitchenPendingLineView> queue = await ReadModel()
            .ListKitchenPendingLinesAsync(cancellationToken);

        KitchenPendingLineView only = Assert.Single(queue);
        Assert.Equal(pending, only.OrderLineIdentifier);
        Assert.Equal("no croutons", only.CustomizationNote);
        Assert.Equal("Table A", only.TableLabel);
        Assert.Equal("Ada", only.PersonName);
        Assert.Equal(sittingA, only.SittingIdentifier);
    }

    [Fact]
    public async Task TheKitchenQueue_FallsBackToTheUsername_WhenSomeoneHasNoDisplayName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);
        Guid tableIdentifier = await World().AddTableAsync("Table 3", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);

        Guid linus = await World().AddPersonAsync("linus", null, cancellationToken);
        await World().JoinAsync(sittingIdentifier, linus, cancellationToken);

        await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            linus,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                linus,
                OrderActorRole.Guest,
                [new LineAddedOperation(_identifiers.Create(), soup, 1, 0m, null)]),
            cancellationToken);

        Assert.Equal(
            "linus",
            Assert.Single(await ReadModel().ListKitchenPendingLinesAsync(cancellationToken)).PersonName);
    }

    [Fact]
    public async Task TheEventLog_ReadsBackEveryOperationTypeInTheOrderItWasWritten()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableIdentifier = await World().AddTableAsync("Table 4", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        Guid ada = await World().AddPersonAsync("ada", "Ada", cancellationToken);
        await World().JoinAsync(sittingIdentifier, ada, cancellationToken);
        Guid kitchen = await World().AddPersonAsync("kim", "Kim", cancellationToken);
        Guid counter = await World().AddPersonAsync("cass", "Cass", cancellationToken);
        Guid soup = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        Guid keep = _identifiers.Create();
        Guid drop = _identifiers.Create();

        AppendOrderEventResult sent = await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            ada,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                ada,
                OrderActorRole.Guest,
                [
                    new LineAddedOperation(keep, soup, 2, 0m, "hot"),
                    new LineAddedOperation(drop, soup, 1, 0m, null),
                ]),
            cancellationToken);

        Guid orderIdentifier = sent.GuestOrderIdentifier!.Value;

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            ada,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission, ada, OrderActorRole.Guest, [new LineRemovedOperation(drop, "too much")]),
            cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.PriceAdjustment,
                counter,
                OrderActorRole.Counter,
                [new LinePriceAdjustedOperation(keep, 2.25m, "half portion")]),
            cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment, kitchen, OrderActorRole.Kitchen, [new LineFulfilledOperation(keep)]),
            cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.FulfillmentReversal,
                kitchen,
                OrderActorRole.Kitchen,
                [new LineFulfillmentRevertedOperation(keep)]),
            cancellationToken);

        IReadOnlyList<OrderEvent> log = await EventLog().ReadEventsAsync(orderIdentifier, cancellationToken);

        Assert.Equal(5, log.Count);
        Assert.Equal(
            new[]
            {
                OrderEventType.GuestSubmission,
                OrderEventType.GuestSubmission,
                OrderEventType.PriceAdjustment,
                OrderEventType.Fulfillment,
                OrderEventType.FulfillmentReversal,
            },
            log.Select(orderEvent => orderEvent.EventType).ToArray());

        Assert.Equal(
            new[]
            {
                OrderActorRole.Guest,
                OrderActorRole.Guest,
                OrderActorRole.Counter,
                OrderActorRole.Kitchen,
                OrderActorRole.Kitchen,
            },
            log.Select(orderEvent => orderEvent.ActorRole).ToArray());

        Dictionary<Guid, LineAddedOperation> adds = log[0].Operations
            .Select(operation => Assert.IsType<LineAddedOperation>(operation))
            .ToDictionary(added => added.OrderLineIdentifier);

        Assert.Equal(2, adds.Count);
        Assert.Equal(2, adds[keep].Quantity);
        Assert.Equal(4.50m, adds[keep].UnitPriceAmount);
        Assert.Equal("hot", adds[keep].CustomizationNote);
        Assert.Equal(1, adds[drop].Quantity);
        Assert.Null(adds[drop].CustomizationNote);

        LineRemovedOperation removal = Assert.IsType<LineRemovedOperation>(Assert.Single(log[1].Operations));
        Assert.Equal("too much", removal.Reason);

        LinePriceAdjustedOperation adjustment =
            Assert.IsType<LinePriceAdjustedOperation>(Assert.Single(log[2].Operations));
        Assert.Equal(2.25m, adjustment.NewUnitPriceAmount);
        Assert.Equal("half portion", adjustment.Reason);

        Assert.IsType<LineFulfilledOperation>(Assert.Single(log[3].Operations));
        Assert.IsType<LineFulfillmentRevertedOperation>(Assert.Single(log[4].Operations));

        Assert.Empty(await EventLog().ReadEventsAsync(_identifiers.Create(), cancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private DapperOrderReadModel ReadModel() => new(_connectionFactory!);

    private DapperOrderEventLog EventLog() => new(_connectionFactory!);

    private OrderTestWorld World() => _world!;

    private static OrderLineView Pick(IReadOnlyList<OrderLineView> lines, Random random)
        => lines[random.Next(lines.Count)];

    private async Task SendAsync(
        Guid sittingIdentifier,
        Guid guest,
        IReadOnlyList<Guid> menu,
        Random random,
        CancellationToken cancellationToken)
    {
        int count = random.Next(1, 4);
        List<OrderOperation> operations = new(count);

        for (int index = 0; index < count; index++)
        {
            operations.Add(new LineAddedOperation(
                _identifiers.Create(),
                menu[random.Next(menu.Count)],
                random.Next(1, 5),
                0m,
                random.Next(3) == 0 ? "extra hot" : null));
        }

        await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            guest,
            new ProposedOrderEvent(OrderEventType.GuestSubmission, guest, OrderActorRole.Guest, operations),
            cancellationToken);
    }
}
