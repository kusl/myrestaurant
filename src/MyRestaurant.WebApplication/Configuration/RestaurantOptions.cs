using System.Globalization;

namespace MyRestaurant.WebApplication.Configuration;

public sealed class RestaurantOptions
{
    public const int MinimumArgon2MemoryKibibytes = 19456;
    public const int MinimumArgon2Iterations = 2;
    public const int MinimumArgon2Parallelism = 1;

    public const int MinimumTableJoinTokenRotationSeconds = 10;
    public const int MinimumTableJoinGrantMinutes = 1;
    public const int MinimumTableDisplayPairingCodeMinutes = 1;

    public const int MinimumGuestRegistrationAttemptsPerWindow = 10;

    public const int MinimumGuestRegistrationWindowMinutes = 1;

    public const int DefaultGuestRegistrationAttemptsPerWindow = 60;

    public const int DefaultGuestRegistrationWindowMinutes = 10;

    public static readonly IReadOnlyList<string> DefaultTrustedOriginPatterns = ["https://*.trycloudflare.com"];

    public const string DefaultSourceUrl = "https://github.com/kusl/myrestaurant";

    public const string TwelveHourClockFormat = "12-hour";

    public const string TwentyFourHourClockFormat = "24-hour";

    public const string DefaultClockFormat = TwelveHourClockFormat;

    private static readonly HashSet<string> TwelveHourSpellings =
        new(StringComparer.OrdinalIgnoreCase) { "12", "12h", "12-hour", "12 hour", "12hour" };

    private static readonly HashSet<string> TwentyFourHourSpellings =
        new(StringComparer.OrdinalIgnoreCase) { "24", "24h", "24-hour", "24 hour", "24hour" };

    public required string RestaurantName { get; init; }
    public required string PublicOrigin { get; init; }

    public string SourceUrl { get; init; } = DefaultSourceUrl;

    public IReadOnlyList<string> TrustedOriginPatterns { get; init; } = DefaultTrustedOriginPatterns;

    public required string TimeZoneId { get; init; }

    public string ClockFormat { get; init; } = DefaultClockFormat;

    public required string CurrencyCode { get; init; }
    public required string DatabaseConnectionString { get; init; }
    public required string DataProtectionKeysDirectory { get; init; }
    public required int KitchenSubmissionReminderSeconds { get; init; }
    public required int TableJoinTokenRotationSeconds { get; init; }
    public required int TableJoinGrantMinutes { get; init; }
    public required int TableDisplayPairingCodeMinutes { get; init; }

    public required int GuestRegistrationAttemptsPerWindow { get; init; }

    public required int GuestRegistrationWindowMinutes { get; init; }

    public required int Argon2MemoryKibibytes { get; init; }
    public required int Argon2Iterations { get; init; }
    public required int Argon2Parallelism { get; init; }
    public required int Argon2MaxConcurrentHashes { get; init; }

