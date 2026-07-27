using System.Globalization;
using MyRestaurant.WebApplication.Configuration;

namespace MyRestaurant.WebApplication.Time;

/// <summary>
/// Renders a stored instant for a screen (TECHNICAL_SPECIFICATION §8.1: instants are "stored
/// <c>timestamptz</c> UTC; rendered in <c>RESTAURANT_TIME_ZONE</c>"; §13, F-36). This is the single
/// place the configured zone is honoured — nothing outside this class may call
/// <see cref="DateTimeOffset.ToLocalTime"/>, which reads the <em>server process's</em> zone and
/// therefore renders UTC in a container that sets no <c>TZ</c>.
///
/// <para><b>Restaurant time, always — never the reader's.</b> A restaurant is a physical place in one
/// IANA zone. A guest in New York reading the history of a meal they ate in Tokyo wants the times the
/// meal actually happened at, not the times it would have been on their own wristwatch; a kitchen
/// ticket and the bill it becomes must agree to the minute across every screen in the building. So the
/// reader's zone is deliberately irrelevant: every instant on every surface, for every viewer, is
/// rendered in <c>RESTAURANT_TIME_ZONE</c>, and the footer clock (§11.7) says so out loud.</para>
///
/// <para><b>Why not <see cref="CultureInfo"/>'s <c>"t"</c>/<c>"g"</c> patterns.</b> They take the
/// 12- versus 24-hour choice, the separator, and the month names from the <em>server's</em> culture,
/// which in this deployment is whatever locale the container image happens to carry — the same trap
/// <see cref="Orders.MoneyText"/> documents for <c>"C"</c>. Every pattern below is explicit and
/// formatted with <see cref="CultureInfo.InvariantCulture"/>; the one genuine choice, 12- versus
/// 24-hour, is configuration (<c>RESTAURANT_CLOCK_FORMAT</c>, §13) rather than an accident of the
/// image. <c>js/clock.js</c> reproduces these patterns character for character so the ticking footer
/// never disagrees with the server-rendered text beside it.</para>
/// </summary>
public sealed class RestaurantTime
{
    /// <summary>
    /// How far ahead <see cref="Snapshot"/> looks for the next UTC-offset change. Long enough to cover
    /// both edges of an annual daylight-saving cycle from any starting point, so a page left open for a
    /// week still knows when the clocks move; short enough that the scan below stays trivial.
    /// </summary>
    private const int TransitionSearchDays = 800;

    private const string DatePattern = "d MMM yyyy";
    private const string WeekdayDatePattern = "ddd d MMM yyyy";
    private const string TwelveHourTimePattern = "h:mm tt";
    private const string TwentyFourHourTimePattern = "HH:mm";
    private const string TwelveHourTimeWithSecondsPattern = "h:mm:ss tt";
    private const string TwentyFourHourTimeWithSecondsPattern = "HH:mm:ss";
    // Separators and the literal T are quoted: in a custom format string ":" means "the culture's
    // time separator" and an unquoted letter is asking to be reinterpreted. Invariant culture makes
    // both harmless today, but this one string has to be ISO 8601 for a machine, not for a reader.
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

