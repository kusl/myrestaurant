using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Sittings;

public sealed class SittingRecordReadsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 4, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    private Guid _adaIdentifier;
    private Guid _bodeIdentifier;
    private Guid _counterIdentifier;
    private Guid _kitchenIdentifier;

    private Guid _namelessKitchenIdentifier;

    private Guid _soupIdentifier;
    private Guid _steakIdentifier;

    public SittingRecordReadsTests(PostgreSqlFixture fixture) => _fixture = fixture;

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

        _adaIdentifier = await _world.AddPersonAsync("ada", "Ada", cancellationToken);
        _bodeIdentifier = await _world.AddPersonAsync("bode", "Bo", cancellationToken);
        _counterIdentifier = await _world.AddPersonAsync("cass", "Cass Okonkwo", cancellationToken);
        _kitchenIdentifier = await _world.AddPersonAsync("kim", "Kim", cancellationToken);
        _namelessKitchenIdentifier = await _world.AddPersonAsync("pat", null, cancellationToken);

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
    public async Task ListOrderRecords_ReturnsOneRecordPerOrder_OldestFirst_WithTheOwnersName()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 3", cancellationToken, _adaIdentifier, _bodeIdentifier);

        await SendAsync(sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
        await SendAsync(sittingIdentifier, _bodeIdentifier, _steakIdentifier, 1, null, cancellationToken);

        IReadOnlyList<SittingOrderRecord> records =
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken);

        Assert.Equal(2, records.Count);

        Assert.Equal(_adaIdentifier, records[0].PersonIdentifier);
        Assert.Equal(sittingIdentifier, records[0].SittingIdentifier);
        Assert.Equal("ada", records[0].Username);
        Assert.Equal("Ada", records[0].DisplayName);
        Assert.Equal("Ada", records[0].OwnerName);

        Assert.Equal(_bodeIdentifier, records[1].PersonIdentifier);

        Assert.True(records[0].CreatedAt < records[1].CreatedAt);
    }

    [Fact]
    public async Task ListOrderRecords_CarriesEveryEventInSequence_WithItsStoredTypeAndActorRole()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 4", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, Guid lineIdentifier) =
            await SendAsync(sittingIdentifier, _adaIdentifier, _soupIdentifier, 2, null, cancellationToken);

        await AdjustAsync(orderIdentifier, lineIdentifier, 3.00m, "goodwill", cancellationToken);
        await FulfillAsync(orderIdentifier, lineIdentifier, _kitchenIdentifier, cancellationToken);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        Assert.Equal(3, record.Events.Count);

        Assert.Equal(1L, record.Events[0].SequenceNumber);
        Assert.Equal("guest_submission", record.Events[0].EventType);
        Assert.Equal("guest", record.Events[0].ActorRole);
        Assert.Equal(_adaIdentifier, record.Events[0].ActorPersonIdentifier);
        Assert.Equal("Ada", record.Events[0].ActorName);

        Assert.Equal(2L, record.Events[1].SequenceNumber);
        Assert.Equal("price_adjustment", record.Events[1].EventType);
        Assert.Equal("counter", record.Events[1].ActorRole);
        Assert.Equal("Cass Okonkwo", record.Events[1].ActorName);

        Assert.Equal(3L, record.Events[2].SequenceNumber);
        Assert.Equal("fulfillment", record.Events[2].EventType);
        Assert.Equal("kitchen", record.Events[2].ActorRole);

        Assert.All(record.Events, stored => Assert.Equal(orderIdentifier, stored.GuestOrderIdentifier));

        Assert.Equal([1L, 2L, 3L], record.Events.Select(stored => stored.SequenceNumber));
    }

    [Fact]
    public async Task ListOrderRecords_NamesTheActor_FallingBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 5", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, Guid lineIdentifier) =
            await SendAsync(sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);

        await FulfillAsync(orderIdentifier, lineIdentifier, _namelessKitchenIdentifier, cancellationToken);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        StoredOrderEvent fulfillment = record.Events.Single(stored => stored.EventType == "fulfillment");

        Assert.Equal("pat", fulfillment.ActorName);
    }

    [Fact]
    public async Task ListOrderRecords_ALineAdded_CarriesTheItemQuantityPriceAndNote()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 6", cancellationToken, _adaIdentifier);

        (_, Guid lineIdentifier) = await SendAsync(
            sittingIdentifier, _adaIdentifier, _soupIdentifier, 3, "no cream", cancellationToken);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        StoredOrderOperation added = Assert.Single(Assert.Single(record.Events).Operations);

        Assert.Equal("line_added", added.OperationKind);
        Assert.Equal(lineIdentifier, added.OrderLineIdentifier);
        Assert.Equal(_soupIdentifier, added.MenuItemIdentifier);
        Assert.Equal("Soup", added.MenuItemName);
        Assert.Equal(3, added.Quantity);

        Assert.Equal(4.50m, added.UnitPriceAmount);
        Assert.Equal("no cream", added.CustomizationNote);

        Assert.Null(added.NewUnitPriceAmount);
        Assert.Null(added.Reason);
    }

    [Fact]
    public async Task ListOrderRecords_ARemoval_CarriesItsReasonAndNamesTheItemItTookOff()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 7", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, Guid lineIdentifier) =
            await SendAsync(sittingIdentifier, _adaIdentifier, _steakIdentifier, 2, null, cancellationToken);

        await RemoveAsync(orderIdentifier, lineIdentifier, "sent back", cancellationToken);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        StoredOrderOperation removed = Assert.Single(
            record.Events.Single(stored => stored.EventType == "staff_edit").Operations);

        Assert.Equal("line_removed", removed.OperationKind);
        Assert.Equal(lineIdentifier, removed.OrderLineIdentifier);
        Assert.Equal("sent back", removed.Reason);

        Assert.Equal(_steakIdentifier, removed.MenuItemIdentifier);
        Assert.Equal("Steak", removed.MenuItemName);
        Assert.Equal(2, removed.Quantity);
    }

    [Fact]
    public async Task ListOrderRecords_ARemovedLine_IsGoneFromTheProjectionAndStillInTheRecord()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 8", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, Guid lineIdentifier) =
            await SendAsync(sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);

        await RemoveAsync(orderIdentifier, lineIdentifier, reason: null, cancellationToken);

        Assert.Empty(await ReadModel().ListLinesForSittingAsync(sittingIdentifier, cancellationToken));

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        Assert.Equal(2, record.Events.Count);
        Assert.Contains(
            record.Events.SelectMany(stored => stored.Operations),
            operation => operation.OperationKind == "line_added" && operation.OrderLineIdentifier == lineIdentifier);
        Assert.Contains(
            record.Events.SelectMany(stored => stored.Operations),
            operation => operation.OperationKind == "line_removed" && operation.OrderLineIdentifier == lineIdentifier);

        StoredOrderOperation removed = record.Events
            .SelectMany(stored => stored.Operations)
            .Single(operation => operation.OperationKind == "line_removed");

        Assert.Null(removed.Reason);
    }

    [Fact]
    public async Task ListOrderRecords_APriceAdjustment_CarriesTheNewPriceAndItsRequiredReason()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 9", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, Guid lineIdentifier) =
            await SendAsync(sittingIdentifier, _adaIdentifier, _steakIdentifier, 1, null, cancellationToken);

        await AdjustAsync(orderIdentifier, lineIdentifier, 15.00m, "burnt, half off", cancellationToken);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        StoredOrderOperation adjusted = Assert.Single(
            record.Events.Single(stored => stored.EventType == "price_adjustment").Operations);

        Assert.Equal("line_price_adjusted", adjusted.OperationKind);
        Assert.Equal(15.00m, adjusted.NewUnitPriceAmount);
        Assert.Equal("burnt, half off", adjusted.Reason);
        Assert.Equal("Steak", adjusted.MenuItemName);

        Assert.Null(adjusted.UnitPriceAmount);

        StoredOrderOperation added = record.Events
            .SelectMany(stored => stored.Operations)
            .Single(operation => operation.OperationKind == "line_added");

        Assert.Equal(21.00m, added.UnitPriceAmount);
    }

    [Fact]
    public async Task ListOrderRecords_AFulfillmentAndItsReversal_AreBothStillInTheRecord()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 10", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, Guid lineIdentifier) =
            await SendAsync(sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);

        await FulfillAsync(orderIdentifier, lineIdentifier, _kitchenIdentifier, cancellationToken);
        await RevertFulfillmentAsync(orderIdentifier, lineIdentifier, cancellationToken);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        Assert.Equal(
            ["guest_submission", "fulfillment", "fulfillment_reversal"],
            record.Events.Select(stored => stored.EventType));

        StoredOrderOperation reverted = Assert.Single(
            record.Events.Single(stored => stored.EventType == "fulfillment_reversal").Operations);

        Assert.Equal("line_fulfillment_reverted", reverted.OperationKind);
        Assert.Equal(lineIdentifier, reverted.OrderLineIdentifier);
        Assert.Equal("Soup", reverted.MenuItemName);

        Assert.Single(record.Events, stored => stored.EventType == "fulfillment");
    }

    [Fact]
    public async Task ListOrderRecords_OneEventWithSeveralOperations_KeepsThemAllOnThatEvent()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 11", cancellationToken, _adaIdentifier);

        Guid soupLine = _identifiers.Create();
        Guid steakLine = _identifiers.Create();

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            sittingIdentifier,
            _adaIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                _adaIdentifier,
                OrderActorRole.Guest,
                [
                    new LineAddedOperation(soupLine, _soupIdentifier, 2, 0m, null),
                    new LineAddedOperation(steakLine, _steakIdentifier, 1, 0m, "rare"),
                ]),
            cancellationToken);

        Assert.True(result.IsAppended);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        StoredOrderEvent send = Assert.Single(record.Events);
        Assert.Equal(2, send.Operations.Count);
        Assert.All(send.Operations, operation => Assert.Equal(send.OrderEventIdentifier, operation.OrderEventIdentifier));

        Assert.Contains(send.Operations, operation => operation.OrderLineIdentifier == soupLine);
        Assert.Contains(send.Operations, operation => operation.OrderLineIdentifier == steakLine);
    }

    [Fact]
    public async Task ListOrderRecords_OrdersInOtherSittings_AreNotIncluded()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid firstSitting = await OpenTableAsync("Table 12", cancellationToken, _adaIdentifier);
        Guid secondSitting = await OpenTableAsync("Table 13", cancellationToken, _bodeIdentifier);

        await SendAsync(firstSitting, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);
        await SendAsync(secondSitting, _bodeIdentifier, _steakIdentifier, 1, null, cancellationToken);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(firstSitting, cancellationToken));

        Assert.Equal(_adaIdentifier, record.PersonIdentifier);

        StoredOrderOperation added = Assert.Single(Assert.Single(record.Events).Operations);
        Assert.Equal("Soup", added.MenuItemName);
    }

    [Fact]
    public async Task ListOrderRecords_AnUnknownSitting_IsEmpty()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Empty(await Reads().ListOrderRecordsForSittingAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task ListOrderRecords_ASittingNobodyOrderedIn_IsEmpty()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 14", cancellationToken, _adaIdentifier, _bodeIdentifier);

        Assert.Empty(await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));
    }

    [Fact]
    public async Task ListOrderRecords_AnAdministratorsPostCloseCorrection_IsInTheRecord()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 15", cancellationToken, _adaIdentifier);

        (Guid orderIdentifier, _) =
            await SendAsync(sittingIdentifier, _adaIdentifier, _soupIdentifier, 1, null, cancellationToken);

        CloseSittingResult closed = await Settlement()
            .CloseAndSettleAsync(sittingIdentifier, _counterIdentifier, cancellationToken);

        Assert.Equal(CloseSittingOutcome.Closed, closed.Outcome);
        Assert.Equal(4.50m, closed.SettledTotalAmount);

        Guid correctionLine = _identifiers.Create();
        AppendOrderEventResult correction = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                _counterIdentifier,
                OrderActorRole.Administrator,
                [new LineAddedOperation(correctionLine, _steakIdentifier, 1, 0m, "billed late")]),
            cancellationToken);

        Assert.True(correction.IsAppended);

        SittingOrderRecord record = Assert.Single(
            await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));

        StoredOrderEvent appended = record.Events.Single(stored => stored.EventType == "staff_edit");

        Assert.Equal("administrator", appended.ActorRole);
        Assert.Equal(2L, appended.SequenceNumber);

        StoredOrderOperation added = Assert.Single(appended.Operations);
        Assert.Equal(correctionLine, added.OrderLineIdentifier);
        Assert.Equal("Steak", added.MenuItemName);
        Assert.Equal("billed late", added.CustomizationNote);

        CounterSittingSummary summary =
            (await new DapperCounterBoardReads(_connectionFactory!).GetSittingAsync(sittingIdentifier, cancellationToken))!;

        Assert.Equal(4.50m, summary.SettledTotalAmount);
        Assert.Equal(25.50m, summary.CurrentTotalAmount);
        Assert.True(summary.HasPostCloseCorrections);
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

    private async Task<(Guid OrderIdentifier, Guid LineIdentifier)> SendAsync(
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

    private async Task FulfillAsync(
        Guid orderIdentifier,
        Guid lineIdentifier,
        Guid kitchenPersonIdentifier,
        CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            orderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment,
                kitchenPersonIdentifier,
                OrderActorRole.Kitchen,
                [new LineFulfilledOperation(lineIdentifier)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private async Task RevertFulfillmentAsync(
        Guid orderIdentifier,
        Guid lineIdentifier,
        CancellationToken cancellationToken)
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

    private DapperSittingRecordReads Reads() => new(_connectionFactory!);

    private DapperOrderReadModel ReadModel() => new(_connectionFactory!);

    private DapperSittingSettlement Settlement() => new(_connectionFactory!, _clock);

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;
}
