using System.Globalization;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Time;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Time;

/// <summary>
/// The rendering half of F-36 (TECHNICAL_SPECIFICATION §8.1: instants are "stored <c>timestamptz</c>
/// UTC; rendered in <c>RESTAURANT_TIME_ZONE</c>"; §13, §11.7).
///
/// <para>Two properties matter more than any individual format string, and both are asserted directly
/// rather than left to inspection. First, the <em>reader's</em> zone never enters: the same instant
/// renders differently for a Tokyo restaurant than for a New York one, and identically for every
/// viewer of either. Second, the text is culture-independent — a container image that ships a
/// different default locale must not change what a guest sees, which is exactly the failure mode
/// <c>ToString("t")</c> had.</para>
///
/// <para>The literal expectations below double as the specification <c>js/clock.js</c> is written
/// against: the footer's ticking text must be byte-identical to what the server painted, or the
/// handover at page load is visible as a flicker.</para>
/// </summary>
public sealed class RestaurantTimeTests
{
    /// <summary>
    /// Sunday 26 July 2026, 19:04:05 UTC. Chosen so that every zone below lands somewhere interesting:
    /// New York is in daylight saving (UTC−4), Tokyo has already rolled into Monday, and Kathmandu sits
    /// on a 45-minute offset — the case a naive "offset is a whole number of hours" assumption breaks.
    /// </summary>
    private static readonly DateTimeOffset Instant = new(2026, 7, 26, 19, 4, 5, TimeSpan.Zero);

    // --- the reader's zone is irrelevant; the restaurant's is everything -------------------------

    [Fact]
    public void Time_RendersInTheRestaurantZone_NotUtc()
        => Assert.Equal("3:04 PM", NewYork().Time(Instant));

    [Fact]
    public void Time_ADifferentRestaurantZone_RendersADifferentReadingForTheSameInstant()
        => Assert.Equal("4:04 AM", Tokyo().Time(Instant));

    [Fact]
    public void Time_AZoneWithAFractionalOffset_IsNotRoundedToTheHour()
        => Assert.Equal("12:49 AM", Build("Asia/Kathmandu").Time(Instant));

    [Fact]
    public void Date_RollsForwardWhenTheRestaurantZoneHasAlreadyPassedMidnight()
    {
        // The instant is still the 26th in UTC and in New York, and already the 27th in Tokyo. A guest
        // reading their history from abroad must see the restaurant's date, not their own.
        Assert.Equal("26 Jul 2026", NewYork().Date(Instant));
        Assert.Equal("27 Jul 2026", Tokyo().Date(Instant));
    }

    // --- the individual formats -------------------------------------------------------------------

    [Fact]
    public void TimeWithSeconds_IsTheTimeToTheSecond()
        => Assert.Equal("3:04:05 PM", NewYork().TimeWithSeconds(Instant));

    [Fact]
    public void DateAndTime_IsTheDateThenTheTime()
        => Assert.Equal("26 Jul 2026, 3:04 PM", NewYork().DateAndTime(Instant));

    [Fact]
    public void DateAndTimeWithSeconds_CarriesTheWeekday_ForTheFooterClock()
        => Assert.Equal("Sun 26 Jul 2026, 3:04:05 PM", NewYork().DateAndTimeWithSeconds(Instant));

    [Fact]
    public void DateAndTimeWithSeconds_WeekdayFollowsTheRestaurantZone()
        => Assert.Equal("Mon 27 Jul 2026, 4:04:05 AM", Tokyo().DateAndTimeWithSeconds(Instant));

    [Fact]
    public void MachineReadable_IsAnIso8601StringCarryingTheRestaurantOffset()
        => Assert.Equal("2026-07-26T15:04:05-04:00", NewYork().MachineReadable(Instant));

    [Fact]
    public void MachineReadable_APositiveOffsetIsSigned()
        => Assert.Equal("2026-07-27T04:04:05+09:00", Tokyo().MachineReadable(Instant));

    // --- the 12-versus-24 decision (§13) ----------------------------------------------------------

    [Fact]
    public void Time_TwentyFourHourClock_PadsAndDropsTheMeridiem()
        => Assert.Equal("15:04", NewYork(usesTwelveHourClock: false).Time(Instant));

    [Fact]
    public void TimeWithSeconds_TwentyFourHourClock_PadsAndDropsTheMeridiem()
        => Assert.Equal("15:04:05", NewYork(usesTwelveHourClock: false).TimeWithSeconds(Instant));

    [Fact]
    public void DateAndTimeWithSeconds_TwentyFourHourClock_MatchesTheScriptsPattern()
        => Assert.Equal(
            "Sun 26 Jul 2026, 15:04:05",
            NewYork(usesTwelveHourClock: false).DateAndTimeWithSeconds(Instant));

