using System.Globalization;

namespace MyRestaurant.WebApplication.Configuration;

/// <summary>
/// The complete environment-only configuration (TECHNICAL_SPECIFICATION §13; REQUIREMENTS §8:
/// "environment variables only"). Bound once at startup from the flat, long-named,
/// prefixed variables, then <see cref="Validate"/> is run before HTTP is bound so the process
/// fails fast on invalid security-relevant configuration (§13, §3.2 floor guard).
/// </summary>
public sealed class RestaurantOptions
{
    // Argon2 floor guard (TECHNICAL_SPECIFICATION §3.2). Below any of these the process must not start.
    public const int MinimumArgon2MemoryKibibytes = 19456;
    public const int MinimumArgon2Iterations = 2;
    public const int MinimumArgon2Parallelism = 1;

    // Token/grant/pairing lower bounds (§13: "rotation/grant/pairing values ≥ 10 s / ≥ 1 min / ≥ 1 min").
    public const int MinimumTableJoinTokenRotationSeconds = 10;
    public const int MinimumTableJoinGrantMinutes = 1;
    public const int MinimumTableDisplayPairingCodeMinutes = 1;

    /// <summary>
    /// The default trusted WebAuthn origin patterns (TECHNICAL_SPECIFICATION §3.3, ADR-0005). Cloudflare
    /// Quick Tunnels hand out a random <c>*.trycloudflare.com</c> hostname per run, so trusting that
    /// wildcard lets passkeys work in a quick-tunnel demo without knowing the URL at startup. This gates
    /// only WebAuthn relying-party derivation; it is deliberately not a general CORS allowance.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTrustedOriginPatterns = ["https://*.trycloudflare.com"];

    public required string RestaurantName { get; init; }
    public required string PublicOrigin { get; init; }

    /// <summary>
    /// Additional browser origins that may act as the WebAuthn relying party, as wildcard patterns
    /// (<c>RESTAURANT_TRUSTED_ORIGIN_PATTERNS</c>; §3.3, ADR-0005). The configured <see cref="PublicOrigin"/>
    /// and loopback (in development) are always trusted; these extend that set so a Cloudflare quick
    /// tunnel's per-run <c>*.trycloudflare.com</c> host works without being known at startup. Defaults to
    /// <see cref="DefaultTrustedOriginPatterns"/>; not required so existing constructions keep the default.
    /// </summary>
    public IReadOnlyList<string> TrustedOriginPatterns { get; init; } = DefaultTrustedOriginPatterns;

    public required string TimeZoneId { get; init; }
    public required string CurrencyCode { get; init; }
    public required string DatabaseConnectionString { get; init; }
    public required string DataProtectionKeysDirectory { get; init; }
    public required int KitchenSubmissionReminderSeconds { get; init; }
    public required int TableJoinTokenRotationSeconds { get; init; }
    public required int TableJoinGrantMinutes { get; init; }
    public required int TableDisplayPairingCodeMinutes { get; init; }
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
            CurrencyCode = ReadString(configuration, "RESTAURANT_CURRENCY_CODE", "USD"),
            DatabaseConnectionString = ReadString(
                configuration,
                "RESTAURANT_DATABASE_CONNECTION_STRING",
                "Host=localhost;Port=5432;Database=myrestaurant;Username=myrestaurant;Password=myrestaurant"),
            DataProtectionKeysDirectory = ReadString(configuration, "DATA_PROTECTION_KEYS_DIRECTORY", "/var/lib/myrestaurant/dataprotection"),
            KitchenSubmissionReminderSeconds = ReadInt(configuration, "KITCHEN_SUBMISSION_REMINDER_SECONDS", 60),
            TableJoinTokenRotationSeconds = ReadInt(configuration, "TABLE_JOIN_TOKEN_ROTATION_SECONDS", 60),
            TableJoinGrantMinutes = ReadInt(configuration, "TABLE_JOIN_GRANT_MINUTES", 10),
            TableDisplayPairingCodeMinutes = ReadInt(configuration, "TABLE_DISPLAY_PAIRING_CODE_MINUTES", 10),
            Argon2MemoryKibibytes = ReadInt(configuration, "ARGON2_MEMORY_KIBIBYTES", 65536),
            Argon2Iterations = ReadInt(configuration, "ARGON2_ITERATIONS", 3),
            Argon2Parallelism = ReadInt(configuration, "ARGON2_PARALLELISM", 1),
            Argon2MaxConcurrentHashes = ReadInt(configuration, "ARGON2_MAX_CONCURRENT_HASHES", 4),
        };
    }

    /// <summary>Returns a human-readable reason for every invalid setting; empty means valid.</summary>
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

        if (CurrencyCode.Length != 3 || !CurrencyCode.All(char.IsAsciiLetter))
        {
            errors.Add($"RESTAURANT_CURRENCY_CODE must be a 3-letter ISO 4217 code (was '{CurrencyCode}').");
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

    /// <summary>
    /// The host of the configured public origin (§3.3). As of ADR-0005 the WebAuthn relying-party ID
    /// is derived <em>per request</em> from the request host (so a quick tunnel's per-run hostname
    /// works), not pinned to this value; this remains the canonical configured host — used for QR join
    /// URLs (ADR-0009) and as the fallback host presented by <see cref="Identity.PublicOriginMiddleware"/>.
    /// </summary>
    public string ResolveWebAuthnRelyingPartyId() => new Uri(PublicOrigin).Host;

    /// <summary>The configured display time zone (validated at startup).</summary>
    public TimeZoneInfo ResolveTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

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

    /// <summary>
    /// Reads a comma/space/newline-separated list of origin patterns, trimming empties; returns the
    /// supplied default when the variable is unset or contains no non-empty entries.
    /// </summary>
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

    /// <summary>
    /// A trusted-origin pattern is <c>scheme://host</c> with an https scheme, a non-empty host that
    /// may begin with a single <c>*.</c> wildcard label, and no path, query, fragment, userinfo, or
    /// port. This matches the runtime matcher in <see cref="Identity.WebAuthnOriginPolicy"/>.
    /// </summary>
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
