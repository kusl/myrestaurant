using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Orders;

public sealed class KitchenReminderService : BackgroundService
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDomainEventBroadcaster _broadcaster;
    private readonly RestaurantMetrics _metrics;
    private readonly RestaurantOptions _options;
    private readonly ILogger<KitchenReminderService> _logger;

    public KitchenReminderService(
        IServiceScopeFactory scopeFactory,
        IDomainEventBroadcaster broadcaster,
        RestaurantMetrics metrics,
        RestaurantOptions options,
        ILogger<KitchenReminderService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Kitchen reminder service started; scanning every {ScanSeconds}s for guest submissions older than {ReminderSeconds}s.",
            ScanInterval.TotalSeconds,
            _options.KitchenSubmissionReminderSeconds);

        using PeriodicTimer timer = new(ScanInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IKitchenNotifications notifications = scope.ServiceProvider
                .GetRequiredService<IKitchenNotifications>();

            IReadOnlyList<KitchenReminderIssued> issued = await notifications
                .IssueDueRemindersAsync(_options.KitchenSubmissionReminderSeconds, cancellationToken)
                .ConfigureAwait(false);

            foreach (KitchenReminderIssued reminder in issued)
            {
                _metrics.RecordKitchenReminderSent();
                _broadcaster.Publish(new KitchenAlert(reminder.OrderEventIdentifier, KitchenAlertKind.Reminder));

                _logger.LogInformation(
                    "Kitchen reminder issued for order event {OrderEventIdentifier} on sitting {SittingIdentifier}.",
                    reminder.OrderEventIdentifier,
                    reminder.SittingIdentifier);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The kitchen reminder scan failed; it will run again in {ScanSeconds}s.", ScanInterval.TotalSeconds);
        }
    }
}