    [Fact]
    public void Time_Midnight_RendersAsTwelveAmNotZeroAm()
    {
        // 04:00 UTC is 00:00 in New York on the same day — the hour a modulo-12 that forgets to remap
        // zero gets wrong, and the one js/clock.js has to remap the same way.
        DateTimeOffset midnightInNewYork = new(2026, 7, 26, 4, 0, 0, TimeSpan.Zero);

        Assert.Equal("12:00 AM", NewYork().Time(midnightInNewYork));
        Assert.Equal("00:00", NewYork(usesTwelveHourClock: false).Time(midnightInNewYork));
    }

    [Fact]
    public void Time_Noon_RendersAsTwelvePm()
    {
        DateTimeOffset noonInNewYork = new(2026, 7, 26, 16, 0, 0, TimeSpan.Zero);

        Assert.Equal("12:00 PM", NewYork().Time(noonInNewYork));
        Assert.Equal("12:00", NewYork(usesTwelveHourClock: false).Time(noonInNewYork));
    }

    // --- the culture independence that F-36 was really about --------------------------------------

    [Fact]
    public void EveryFormat_IsUnaffectedByTheAmbientCulture()
    {
        // The deployed container's locale is whatever its base image carries. Before this class, that
        // decided 12- versus 24-hour, the separator, and the month names. It must now decide nothing.
        RestaurantTime restaurantTime = NewYork();

        string time = restaurantTime.Time(Instant);
        string dateAndTime = restaurantTime.DateAndTime(Instant);
        string machineReadable = restaurantTime.MachineReadable(Instant);

        using (new CultureScope("de-DE"))
        {
            Assert.Equal(time, restaurantTime.Time(Instant));
            Assert.Equal(dateAndTime, restaurantTime.DateAndTime(Instant));
            Assert.Equal(machineReadable, restaurantTime.MachineReadable(Instant));
        }

        using (new CultureScope("ja-JP"))
        {
            Assert.Equal(time, restaurantTime.Time(Instant));
            Assert.Equal(dateAndTime, restaurantTime.DateAndTime(Instant));
            Assert.Equal(machineReadable, restaurantTime.MachineReadable(Instant));
        }
    }

    // --- the zone label the footer shows ----------------------------------------------------------

    [Theory]
    [InlineData("America/New_York", "New York")]
    [InlineData("Asia/Tokyo", "Tokyo")]
    [InlineData("America/Argentina/Buenos_Aires", "Buenos Aires")]
    [InlineData("UTC", "UTC")]
    public void ZoneLabel_IsTheHumanTailOfTheIdentifier(string zoneIdentifier, string expectedLabel)
        => Assert.Equal(expectedLabel, Build(zoneIdentifier).ZoneLabel);

    [Fact]
    public void ZoneIdentifier_IsTheConfiguredString_NotWhateverTheHostNormalizedItTo()
        => Assert.Equal("America/New_York", NewYork().ZoneIdentifier);

    // --- the snapshot js/clock.js anchors from ----------------------------------------------------

    [Fact]
    public void Snapshot_CarriesTheInstantAndTheOffsetThatAppliesToIt()
    {
        RestaurantClockSnapshot snapshot = NewYork().Snapshot(Instant);

        Assert.Equal(Instant.ToUnixTimeMilliseconds(), snapshot.EpochMilliseconds);
        Assert.Equal(-240, snapshot.UtcOffsetMinutes);   // EDT
        Assert.Equal("America/New_York", snapshot.ZoneIdentifier);
        Assert.Equal("New York", snapshot.ZoneLabel);
        Assert.True(snapshot.UsesTwelveHourClock);
    }

    [Fact]
    public void Snapshot_TwentyFourHourClock_SaysSo()
        => Assert.False(NewYork(usesTwelveHourClock: false).Snapshot(Instant).UsesTwelveHourClock);

