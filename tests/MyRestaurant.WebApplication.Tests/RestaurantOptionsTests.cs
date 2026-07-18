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
        Assert.Equal("USD", options.CurrencyCode);
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
    public void ResolveTimeZone_ReturnsTheConfiguredZone()
    {
        TimeZoneInfo zone = Build(timeZoneId: "America/New_York").ResolveTimeZone();
        Assert.NotNull(zone);
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static RestaurantOptions Build(
        string publicOrigin = "https://localhost:8443",
        string timeZoneId = "America/New_York",
        string currencyCode = "USD",
        string databaseConnectionString = "Host=localhost;Database=x;Username=u;Password=p",
        int kitchenReminderSeconds = 60,
        int rotationSeconds = 60,
        int grantMinutes = 10,
        int pairingMinutes = 10,
        int argon2Memory = 65536,
        int argon2Iterations = 3,
        int argon2Parallelism = 1,
        int argon2MaxConcurrent = 4)
        => new()
        {
            RestaurantName = "Test Bistro",
            PublicOrigin = publicOrigin,
            TimeZoneId = timeZoneId,
            CurrencyCode = currencyCode,
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
