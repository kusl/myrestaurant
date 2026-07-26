using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

/// <summary>
/// Integration tests for <see cref="DapperOrderReadModel"/> and <see cref="DapperOrderEventLog"/>
/// against a real PostgreSQL 17 container — and, most importantly, for TECHNICAL_SPECIFICATION §8.5:
/// the SQL projection views and the pure domain fold must agree.
///
/// <para>§8.5 is the load-bearing claim of the whole event model. The event tables are the source of
/// truth (ADR-0002); the views are how the application reads them and the fold is how the domain reasons
/// about them, and if the two ever disagree, one of the guest's screen and the counter's bill is lying.
/// <see cref="Views_AndTheDomainFold_AgreeOnARandomisedEventSequence"/> is the assertion that keeps them
/// honest: it drives dozens of real events — sends, removals, fulfillments, reversals, price
/// adjustments, staff edits — through the real transaction with a seeded random generator, then compares
/// every field of every line, both counts, and the total, order by order.</para>
///
/// <para>The generator is seeded deliberately rather than time-based: a projection bug that reproduces
/// only on Tuesdays is worse than no test. Some generated events are rejected by §6.5 (a guest trying to
/// remove a line the counter added, a reversal of something not fulfilled) and that is left in on
/// purpose — a rejected event must leave the log and the views equally untouched.</para>
/// </summary>
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
            // A distinct instant per step, so `added_at` ordering is total and both sides sort the same
            // way. Whole seconds keep the DateTimeOffset and the timestamptz bit-identical.
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
                    // A staff edit from the counter: adds a line to whichever order it is looking at,
                    // which is also what makes some later guest removals fail (§6.5.3) — deliberately.
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

        // The generator has to have actually produced something, or this test would pass vacuously.
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

            // §8.5's contract is the line *set*, its prices, and its fulfillment flags — not a row
            // order. It has to be, because lines added in one send share an `occurred_at` to the
            // microsecond, and the tie-breaker cannot agree: the fold's `ThenBy(Guid)` uses .NET's
            // Guid.CompareTo (Data1 as an int, then two shorts, then bytes) while the view's ORDER BY
            // uses PostgreSQL's bytewise uuid collation. Both are stable and neither is wrong; asserting
            // one against the other would be asserting an accident.
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

            // …and the same summary, which is what the guest's running total and the bill are built on.
            Assert.Equal(folded.PendingLineCount, state.PendingLineCount);
            Assert.Equal(folded.FulfilledLineCount, state.FulfilledLineCount);
            Assert.Equal(folded.CurrentTotalAmount, state.CurrentTotalAmount);
            Assert.Equal(folded.FirstSubmittedAt, state.FirstSubmittedAt);
            Assert.Equal(folded.LastEventAt, state.LastEventAt);

            // Sequence numbers are dense and monotonic from 1 (§6.2), assigned under the order lock.
            Assert.Equal(
                Enumerable.Range(1, log.Count).Select(number => (long)number).ToArray(),
                log.Select(orderEvent => orderEvent.SequenceNumber).ToArray());
        }

        // The per-person bill is the same arithmetic seen from the sitting's side (§8.3).
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

        // §7 / §6.5.4: "Prices on existing lines never move when the menu price changes."
        await World().SetMenuItemAsync(soup, 9.99m, isActive: true, cancellationToken);

        OrderLineView line = Assert.Single(
            await ReadModel().ListLinesForOrderAsync(sent.GuestOrderIdentifier!.Value, cancellationToken));

        Assert.Equal(4.50m, line.CurrentUnitPriceAmount);
        Assert.Equal(9.00m, line.LineTotalAmount);

        // The name, though, is a read-time join, so a rename shows through immediately (§8.3).
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

        // Table A stays open: one line is fulfilled, one is removed, one is left pending.
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

        // Table B is closed with a pending line still on it — the kitchen must not still be cooking it.
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

        // §11.2 groups the queue by person display name; a freshly created account has none, and a
        // blank ticket header is worse than a username.
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

        // The first event owns both adds. They are compared as a set, not a sequence: the schema records
        // no ordinal within an event, and the surrogate keys that give the read a deterministic order are
        // UUIDv7s whose random bits decide ties inside one millisecond.
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

        // And the log an unknown order has is empty, not an exception: no order yet and no events yet
        // are the same answer to a reader (§6.1).
        Assert.Empty(await EventLog().ReadEventsAsync(_identifiers.Create(), cancellationToken));
    }

    // --- helpers -----------------------------------------------------------------------------------

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
