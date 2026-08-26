using System.Globalization;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Time;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Time;

public sealed class RestaurantTimeTests
{
    private static readonly DateTimeOffset Instant = new(2026, 7, 26, 19, 4, 5, TimeSpan.Zero);

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
        Assert.Equal("26 Jul 2026", NewYork().Date(Instant));
        Assert.Equal("27 Jul 2026", Tokyo().Date(Instant));
    }

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

    [Fact]
    public void EveryFormat_IsUnaffectedByTheAmbientCulture()
    {
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

    [Fact]
    public void Snapshot_CarriesTheInstantAndTheOffsetThatAppliesToIt()
    {
        RestaurantClockSnapshot snapshot = NewYork().Snapshot(Instant);

        Assert.Equal(Instant.ToUnixTimeMilliseconds(), snapshot.EpochMilliseconds);
        Assert.Equal(-240, snapshot.UtcOffsetMinutes);
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
        RestaurantClockSnapshot snapshot = NewYork().Snapshot(Instant);

        Assert.NotNull(snapshot.NextTransitionEpochMilliseconds);
        Assert.Equal(-300, snapshot.NextUtcOffsetMinutes);

        DateTimeOffset transition =
            DateTimeOffset.FromUnixTimeMilliseconds(snapshot.NextTransitionEpochMilliseconds!.Value);

        Assert.True(
            (transition - new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero)).Duration()
                <= TimeSpan.FromSeconds(1),
            $"Expected the transition near 2026-11-01T06:00:00Z but found {transition:O}.");
    }

    [Fact]
    public void Snapshot_TheReportedTransitionIsWhereTheOffsetActuallyChanges()
    {
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
        RestaurantTime restaurantTime = NewYork();
        _ = restaurantTime.Snapshot(Instant);

        DateTimeOffset afterTheChange = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        RestaurantClockSnapshot snapshot = restaurantTime.Snapshot(afterTheChange);

        Assert.Equal(-300, snapshot.UtcOffsetMinutes);
        Assert.Equal(-240, snapshot.NextUtcOffsetMinutes);
    }

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
