using System.Globalization;
using MyRestaurant.WebApplication.Configuration;

namespace MyRestaurant.WebApplication.Time;

public sealed class RestaurantTime
{
    private const int TransitionSearchDays = 800;

    private const string DatePattern = "d MMM yyyy";
    private const string WeekdayDatePattern = "ddd d MMM yyyy";
    private const string TwelveHourTimePattern = "h:mm tt";
    private const string TwentyFourHourTimePattern = "HH:mm";
    private const string TwelveHourTimeWithSecondsPattern = "h:mm:ss tt";
    private const string TwentyFourHourTimeWithSecondsPattern = "HH:mm:ss";

    private const string MachineReadablePattern = "yyyy'-'MM'-'dd'T'HH':'mm':'sszzz";

    private readonly object _transitionGate = new();
    private readonly TimeZoneInfo _zone;

    private CachedTransition? _cachedTransition;

    public RestaurantTime(RestaurantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _zone = options.ResolveTimeZone();
        ZoneIdentifier = options.TimeZoneId;
        UsesTwelveHourClock = options.UsesTwelveHourClock;
        ZoneLabel = LabelFor(options.TimeZoneId);
    }

    public RestaurantTime(TimeZoneInfo zone, string zoneIdentifier, bool usesTwelveHourClock)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneIdentifier);

        _zone = zone;
        ZoneIdentifier = zoneIdentifier;
        UsesTwelveHourClock = usesTwelveHourClock;
        ZoneLabel = LabelFor(zoneIdentifier);
    }

    public string ZoneIdentifier { get; }

    public string ZoneLabel { get; }

    public bool UsesTwelveHourClock { get; }

    public DateTimeOffset ToRestaurantTime(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, _zone);

    public string Time(DateTimeOffset instant)
        => Render(instant, UsesTwelveHourClock ? TwelveHourTimePattern : TwentyFourHourTimePattern);

    public string TimeWithSeconds(DateTimeOffset instant)
        => Render(instant, UsesTwelveHourClock ? TwelveHourTimeWithSecondsPattern : TwentyFourHourTimeWithSecondsPattern);

    public string Date(DateTimeOffset instant) => Render(instant, DatePattern);

    public string DateAndTime(DateTimeOffset instant)
        => $"{Date(instant)}, {Time(instant)}";

    public string DateAndTimeWithSeconds(DateTimeOffset instant)
        => $"{Render(instant, WeekdayDatePattern)}, {TimeWithSeconds(instant)}";

    public string MachineReadable(DateTimeOffset instant) => Render(instant, MachineReadablePattern);

    public DateTimeOffset StartOfDay(DateOnly day)
    {
        DateTime localMidnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        return new DateTimeOffset(localMidnight, _zone.GetUtcOffset(localMidnight)).ToUniversalTime();
    }

    public DateTimeOffset StartOfNextDay(DateOnly day) => StartOfDay(day.AddDays(1));

    public RestaurantClockSnapshot Snapshot(DateTimeOffset utcNow)
    {
        CachedTransition transition = TransitionAfter(utcNow);

        return new RestaurantClockSnapshot(
            EpochMilliseconds: utcNow.ToUnixTimeMilliseconds(),
            UtcOffsetMinutes: (int)_zone.GetUtcOffset(utcNow).TotalMinutes,
            NextTransitionEpochMilliseconds: transition.Instant?.ToUnixTimeMilliseconds(),
            NextUtcOffsetMinutes: transition.OffsetMinutes,
            ZoneIdentifier: ZoneIdentifier,
            ZoneLabel: ZoneLabel,
            UsesTwelveHourClock: UsesTwelveHourClock);
    }

    private string Render(DateTimeOffset instant, string pattern)
        => ToRestaurantTime(instant).ToString(pattern, CultureInfo.InvariantCulture);

    private static string LabelFor(string zoneIdentifier)
    {
        int lastSeparator = zoneIdentifier.LastIndexOf('/');
        string tail = lastSeparator >= 0 && lastSeparator < zoneIdentifier.Length - 1
            ? zoneIdentifier[(lastSeparator + 1)..]
            : zoneIdentifier;

        return tail.Replace('_', ' ');
    }

    private CachedTransition TransitionAfter(DateTimeOffset utcNow)
    {
        lock (_transitionGate)
        {
            if (_cachedTransition is { } cached && utcNow < cached.RecomputeAfter)
            {
                return cached;
            }

            CachedTransition computed = ComputeTransitionAfter(utcNow);
            _cachedTransition = computed;
            return computed;
        }
    }

    private CachedTransition ComputeTransitionAfter(DateTimeOffset utcNow)
    {
        TimeSpan currentOffset = _zone.GetUtcOffset(utcNow);
        DateTimeOffset horizon = utcNow.AddDays(TransitionSearchDays);

        DateTimeOffset low = utcNow;
        DateTimeOffset? bracket = null;

        for (DateTimeOffset probe = utcNow.AddDays(1); probe <= horizon; probe = probe.AddDays(1))
        {
            if (_zone.GetUtcOffset(probe) != currentOffset)
            {
                bracket = probe;
                break;
            }

            low = probe;
        }

        if (bracket is not { } high)
        {
            return new CachedTransition(Instant: null, OffsetMinutes: null, RecomputeAfter: horizon);
        }

        while (high - low > TimeSpan.FromSeconds(1))
        {
            DateTimeOffset middle = low + TimeSpan.FromTicks((high - low).Ticks / 2);
            if (_zone.GetUtcOffset(middle) == currentOffset)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return new CachedTransition(
            Instant: high,
            OffsetMinutes: (int)_zone.GetUtcOffset(high).TotalMinutes,
            RecomputeAfter: high);
    }

    private sealed record CachedTransition(DateTimeOffset? Instant, int? OffsetMinutes, DateTimeOffset RecomputeAfter);
}

public sealed record RestaurantClockSnapshot(
    long EpochMilliseconds,
    int UtcOffsetMinutes,
    long? NextTransitionEpochMilliseconds,
    int? NextUtcOffsetMinutes,
    string ZoneIdentifier,
    string ZoneLabel,
    bool UsesTwelveHourClock);
