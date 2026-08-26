using System.Diagnostics.Metrics;

namespace MyRestaurant.WebApplication.Observability;

public sealed class RestaurantMetrics : IDisposable
{
    public const string MeterName = "MyRestaurant";

    private const string ResultTag = "result";
    private const string MethodTag = "method";

    private readonly Meter _meter;

    private readonly Counter<long> _guestSubmissionBatches;
    private readonly Counter<long> _orderLinesAdded;
    private readonly Counter<long> _orderLinesRemoved;
    private readonly Counter<long> _orderLinesFulfilled;
    private readonly Counter<long> _kitchenRemindersSent;
    private readonly Counter<long> _sittingsClosed;
    private readonly Counter<long> _tableJoinTokensValidated;
    private readonly Counter<long> _signIns;
    private readonly Histogram<double> _passwordHashDurationMilliseconds;

    public RestaurantMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);

        _guestSubmissionBatches = _meter.CreateCounter<long>(
            "guest_submission_batches_total",
            unit: "{batch}",
            description: "Guest submission batches accepted (one per guest 'send').");
        _orderLinesAdded = _meter.CreateCounter<long>(
            "order_lines_added_total",
            unit: "{line}",
            description: "Order lines added across all sittings.");
        _orderLinesRemoved = _meter.CreateCounter<long>(
            "order_lines_removed_total",
            unit: "{line}",
            description: "Order lines removed across all sittings.");
        _orderLinesFulfilled = _meter.CreateCounter<long>(
            "order_lines_fulfilled_total",
            unit: "{line}",
            description: "Order lines marked fulfilled by the kitchen.");
        _kitchenRemindersSent = _meter.CreateCounter<long>(
            "kitchen_reminders_sent_total",
            unit: "{reminder}",
            description: "Kitchen submission reminders emitted (§10.2).");
        _sittingsClosed = _meter.CreateCounter<long>(
            "sittings_closed_total",
            unit: "{sitting}",
            description: "Sittings closed / bills settled.");
        _tableJoinTokensValidated = _meter.CreateCounter<long>(
            "table_join_tokens_validated_total",
            unit: "{validation}",
            description: "Table join-token validations, tagged by result.");
        _signIns = _meter.CreateCounter<long>(
            "sign_ins_total",
            unit: "{attempt}",
            description: "Staff sign-in attempts, tagged by method and result.");
        _passwordHashDurationMilliseconds = _meter.CreateHistogram<double>(
            "password_hash_duration_milliseconds",
            unit: "ms",
            description: "Wall-clock duration of an Argon2id password hash.");
    }

    public void RecordGuestSubmissionBatch() => _guestSubmissionBatches.Add(1);

    public void RecordOrderLinesAdded(long count) => _orderLinesAdded.Add(count);

    public void RecordOrderLinesRemoved(long count) => _orderLinesRemoved.Add(count);

    public void RecordOrderLinesFulfilled(long count) => _orderLinesFulfilled.Add(count);

    public void RecordKitchenReminderSent() => _kitchenRemindersSent.Add(1);

    public void RecordSittingClosed() => _sittingsClosed.Add(1);

    public void RecordTableJoinTokenValidated(string result)
        => _tableJoinTokensValidated.Add(1, new KeyValuePair<string, object?>(ResultTag, result));

    public void RecordSignIn(string method, string result)
        => _signIns.Add(
            1,
            new KeyValuePair<string, object?>(MethodTag, method),
            new KeyValuePair<string, object?>(ResultTag, result));

    public void RecordPasswordHashDuration(double milliseconds)
        => _passwordHashDurationMilliseconds.Record(milliseconds);

    public void Dispose() => _meter.Dispose();
}
