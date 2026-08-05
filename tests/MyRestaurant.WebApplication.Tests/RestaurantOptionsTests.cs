using Microsoft.Extensions.Configuration;
using MyRestaurant.WebApplication.Configuration;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Verifies configuration binding, the documented defaults, and the fail-fast validation of
/// <see cref="RestaurantOptions"/> (TECHNICAL_SPECIFICATION §13, and the §3.2 Argon2 floor guard).
/// Validation runs before HTTP is bound, so every security-relevant lower bound is asserted here.
/// </summary>
public sealed class RestaurantOptionsTests
{
    [Fact]
    public void FromConfiguration_EmptyConfiguration_UsesDocumentedDefaults()
    {
        RestaurantOptions options = RestaurantOptions.FromConfiguration(EmptyConfiguration());

        Assert.Equal("My Restaurant", options.RestaurantName);
        Assert.Equal("https://localhost:8443", options.PublicOrigin);
        Assert.Equal("America/New_York", options.TimeZoneId);
        Assert.Equal(RestaurantOptions.TwelveHourClockFormat, options.ClockFormat);
        Assert.True(options.UsesTwelveHourClock);
        Assert.Equal("USD", options.CurrencyCode);
        Assert.Equal(RestaurantOptions.DefaultSourceUrl, options.SourceUrl);
        Assert.Equal(65536, options.Argon2MemoryKibibytes);
        Assert.Equal(3, options.Argon2Iterations);
        Assert.Equal(60, options.TableJoinTokenRotationSeconds);
        Assert.Equal(10, options.TableJoinGrantMinutes);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void FromConfiguration_ReadsProvidedValues()
    {
        RestaurantOptions options = RestaurantOptions.FromConfiguration(ConfigurationWith(new()
        {
            ["RESTAURANT_NAME"] = "Cafe Test",
            ["RESTAURANT_PUBLIC_ORIGIN"] = "https://order.example.com",
            ["RESTAURANT_CURRENCY_CODE"] = "EUR",
            ["ARGON2_ITERATIONS"] = "5",
            ["TABLE_JOIN_TOKEN_ROTATION_SECONDS"] = "45",
        }));

        Assert.Equal("Cafe Test", options.RestaurantName);
        Assert.Equal("https://order.example.com", options.PublicOrigin);
        Assert.Equal("EUR", options.CurrencyCode);
        Assert.Equal(5, options.Argon2Iterations);
        Assert.Equal(45, options.TableJoinTokenRotationSeconds);
    }

    [Fact]
    public void FromConfiguration_NonNumericInteger_FallsBackToDefault()
    {
        RestaurantOptions options = RestaurantOptions.FromConfiguration(ConfigurationWith(new()
        {
            ["ARGON2_ITERATIONS"] = "not-a-number",
        }));

        Assert.Equal(3, options.Argon2Iterations);
    }

    [Fact]
    public void Validate_ValidOptions_ReturnNoErrors()
        => Assert.Empty(Build().Validate());

    [Fact]
    public void Validate_NonHttpsOrigin_IsRejected()
        => Assert.NotEmpty(Build(publicOrigin: "http://insecure.example.com").Validate());

    [Fact]
    public void Validate_UnresolvableTimeZone_IsRejected()
        => Assert.NotEmpty(Build(timeZoneId: "Nowhere/Unreal").Validate());

    [Theory]
    [InlineData("US")]    // too short
    [InlineData("USDD")]  // too long
    [InlineData("US1")]   // non-letter
    public void Validate_BadCurrencyCode_IsRejected(string currencyCode)
        => Assert.NotEmpty(Build(currencyCode: currencyCode).Validate());

    [Fact]
    public void Validate_Argon2MemoryBelowFloor_IsRejected()
        => Assert.NotEmpty(Build(argon2Memory: 1024).Validate());

    [Fact]
    public void Validate_Argon2IterationsBelowFloor_IsRejected()
        => Assert.NotEmpty(Build(argon2Iterations: 1).Validate());

    [Fact]
    public void Validate_Argon2ParallelismBelowFloor_IsRejected()
        => Assert.NotEmpty(Build(argon2Parallelism: 0).Validate());

    [Fact]
    public void Validate_Argon2MaxConcurrentBelowOne_IsRejected()
        => Assert.NotEmpty(Build(argon2MaxConcurrent: 0).Validate());

    [Fact]
    public void Validate_TokenRotationBelowFloor_IsRejected()
        => Assert.NotEmpty(Build(rotationSeconds: 5).Validate());

    [Fact]
    public void Validate_GrantMinutesBelowFloor_IsRejected()
        => Assert.NotEmpty(Build(grantMinutes: 0).Validate());

    [Fact]
    public void Validate_PairingMinutesBelowFloor_IsRejected()
        => Assert.NotEmpty(Build(pairingMinutes: 0).Validate());

    [Fact]
    public void Validate_ReminderSecondsBelowOne_IsRejected()
        => Assert.NotEmpty(Build(kitchenReminderSeconds: 0).Validate());

    [Fact]
    public void Validate_BlankConnectionString_IsRejected()
        => Assert.NotEmpty(Build(databaseConnectionString: "").Validate());

    [Fact]
    public void ResolveWebAuthnRelyingPartyId_IsTheOriginHost()
        => Assert.Equal(
            "order.example.com",
            Build(publicOrigin: "https://order.example.com:8443").ResolveWebAuthnRelyingPartyId());

    [Fact]
    public void FromConfiguration_TrustedOriginPatterns_DefaultsToTheQuickTunnelWildcard()
    {
        RestaurantOptions options = RestaurantOptions.FromConfiguration(EmptyConfiguration());

        Assert.Equal(new[] { "https://*.trycloudflare.com" }, options.TrustedOriginPatterns);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void FromConfiguration_TrustedOriginPatterns_ReadsAndSplitsAList()
    {
        RestaurantOptions options = RestaurantOptions.FromConfiguration(ConfigurationWith(new()
        {
            ["RESTAURANT_TRUSTED_ORIGIN_PATTERNS"] = "https://*.trycloudflare.com, https://demo.example.com",
        }));

        Assert.Equal(
            new[] { "https://*.trycloudflare.com", "https://demo.example.com" },
            options.TrustedOriginPatterns);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("http://*.trycloudflare.com")]   // must be https
    [InlineData("https://")]                      // no host
    [InlineData("*.trycloudflare.com")]           // no scheme
    [InlineData("https://foo.*.com")]             // wildcard not the leading label
    [InlineData("https://foo.trycloudflare.com:8443")] // no port allowed in a pattern
    public void Validate_BadTrustedOriginPattern_IsRejected(string pattern)
        => Assert.NotEmpty(Build(trustedOriginPatterns: [pattern]).Validate());

    [Fact]
    public void ResolveTimeZone_ReturnsTheConfiguredZone()
    {
        TimeZoneInfo zone = Build(timeZoneId: "America/New_York").ResolveTimeZone();
        Assert.NotNull(zone);
    }

    // --- RESTAURANT_CLOCK_FORMAT (§13, F-36) -----------------------------------------------------
    //
    // The 12-versus-24 question the specification never answered. It is configuration rather than a
    // constant because the same code runs in restaurants on both conventions — and it is validated
    // rather than merely parsed because a typo that silently fell back to the default would show the
    // wrong clock on every screen in the building with nothing to say why.

    [Theory]
    [InlineData("12")]
    [InlineData("12h")]
    [InlineData("12-hour")]
    [InlineData("12 hour")]
    [InlineData("12hour")]
    [InlineData("  12-HOUR  ")]
    public void UsesTwelveHourClock_TwelveHourSpellings_AreAccepted(string clockFormat)
    {
        RestaurantOptions options = Build(clockFormat: clockFormat);

        Assert.True(options.UsesTwelveHourClock);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("24")]
    [InlineData("24h")]
    [InlineData("24-hour")]
    [InlineData("24 hour")]
    [InlineData("24hour")]
    [InlineData("  24-Hour  ")]
    public void UsesTwelveHourClock_TwentyFourHourSpellings_AreAccepted(string clockFormat)
    {
        RestaurantOptions options = Build(clockFormat: clockFormat);

        Assert.False(options.UsesTwelveHourClock);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("military")]
    [InlineData("HH:mm")]
    [InlineData("13")]
    [InlineData("")]
    public void Validate_UnknownClockFormat_IsRejected(string clockFormat)
        => Assert.NotEmpty(Build(clockFormat: clockFormat).Validate());

    // --- the source offer (§11.9, F-39) -----------------------------------------------------------

    [Fact]
    public void FromConfiguration_ReadsTheSourceUrl()
    {
        RestaurantOptions options = RestaurantOptions.FromConfiguration(ConfigurationWith(new()
        {
            ["RESTAURANT_SOURCE_URL"] = "https://git.example.com/someone/myrestaurant-fork",
        }));

        Assert.Equal("https://git.example.com/someone/myrestaurant-fork", options.SourceUrl);
        Assert.Empty(options.Validate());
    }

    /// <summary>
    /// http is accepted here and nowhere else. RESTAURANT_PUBLIC_ORIGIN is https-only because
    /// WebAuthn needs a secure context and the authentication cookie is Secure; an outbound link to
    /// somebody else's repository has neither property. A fork operator running a forge on a LAN over
    /// plain http is discharging AGPL §13 perfectly well, and refusing to boot over it would be this
    /// application enforcing a taste as though it were a security control.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/kusl/myrestaurant")]
    [InlineData("http://gitea.lan:3000/restaurant/source")]
    public void Validate_AbsoluteHttpOrHttpsSourceUrl_IsAccepted(string sourceUrl)
        => Assert.Empty(Build(sourceUrl: sourceUrl).Validate());

    [Theory]
    [InlineData("")]                              // cleared rather than set
    [InlineData("github.com/kusl/myrestaurant")]  // no scheme: a browser would resolve it relatively
    [InlineData("/source")]                       // relative: points back at this instance, offering nothing
    [InlineData("ftp://example.com/source.tar")]  // not something a browser will open
    [InlineData("javascript:alert(1)")]           // absolute, and a link the footer would render
    public void Validate_SourceUrlThatIsNotAnAbsoluteHttpUrl_IsRejected(string sourceUrl)
        => Assert.NotEmpty(Build(sourceUrl: sourceUrl).Validate());

    [Fact]
    public void FromConfiguration_ReadsTheClockFormat()
    {
        RestaurantOptions options = RestaurantOptions.FromConfiguration(ConfigurationWith(new()
        {
            ["RESTAURANT_CLOCK_FORMAT"] = "24-hour",
        }));

        Assert.Equal("24-hour", options.ClockFormat);
        Assert.False(options.UsesTwelveHourClock);
        Assert.Empty(options.Validate());
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static RestaurantOptions Build(
        string publicOrigin = "https://localhost:8443",
        string timeZoneId = "America/New_York",
        string clockFormat = RestaurantOptions.DefaultClockFormat,
        string currencyCode = "USD",
        string sourceUrl = RestaurantOptions.DefaultSourceUrl,
        string databaseConnectionString = "Host=localhost;Database=x;Username=u;Password=p",
        int kitchenReminderSeconds = 60,
        int rotationSeconds = 60,
        int grantMinutes = 10,
        int pairingMinutes = 10,
        int argon2Memory = 65536,
        int argon2Iterations = 3,
        int argon2Parallelism = 1,
        int argon2MaxConcurrent = 4,
        IReadOnlyList<string>? trustedOriginPatterns = null)
        => new()
        {
            RestaurantName = "Test Bistro",
            PublicOrigin = publicOrigin,
            TrustedOriginPatterns = trustedOriginPatterns ?? RestaurantOptions.DefaultTrustedOriginPatterns,
            TimeZoneId = timeZoneId,
            ClockFormat = clockFormat,
            CurrencyCode = currencyCode,
            SourceUrl = sourceUrl,
            DatabaseConnectionString = databaseConnectionString,
            DataProtectionKeysDirectory = "/tmp/myrestaurant-keys",
            KitchenSubmissionReminderSeconds = kitchenReminderSeconds,
            TableJoinTokenRotationSeconds = rotationSeconds,
            TableJoinGrantMinutes = grantMinutes,
            TableDisplayPairingCodeMinutes = pairingMinutes,
            Argon2MemoryKibibytes = argon2Memory,
            Argon2Iterations = argon2Iterations,
            Argon2Parallelism = argon2Parallelism,
            Argon2MaxConcurrentHashes = argon2MaxConcurrent,
        };
}

################################################################################