    /// <summary>
    /// Direct construction, for tests and for any caller that already holds a resolved zone. The
    /// identifier is carried separately rather than read from <see cref="TimeZoneInfo.Id"/> so the
    /// label shown to a guest is the string an operator actually configured.
    /// </summary>
    public RestaurantTime(TimeZoneInfo zone, string zoneIdentifier, bool usesTwelveHourClock)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneIdentifier);

        _zone = zone;
        ZoneIdentifier = zoneIdentifier;
        UsesTwelveHourClock = usesTwelveHourClock;
        ZoneLabel = LabelFor(zoneIdentifier);
    }

    /// <summary>The configured zone identifier, e.g. <c>America/New_York</c>.</summary>
    public string ZoneIdentifier { get; }

    /// <summary>
    /// The short, human half of <see cref="ZoneIdentifier"/> — <c>New York</c>, <c>Tokyo</c>,
    /// <c>Buenos Aires</c>. Shown in the footer, where the full identifier would swamp a phone; the
    /// full identifier stays available as the element's title.
    /// </summary>
    public string ZoneLabel { get; }

    /// <summary>Whether times render as <c>3:04 PM</c> (true) or <c>15:04</c> (false); §13.</summary>
    public bool UsesTwelveHourClock { get; }

    /// <summary>The instant, shifted into the restaurant's zone. The absolute moment is unchanged.</summary>
    public DateTimeOffset ToRestaurantTime(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, _zone);

    /// <summary>Time of day only — <c>3:04 PM</c> or <c>15:04</c>.</summary>
    public string Time(DateTimeOffset instant)
        => Render(instant, UsesTwelveHourClock ? TwelveHourTimePattern : TwentyFourHourTimePattern);

    /// <summary>Time of day to the second — <c>3:04:05 PM</c> or <c>15:04:05</c>.</summary>
    public string TimeWithSeconds(DateTimeOffset instant)
        => Render(instant, UsesTwelveHourClock ? TwelveHourTimeWithSecondsPattern : TwentyFourHourTimeWithSecondsPattern);

    /// <summary>Date only — <c>26 Jul 2026</c>.</summary>
    public string Date(DateTimeOffset instant) => Render(instant, DatePattern);

    /// <summary>Date and time of day — <c>26 Jul 2026, 3:04 PM</c>.</summary>
    public string DateAndTime(DateTimeOffset instant)
        => $"{Date(instant)}, {Time(instant)}";

    /// <summary>
    /// The footer clock's reading — <c>Sun 26 Jul 2026, 3:04:05 PM</c>. The weekday earns its place
    /// here and nowhere else: this is the one line on the page whose job is to tell a reader what
    /// "now" is at the restaurant, and a bare date cannot do that across a time-zone boundary.
    /// <c>js/clock.js</c> formats identically.
    /// </summary>
    public string DateAndTimeWithSeconds(DateTimeOffset instant)
        => $"{Render(instant, WeekdayDatePattern)}, {TimeWithSeconds(instant)}";

    /// <summary>
    /// The <c>datetime</c> attribute value for a <c>&lt;time&gt;</c> element —
    /// <c>2026-07-26T15:04:05-04:00</c>. Machine-readable, and carries the offset so the markup is
    /// unambiguous even though the text beside it is not annotated.
    /// </summary>
    public string MachineReadable(DateTimeOffset instant) => Render(instant, MachineReadablePattern);

    /// <summary>
    /// Everything <c>js/clock.js</c> needs to keep ticking without the server: the anchoring instant,
    /// the offset that applies to it, and — because a page can outlive a daylight-saving boundary — the
    /// next instant at which that offset changes, with the offset that takes over. Also served as JSON
    /// by <see cref="RestaurantClockEndpoints"/> so a long-lived surface can re-anchor without a reload.
    /// </summary>
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

    /// <summary>
    /// <c>America/Argentina/Buenos_Aires</c> → <c>Buenos Aires</c>; <c>UTC</c> → <c>UTC</c>. A Windows
    /// identifier ("Eastern Standard Time") has no separator and is returned unchanged.
    /// </summary>
    private static string LabelFor(string zoneIdentifier)
    {
        int lastSeparator = zoneIdentifier.LastIndexOf('/');
        string tail = lastSeparator >= 0 && lastSeparator < zoneIdentifier.Length - 1
            ? zoneIdentifier[(lastSeparator + 1)..]
            : zoneIdentifier;

        return tail.Replace('_', ' ');
    }

    /// <summary>
    /// The next offset change at or after <paramref name="utcNow"/>, memoized. The scan is a day-by-day
    /// walk to find the bracketing day, then a bisection to the second — a few hundred
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTimeOffset)"/> calls, run at most once per transition
    /// (or once per <see cref="TransitionSearchDays"/> in a zone that never moves) rather than once per
    /// page render.
    /// </summary>
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
            // No change within the horizon. Re-ask then rather than never: the tz database is updated
            // in place under a running container, and a zone can acquire a rule it did not have.
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

/// <summary>
/// The wire shape of the footer clock's anchor (§11.7). Serialized camelCase by the minimal-API JSON
/// defaults, which is what <c>js/clock.js</c> reads.
/// </summary>
/// <param name="EpochMilliseconds">The server's instant, milliseconds since the Unix epoch, UTC.</param>
/// <param name="UtcOffsetMinutes">The restaurant zone's offset from UTC at that instant, in minutes.</param>
/// <param name="NextTransitionEpochMilliseconds">
/// When the offset next changes, or <c>null</c> if it does not within the search horizon.
/// </param>
/// <param name="NextUtcOffsetMinutes">The offset that takes over at that transition, or <c>null</c>.</param>
/// <param name="ZoneIdentifier">The configured IANA identifier, e.g. <c>America/New_York</c>.</param>
/// <param name="ZoneLabel">Its short human form, e.g. <c>New York</c>.</param>
/// <param name="UsesTwelveHourClock">Whether to render <c>3:04:05 PM</c> rather than <c>15:04:05</c>.</param>
public sealed record RestaurantClockSnapshot(
    long EpochMilliseconds,
    int UtcOffsetMinutes,
    long? NextTransitionEpochMilliseconds,
    int? NextUtcOffsetMinutes,
    string ZoneIdentifier,
    string ZoneLabel,
    bool UsesTwelveHourClock);