    [Fact]
    public void Snapshot_FindsTheNextDaylightSavingTransition()
    {
        // A page left open across the first Sunday in November must not keep rendering EDT. The clocks
        // go back at 02:00 EDT = 06:00 UTC on 2026-11-01.
        RestaurantClockSnapshot snapshot = NewYork().Snapshot(Instant);

        Assert.NotNull(snapshot.NextTransitionEpochMilliseconds);
        Assert.Equal(-300, snapshot.NextUtcOffsetMinutes);   // EST

        DateTimeOffset transition =
            DateTimeOffset.FromUnixTimeMilliseconds(snapshot.NextTransitionEpochMilliseconds!.Value);

        // Bisection stops at one-second precision, so assert the second rather than the millisecond.
        Assert.True(
            (transition - new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero)).Duration()
                <= TimeSpan.FromSeconds(1),
            $"Expected the transition near 2026-11-01T06:00:00Z but found {transition:O}.");
    }

    [Fact]
    public void Snapshot_TheReportedTransitionIsWhereTheOffsetActuallyChanges()
    {
        // Guards the bisection against an off-by-one that would have the script switch offsets an hour
        // early or late — a bug nobody would see until the day it happened.
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        RestaurantClockSnapshot snapshot = NewYork().Snapshot(Instant);

        DateTimeOffset transition =
            DateTimeOffset.FromUnixTimeMilliseconds(snapshot.NextTransitionEpochMilliseconds!.Value);

        Assert.Equal(
            snapshot.NextUtcOffsetMinutes,
            (int)zone.GetUtcOffset(transition).TotalMinutes);
        Assert.Equal(
            snapshot.UtcOffsetMinutes,
            (int)zone.GetUtcOffset(transition.AddSeconds(-2)).TotalMinutes);
    }

    [Fact]
    public void Snapshot_AZoneThatNeverMovesItsClocks_ReportsNoTransition()
    {
        RestaurantClockSnapshot snapshot = Tokyo().Snapshot(Instant);

        Assert.Null(snapshot.NextTransitionEpochMilliseconds);
        Assert.Null(snapshot.NextUtcOffsetMinutes);
        Assert.Equal(540, snapshot.UtcOffsetMinutes);
    }

    [Fact]
    public void Snapshot_IsStableAcrossRepeatedCalls_TheTransitionScanIsMemoized()
    {
        RestaurantTime restaurantTime = NewYork();

        RestaurantClockSnapshot first = restaurantTime.Snapshot(Instant);
        RestaurantClockSnapshot second = restaurantTime.Snapshot(Instant.AddMinutes(1));

        Assert.Equal(first.NextTransitionEpochMilliseconds, second.NextTransitionEpochMilliseconds);
        Assert.Equal(first.NextUtcOffsetMinutes, second.NextUtcOffsetMinutes);
        Assert.Equal(first.EpochMilliseconds + 60000, second.EpochMilliseconds);
    }

    [Fact]
    public void Snapshot_AfterTheCachedTransition_ReportsTheFollowingOne()
    {
        // The memo must expire at the transition it named, or a display left running through November
        // would keep insisting the change is still ahead of it.
        RestaurantTime restaurantTime = NewYork();
        _ = restaurantTime.Snapshot(Instant);

        DateTimeOffset afterTheChange = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        RestaurantClockSnapshot snapshot = restaurantTime.Snapshot(afterTheChange);

        Assert.Equal(-300, snapshot.UtcOffsetMinutes);
        Assert.Equal(-240, snapshot.NextUtcOffsetMinutes);   // the following March
    }

    // --- construction from configuration ----------------------------------------------------------

    [Fact]
    public void ConstructedFromOptions_TakesTheZoneAndTheClockFormat()
    {
        RestaurantTime restaurantTime = new(Options("Asia/Tokyo", RestaurantOptions.TwentyFourHourClockFormat));

        Assert.Equal("Asia/Tokyo", restaurantTime.ZoneIdentifier);
        Assert.False(restaurantTime.UsesTwelveHourClock);
        Assert.Equal("04:04", restaurantTime.Time(Instant));
    }

    [Fact]
    public void ConstructedFromOptions_DefaultsToTheTwelveHourClock()
        => Assert.True(new RestaurantTime(Options("America/New_York", RestaurantOptions.DefaultClockFormat))
            .UsesTwelveHourClock);

    // --- helpers ------------------------------------------------------------------------------------

    private static RestaurantTime NewYork(bool usesTwelveHourClock = true)
        => Build("America/New_York", usesTwelveHourClock);

    private static RestaurantTime Tokyo(bool usesTwelveHourClock = true)
        => Build("Asia/Tokyo", usesTwelveHourClock);

    private static RestaurantTime Build(string zoneIdentifier, bool usesTwelveHourClock = true)
        => new(TimeZoneInfo.FindSystemTimeZoneById(zoneIdentifier), zoneIdentifier, usesTwelveHourClock);

    private static RestaurantOptions Options(string zoneIdentifier, string clockFormat) => new()
    {
        RestaurantName = "Test Bistro",
        PublicOrigin = "https://localhost:8443",
        TimeZoneId = zoneIdentifier,
        ClockFormat = clockFormat,
        CurrencyCode = "USD",
        DatabaseConnectionString = "Host=localhost;Database=x;Username=u;Password=p",
        DataProtectionKeysDirectory = "/tmp/myrestaurant-keys",
        KitchenSubmissionReminderSeconds = 60,
        TableJoinTokenRotationSeconds = 60,
        TableJoinGrantMinutes = 10,
        TableDisplayPairingCodeMinutes = 10,
        Argon2MemoryKibibytes = 65536,
        Argon2Iterations = 3,
        Argon2Parallelism = 1,
        Argon2MaxConcurrentHashes = 4,
        GuestRegistrationAttemptsPerWindow = 0,
        GuestRegistrationWindowMinutes = 0,
    };

    /// <summary>
    /// Swaps the ambient culture for the duration of a block and puts it back. Hand-written rather than
    /// reached for from a package, in the spirit of §16.1's preference for fakes over machinery.
    /// </summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previousCulture;
        private readonly CultureInfo _previousUiCulture;

        public CultureScope(string cultureName)
        {
            _previousCulture = CultureInfo.CurrentCulture;
            _previousUiCulture = CultureInfo.CurrentUICulture;

            CultureInfo culture = new(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
        }
    }
}
