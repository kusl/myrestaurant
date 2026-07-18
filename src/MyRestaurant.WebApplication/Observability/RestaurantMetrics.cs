using System.Diagnostics.Metrics;

namespace MyRestaurant.WebApplication.Observability;

/// <summary>
/// The application's custom metrics (TECHNICAL_SPECIFICATION §12). One <see cref="Meter"/> named
/// <see cref="MeterName"/> is created through the injected <see cref="IMeterFactory"/> so the meter's
/// lifetime is owned by DI and the OpenTelemetry pipeline can subscribe to it by name
/// (<c>AddMeter(RestaurantMetrics.MeterName)</c> in <c>Program.cs</c>).
///
/// Instrument names are the full, unabbreviated snake_case names from the specification; call sites
/// use the strongly-typed helper methods below rather than touching the instruments directly, which
/// keeps tag names (<c>result</c>, <c>method</c>) spelled consistently in exactly one place.
/// </summary>
public sealed class RestaurantMetrics : IDisposable
{
    /// <summary>The meter name the OpenTelemetry metrics pipeline subscribes to.</summary>
    public const string MeterName = "MyRestaurant";

    // Tag keys — declared once so every emission agrees on spelling (§12).
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

    /// <summary>Records a join-token validation outcome (<c>result</c> = valid | expired | invalid).</summary>
    public void RecordTableJoinTokenValidated(string result)
        => _tableJoinTokensValidated.Add(1, new KeyValuePair<string, object?>(ResultTag, result));

    /// <summary>Records a sign-in attempt (<c>method</c> = password | passkey, <c>result</c> = succeeded | failed).</summary>
    public void RecordSignIn(string method, string result)
        => _signIns.Add(
            1,
            new KeyValuePair<string, object?>(MethodTag, method),
            new KeyValuePair<string, object?>(ResultTag, result));

    /// <summary>Records how long a single Argon2id hash took, in milliseconds.</summary>
    public void RecordPasswordHashDuration(double milliseconds)
        => _passwordHashDurationMilliseconds.Record(milliseconds);

    public void Dispose() => _meter.Dispose();
}