    public static RestaurantOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new RestaurantOptions
        {
            RestaurantName = ReadString(configuration, "RESTAURANT_NAME", "My Restaurant"),
            PublicOrigin = ReadString(configuration, "RESTAURANT_PUBLIC_ORIGIN", "https://localhost:8443"),
            TrustedOriginPatterns = ReadOriginPatterns(configuration, "RESTAURANT_TRUSTED_ORIGIN_PATTERNS", DefaultTrustedOriginPatterns),
            TimeZoneId = ReadString(configuration, "RESTAURANT_TIME_ZONE", "America/New_York"),
            ClockFormat = ReadString(configuration, "RESTAURANT_CLOCK_FORMAT", DefaultClockFormat),
            CurrencyCode = ReadString(configuration, "RESTAURANT_CURRENCY_CODE", "USD"),
            SourceUrl = ReadString(configuration, "RESTAURANT_SOURCE_URL", DefaultSourceUrl),
            DatabaseConnectionString = ReadString(
                configuration,
                "RESTAURANT_DATABASE_CONNECTION_STRING",
                "Host=localhost;Port=5432;Database=myrestaurant;Username=myrestaurant;Password=myrestaurant"),
            DataProtectionKeysDirectory = ReadString(configuration, "DATA_PROTECTION_KEYS_DIRECTORY", "/var/lib/myrestaurant/dataprotection"),
            KitchenSubmissionReminderSeconds = ReadInt(configuration, "KITCHEN_SUBMISSION_REMINDER_SECONDS", 60),
            TableJoinTokenRotationSeconds = ReadInt(configuration, "TABLE_JOIN_TOKEN_ROTATION_SECONDS", 60),
            TableJoinGrantMinutes = ReadInt(configuration, "TABLE_JOIN_GRANT_MINUTES", 10),
            TableDisplayPairingCodeMinutes = ReadInt(configuration, "TABLE_DISPLAY_PAIRING_CODE_MINUTES", 10),
            GuestRegistrationAttemptsPerWindow = ReadInt(
                configuration,
                "GUEST_REGISTRATION_ATTEMPTS_PER_WINDOW",
                DefaultGuestRegistrationAttemptsPerWindow),
            GuestRegistrationWindowMinutes = ReadInt(
                configuration,
                "GUEST_REGISTRATION_WINDOW_MINUTES",
                DefaultGuestRegistrationWindowMinutes),
            Argon2MemoryKibibytes = ReadInt(configuration, "ARGON2_MEMORY_KIBIBYTES", 65536),
            Argon2Iterations = ReadInt(configuration, "ARGON2_ITERATIONS", 3),
            Argon2Parallelism = ReadInt(configuration, "ARGON2_PARALLELISM", 1),
            Argon2MaxConcurrentHashes = ReadInt(configuration, "ARGON2_MAX_CONCURRENT_HASHES", 4),
        };
    }

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(DatabaseConnectionString))
        {
            errors.Add("RESTAURANT_DATABASE_CONNECTION_STRING must be set.");
        }

        if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out Uri? origin)
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"RESTAURANT_PUBLIC_ORIGIN must be an absolute https URL (was '{PublicOrigin}').");
        }

        if (!TryResolveTimeZone(TimeZoneId))
        {
            errors.Add($"RESTAURANT_TIME_ZONE '{TimeZoneId}' is not a resolvable time zone on this host.");
        }

        if (!IsKnownClockFormat(ClockFormat))
        {
            errors.Add(
                $"RESTAURANT_CLOCK_FORMAT must be '{TwelveHourClockFormat}' or '{TwentyFourHourClockFormat}' (was '{ClockFormat}').");
        }

        if (CurrencyCode.Length != 3 || !CurrencyCode.All(char.IsAsciiLetter))
        {
            errors.Add($"RESTAURANT_CURRENCY_CODE must be a 3-letter ISO 4217 code (was '{CurrencyCode}').");
        }

        if (!Uri.TryCreate(SourceUrl, UriKind.Absolute, out Uri? sourceUrl)
            || (!string.Equals(sourceUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sourceUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"RESTAURANT_SOURCE_URL must be an absolute http or https URL (was '{SourceUrl}').");
        }

        if (Argon2MemoryKibibytes < MinimumArgon2MemoryKibibytes)
        {
            errors.Add($"ARGON2_MEMORY_KIBIBYTES must be at least {MinimumArgon2MemoryKibibytes} (was {Argon2MemoryKibibytes}).");
        }

        if (Argon2Iterations < MinimumArgon2Iterations)
        {
            errors.Add($"ARGON2_ITERATIONS must be at least {MinimumArgon2Iterations} (was {Argon2Iterations}).");
        }

        if (Argon2Parallelism < MinimumArgon2Parallelism)
        {
            errors.Add($"ARGON2_PARALLELISM must be at least {MinimumArgon2Parallelism} (was {Argon2Parallelism}).");
        }

        if (Argon2MaxConcurrentHashes < 1)
        {
            errors.Add($"ARGON2_MAX_CONCURRENT_HASHES must be at least 1 (was {Argon2MaxConcurrentHashes}).");
        }

        if (TableJoinTokenRotationSeconds < MinimumTableJoinTokenRotationSeconds)
        {
            errors.Add($"TABLE_JOIN_TOKEN_ROTATION_SECONDS must be at least {MinimumTableJoinTokenRotationSeconds} (was {TableJoinTokenRotationSeconds}).");
        }

        if (TableJoinGrantMinutes < MinimumTableJoinGrantMinutes)
        {
            errors.Add($"TABLE_JOIN_GRANT_MINUTES must be at least {MinimumTableJoinGrantMinutes} (was {TableJoinGrantMinutes}).");
        }

        if (TableDisplayPairingCodeMinutes < MinimumTableDisplayPairingCodeMinutes)
        {
            errors.Add($"TABLE_DISPLAY_PAIRING_CODE_MINUTES must be at least {MinimumTableDisplayPairingCodeMinutes} (was {TableDisplayPairingCodeMinutes}).");
        }

        if (GuestRegistrationAttemptsPerWindow < MinimumGuestRegistrationAttemptsPerWindow)
        {
            errors.Add($"GUEST_REGISTRATION_ATTEMPTS_PER_WINDOW must be at least {MinimumGuestRegistrationAttemptsPerWindow} (was {GuestRegistrationAttemptsPerWindow}). This surface is partitioned by client address, and a whole dining room can share one; a smaller budget refuses guests rather than attackers.");
        }

        if (GuestRegistrationWindowMinutes < MinimumGuestRegistrationWindowMinutes)
        {
            errors.Add($"GUEST_REGISTRATION_WINDOW_MINUTES must be at least {MinimumGuestRegistrationWindowMinutes} (was {GuestRegistrationWindowMinutes}).");
        }

        if (KitchenSubmissionReminderSeconds < 1)
        {
            errors.Add($"KITCHEN_SUBMISSION_REMINDER_SECONDS must be at least 1 (was {KitchenSubmissionReminderSeconds}).");
        }

        foreach (string pattern in TrustedOriginPatterns)
        {
            if (!IsValidOriginPattern(pattern))
            {
                errors.Add($"RESTAURANT_TRUSTED_ORIGIN_PATTERNS entry '{pattern}' must be an https origin like 'https://*.trycloudflare.com' (scheme://host, optional leading '*.' wildcard label, no path or port).");
            }
        }

        return errors;
    }

    public string ResolveWebAuthnRelyingPartyId() => new Uri(PublicOrigin).Host;

    public TimeZoneInfo ResolveTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public bool UsesTwelveHourClock => !TwentyFourHourSpellings.Contains(ClockFormat.Trim());

    private static bool IsKnownClockFormat(string clockFormat)
    {
        string value = clockFormat.Trim();
        return TwelveHourSpellings.Contains(value) || TwentyFourHourSpellings.Contains(value);
    }

    private static bool TryResolveTimeZone(string timeZoneId)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static string ReadString(IConfiguration configuration, string key, string fallback)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
        => int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static IReadOnlyList<string> ReadOriginPatterns(
        IConfiguration configuration,
        string key,
        IReadOnlyList<string> fallback)
    {
        string? raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        char[] separators = new[] { ',', ' ', '\t', '\n', '\r' };
        string[] parts = raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? fallback : parts;
    }

    private static bool IsValidOriginPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        string value = pattern.Trim().ToLowerInvariant();
        const string prefix = "https://";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string host = value[prefix.Length..];
        if (host.Length == 0 || host.AsSpan().ContainsAny("/?#@ :"))
        {
            return false;
        }

        string bare = host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;
        return bare.Length > 0 && !bare.Contains('*') && bare.Contains('.');
    }
}
