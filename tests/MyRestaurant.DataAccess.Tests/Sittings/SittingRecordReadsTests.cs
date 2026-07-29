using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.DataAccess.Tests.Orders;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Sittings;

/// <summary>
/// Integration tests for <see cref="DapperSittingRecordReads"/> against a real PostgreSQL 17 container —
/// TECHNICAL_SPECIFICATION §11.4's "complete stored record … never projected or truncated", which is what
/// administration renders and what an administrator reads before appending a §6.7 correction.
///
/// <para>Two things are being asserted here and they pull in opposite directions. The first is
/// <em>completeness</em>: a removed line, an undone fulfillment, and a superseded price must all still be
/// in the answer, because a projection is exactly what this reader must not be. The second is
/// <em>legibility</em>: every operation has to name its item and quantity, which the four non-adding
/// operation tables do not store — they carry only <c>order_line_identifier</c> — so the reader joins back
/// through <c>order_operation_line_added</c>, whose <c>order_line_identifier</c> is NOT NULL UNIQUE and is
/// the declared FK target of all four. If that join is wrong the symptom is not an exception: it is a
/// history that silently loses rows, which is the worst shape of bug an audit trail can have.</para>
///
/// <para>Each test truncates first (xUnit builds a fresh instance per test and runs them sequentially).
/// Own <c>IClassFixture</c>, own container; if no container engine is available, every test skips.</para>
/// </summary>
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

    /// <summary>A kitchen hand with no display name — the actor-name fallback (§5.2's rendering rule).</summary>
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

        // Three characters minimum: person.username carries CHECK (char_length BETWEEN 3 AND 64) (§8.2).
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

        // The order rows were created lazily inside each first send (§6.1), so their created_at is the
        // instant of that send and the list is ordered by it.
        Assert.True(records[0].CreatedAt < records[1].CreatedAt);
    }

    /// <summary>
    /// The stored words, not an enum. §11.4 renders the record, and the surface labels the five values
    /// §8.2's CHECK admits while falling back to the raw string — which only works if the raw string is
    /// what arrives here.
    /// </summary>
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

        // Every event carries the order it belongs to, so a caller flattening several records keeps the
        // association without re-deriving it.
        Assert.All(record.Events, stored => Assert.Equal(orderIdentifier, stored.GuestOrderIdentifier));

        // Ascending, and monotonic without gaps — the sequence is assigned under the order lock (§6.6).
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

        // A blank display name must not produce a blank line in the history: the record says who did it.
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

        // The price the transaction captured from the menu under its own lock (§6.5.4), not the zero the
        // caller sent.
        Assert.Equal(4.50m, added.UnitPriceAmount);
        Assert.Equal("no cream", added.CustomizationNote);

        // Columns that belong to other kinds stay null on this one.
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

        // order_operation_line_removed stores only the line identifier and the reason. Both of these come
        // from the join back to the adding row, and they are the difference between a readable history and
        // a column of UUIDs.
        Assert.Equal(_steakIdentifier, removed.MenuItemIdentifier);
        Assert.Equal("Steak", removed.MenuItemName);
        Assert.Equal(2, removed.Quantity);
    }

    /// <summary>
    /// The whole reason this reader exists beside <see cref="IOrderReadModel"/>. The projection is
    /// correct to drop a removed line; the record would be wrong to.
    /// </summary>
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

        // A removal with no reason is legal (§11.3: "optional reason on removal") and reads as null rather
        // than as an empty string, so a surface can tell "no reason given" from "the reason was blank".
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

        // The captured price is not restated on the adjustment row — the record must not invent a number
        // for a column the table does not have. The original is on the line_added operation above it,
        // which is exactly where somebody settling a price argument reads it from.
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

        // The undo does not erase the fulfillment; both are events, and §6.4's line lifecycle is read off
        // the pair rather than off a flag somebody overwrote.
        Assert.Single(record.Events, stored => stored.EventType == "fulfillment");
    }

    /// <summary>
    /// One event, several operations — a guest's batch send (§6.3). All of them must land on that event
    /// and none on a neighbour, which is what the group-by-event step in the reader is for.
    /// </summary>
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

    /// <summary>
    /// A table where everybody joined and nobody sent anything has no <c>guest_order</c> row at all (§6.1
    /// creates it lazily), so the honest answer is an empty record rather than an error — the page above
    /// already knows the sitting exists from its own header query.
    /// </summary>
    [Fact]
    public async Task ListOrderRecords_ASittingNobodyOrderedIn_IsEmpty()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid sittingIdentifier = await OpenTableAsync("Table 14", cancellationToken, _adaIdentifier, _bodeIdentifier);

        Assert.Empty(await Reads().ListOrderRecordsForSittingAsync(sittingIdentifier, cancellationToken));
    }

    /// <summary>
    /// §6.7's whole point: after a close, an administrator's corrective event joins the record and the
    /// stamped total does not move. This is the read the administration surface renders that correction
    /// from.
    /// </summary>
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

        // §6.5.8: after a close only an administrator may append, and never a guest submission.
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

        // The stamped total is untouched — the correction lives beside it (§5.3).
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

        // The zero unit price is deliberate: §6.5.4 has the transaction price the line from the menu row
        // it reads under the lock, so anything sent here is discarded.
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
