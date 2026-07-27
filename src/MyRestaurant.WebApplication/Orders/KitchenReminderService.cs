using MyRestaurant.DataAccess.Orders;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// The §10.2 reminder loop (TECHNICAL_SPECIFICATION §8.4, §10.2, §12). Every
/// <see cref="ScanInterval"/> it runs the §8.4 scan and, for each reminder row it actually wrote,
/// increments <c>kitchen_reminders_sent_total</c> and broadcasts <c>KitchenAlert(reminder)</c>.
///
/// <para>This is the one thing in the system that happens because <em>nobody</em> did anything, which is
/// why it cannot be a consequence of a write and has to be a timer. A guest sends, the kitchen is busy,
/// the ticket scrolls, and a minute later the send is still untouched: §10.2 says that gets exactly one
/// second alert, and the SQL that decides it is normative (§8.4).</para>
///
/// <para><b>Everything that makes it safe lives below this class.</b> The "exactly one" is the
/// <c>UNIQUE (order_event_identifier, kind)</c> constraint, not a flag in memory; the "only if the
/// insert took" is the <c>RETURNING</c> clause. So a restart mid-scan, two overlapping ticks, or (were
/// there ever one) a second web replica cannot double-alert. That matters because this service is the
/// only part of the application whose bug is silence, and silence is not something a cook notices until
/// a table complains.</para>
///
/// <para><b>A failed tick is logged and the loop continues.</b> The database being briefly unreachable
/// must not kill the reminder service for the rest of the process's life — the ticket that was due will
/// simply be due again in five seconds, because nothing about the scan is stateful.</para>
/// </summary>
public sealed class KitchenReminderService : BackgroundService
{
    /// <summary>
    /// §8.4: "The reminder background service (§10.2) runs every ~5 seconds". The scan is cheap and
    /// indexed; the interval is the resolution of the reminder, so a send configured to remind at 60
    /// seconds reminds somewhere in 60–65, which is well inside what the requirement means.
    /// </summary>
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

        // WaitForNextTickAsync returns false on cancellation rather than throwing, so shutdown is an
        // ordinary loop exit and the host is not asked to swallow an exception on every stop.
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One pass of §8.4. Exposed as its own method so the failure boundary is obvious: nothing thrown
    /// inside a tick escapes into the loop.
    /// </summary>
    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A scope per tick, not per process: the notification service is scoped like every other
            // data service, and a singleton holding one for the lifetime of the app would keep a
            // connection-factory-derived object alive across configuration it never re-reads.
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IKitchenNotifications notifications = scope.ServiceProvider
                .GetRequiredService<IKitchenNotifications>();

            IReadOnlyList<KitchenReminderIssued> issued = await notifications
                .IssueDueRemindersAsync(_options.KitchenSubmissionReminderSeconds, cancellationToken)
                .ConfigureAwait(false);

            foreach (KitchenReminderIssued reminder in issued)
            {
                // §12 first, then §9 — the same order OrderWorkflow uses after a commit, so a
                // subscriber that re-queries synchronously cannot observe an alert that has not been
                // counted.
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
            // Shutdown during a scan. Nothing was broadcast for a row that was written; the next start
            // will not re-announce it (the unique constraint saw to that), which is the correct trade:
            // a reminder that arrives while the kitchen board is not running has nobody to reach.
        }
        catch (Exception exception)
        {
            // Deliberately broad. The alternative — letting the tick throw — stops the loop for the
            // life of the process, and a transient database blip must not silence the kitchen forever.
            _logger.LogError(exception, "The kitchen reminder scan failed; it will run again in {ScanSeconds}s.", ScanInterval.TotalSeconds);
        }
    }
}
