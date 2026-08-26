using MyRestaurant.DataAccess.Orders;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Orders;

public sealed class KitchenNotificationsTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const int ReminderSeconds = 60;

    private const string CountRemindersSql = """
        SELECT count(*)::int FROM kitchen_notification WHERE kind = 'reminder';
        """;

    private const string CountInitialAlertsSql = """
        SELECT count(*)::int FROM kitchen_notification WHERE kind = 'initial';
        """;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 5, 14, 18, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;
    private OrderTestWorld? _world;

    public KitchenNotificationsTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    public async Task ASendYoungerThanTheThreshold_IsNotReminded()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);

        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountRemindersSql, cancellationToken));
    }

    [Fact]
    public async Task ASendOlderThanTheThreshold_IsRemindedExactlyOnce()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult send = await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(90);

        IReadOnlyList<KitchenReminderIssued> first = await Notifications()
            .IssueDueRemindersAsync(ReminderSeconds, cancellationToken);

        KitchenReminderIssued issued = Assert.Single(first);
        Assert.Equal(send.OrderEventIdentifier, issued.OrderEventIdentifier);
        Assert.Equal(send.GuestOrderIdentifier, issued.GuestOrderIdentifier);
        Assert.Equal(scene.SittingIdentifier, issued.SittingIdentifier);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));

        Assert.Equal(1, await World().CountAsync(CountRemindersSql, cancellationToken));
    }

    [Fact]
    public async Task TheReminderRowSitsBesideTheInitialAlert()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult send = await SendAsync(scene, cancellationToken);
        Assert.True(send.KitchenNotificationWritten);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(90);
        Assert.Single(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));

        Assert.Equal(1, await World().CountAsync(CountInitialAlertsSql, cancellationToken));
        Assert.Equal(1, await World().CountAsync(CountRemindersSql, cancellationToken));

        string? storedEventType = await World().ScalarAsync<string>(
            "SELECT event_type FROM kitchen_notification WHERE kind = 'reminder';",
            null,
            cancellationToken);

        Assert.Equal("guest_submission", storedEventType);
    }

    [Fact]
    public async Task ASendWhoseLineWasFulfilled_IsNotReminded()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult send = await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(20);
        await FulfillAsync(scene, send.GuestOrderIdentifier!.Value, scene.FirstLineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(90);

        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));
    }

    [Fact]
    public async Task ASendWhoseLineWasRemoved_IsNotReminded()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult send = await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(20);
        await StaffRemoveAsync(scene, send.GuestOrderIdentifier!.Value, scene.FirstLineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(90);

        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));
    }

    [Fact]
    public async Task ASendIsNotReminded_WhenOnlySomeOfItsLinesWereFulfilled()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult send = await SendAsync(scene, cancellationToken, lineCount: 3);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(20);
        await FulfillAsync(scene, send.GuestOrderIdentifier!.Value, scene.FirstLineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(90);

        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));
    }

    [Fact]
    public async Task APureRemovalSend_NeverReminds()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult firstSend = await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(20);

        AppendOrderEventResult removalSend = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.GuestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                scene.GuestIdentifier,
                OrderActorRole.Guest,
                [new LineRemovedOperation(scene.FirstLineIdentifier, "changed my mind")]),
            cancellationToken);

        Assert.True(removalSend.IsAppended);

        Assert.True(removalSend.KitchenNotificationWritten);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(120);

        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));
        Assert.Equal(0, await World().CountAsync(CountRemindersSql, cancellationToken));
        Assert.NotNull(firstSend.OrderEventIdentifier);
    }

    [Fact]
    public async Task AClosedSitting_IsNotReminded()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        await World().CloseSittingAsync(scene.SittingIdentifier, scene.CounterIdentifier, 4.50m, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(90);

        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));
    }

    [Fact]
    public async Task EachOverdueSendGetsItsOwnReminder()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult first = await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(10);
        AppendOrderEventResult second = await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(120);

        IReadOnlyList<KitchenReminderIssued> issued = await Notifications()
            .IssueDueRemindersAsync(ReminderSeconds, cancellationToken);

        Assert.Equal(2, issued.Count);
        Assert.Contains(issued, reminder => reminder.OrderEventIdentifier == first.OrderEventIdentifier);
        Assert.Contains(issued, reminder => reminder.OrderEventIdentifier == second.OrderEventIdentifier);
    }

    [Fact]
    public async Task AStaffEditIsNeverReminded_EvenWhenItAddsLines()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Scene scene = await ArrangeAsync(cancellationToken);
        AppendOrderEventResult send = await SendAsync(scene, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(10);

        AppendOrderEventResult staffEdit = await Mutations().AppendToOrderAsync(
            send.GuestOrderIdentifier!.Value,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                scene.CounterIdentifier,
                OrderActorRole.Counter,
                [new LineAddedOperation(_identifiers.Create(), scene.MenuItemIdentifier, 1, 0m, null)]),
            cancellationToken);

        Assert.True(staffEdit.IsAppended);

        await FulfillAsync(scene, send.GuestOrderIdentifier!.Value, scene.FirstLineIdentifier, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(120);

        Assert.Empty(await Notifications().IssueDueRemindersAsync(ReminderSeconds, cancellationToken));
    }

    private async Task<Scene> ArrangeAsync(CancellationToken cancellationToken)
    {
        Guid tableIdentifier = await World().AddTableAsync("Table 4", cancellationToken);
        Guid sittingIdentifier = await World().OpenSittingAsync(tableIdentifier, cancellationToken);
        Guid guestIdentifier = await World().AddPersonAsync("ada", "Ada", cancellationToken);
        await World().JoinAsync(sittingIdentifier, guestIdentifier, cancellationToken);

        Guid kitchenIdentifier = await World().AddPersonAsync("kim", "Kim", cancellationToken);
        Guid counterIdentifier = await World().AddPersonAsync("cass", "Cass", cancellationToken);
        Guid menuItemIdentifier = await World().AddMenuItemAsync("Soup", 4.50m, cancellationToken);

        return new Scene
        {
            TableIdentifier = tableIdentifier,
            SittingIdentifier = sittingIdentifier,
            GuestIdentifier = guestIdentifier,
            KitchenIdentifier = kitchenIdentifier,
            CounterIdentifier = counterIdentifier,
            MenuItemIdentifier = menuItemIdentifier,
        };
    }

    private async Task<AppendOrderEventResult> SendAsync(
        Scene scene,
        CancellationToken cancellationToken,
        int lineCount = 1)
    {
        List<OrderOperation> operations = new(lineCount);
        Guid firstLineIdentifier = Guid.Empty;

        for (int index = 0; index < lineCount; index++)
        {
            Guid lineIdentifier = _identifiers.Create();
            if (index == 0)
            {
                firstLineIdentifier = lineIdentifier;
            }

            operations.Add(new LineAddedOperation(lineIdentifier, scene.MenuItemIdentifier, 1, 0m, null));
        }

        AppendOrderEventResult result = await Mutations().AppendToLivingOrderAsync(
            scene.SittingIdentifier,
            scene.GuestIdentifier,
            new ProposedOrderEvent(
                OrderEventType.GuestSubmission,
                scene.GuestIdentifier,
                OrderActorRole.Guest,
                operations),
            cancellationToken);

        Assert.True(result.IsAppended);
        scene.FirstLineIdentifier = firstLineIdentifier;
        return result;
    }

    private async Task FulfillAsync(
        Scene scene,
        Guid guestOrderIdentifier,
        Guid orderLineIdentifier,
        CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            guestOrderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.Fulfillment,
                scene.KitchenIdentifier,
                OrderActorRole.Kitchen,
                [new LineFulfilledOperation(orderLineIdentifier)]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private async Task StaffRemoveAsync(
        Scene scene,
        Guid guestOrderIdentifier,
        Guid orderLineIdentifier,
        CancellationToken cancellationToken)
    {
        AppendOrderEventResult result = await Mutations().AppendToOrderAsync(
            guestOrderIdentifier,
            new ProposedOrderEvent(
                OrderEventType.StaffEdit,
                scene.CounterIdentifier,
                OrderActorRole.Counter,
                [new LineRemovedOperation(orderLineIdentifier, "eighty-sixed")]),
            cancellationToken);

        Assert.True(result.IsAppended);
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperOrderMutations Mutations() => new(_connectionFactory!, _clock, _identifiers);

    private DapperKitchenNotifications Notifications() => new(_connectionFactory!, _clock, _identifiers);

    private OrderTestWorld World() => _world!;

    private sealed class Scene
    {
        public required Guid TableIdentifier { get; init; }

        public required Guid SittingIdentifier { get; init; }

        public required Guid GuestIdentifier { get; init; }

        public required Guid KitchenIdentifier { get; init; }

        public required Guid CounterIdentifier { get; init; }

        public required Guid MenuItemIdentifier { get; init; }

        public Guid FirstLineIdentifier { get; set; }
    }
}
