using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Security;

namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// The custom <see cref="IPasswordHasher{TUser}"/> (TECHNICAL_SPECIFICATION §3.2, ADR-0008).
/// Identity's PBKDF2 hasher is deliberately not registered; this replaces it. It computes
/// Argon2id with Konscious.Security.Cryptography, encodes/decodes the PHC string through the
/// pure <see cref="Argon2PhcString"/> helper (Domain), and:
/// <list type="bullet">
///   <item>hashes with a fresh 16-byte CSPRNG salt and a 32-byte tag using the configured cost
///   parameters;</item>
///   <item>verifies by parsing the <b>stored</b> parameters, recomputing, and comparing with
///   <see cref="CryptographicOperations.FixedTimeEquals"/>;</item>
///   <item>returns <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> when the stored
///   parameters differ from the configured ones, so Identity transparently rehashes at sign-in;</item>
///   <item>bounds concurrent computations with a process-wide semaphore (each hash costs roughly
///   <c>MemoryKibibytes</c>), so a burst of sign-ins cannot exhaust memory — excess callers queue.</item>
/// </list>
/// Register this as a <b>singleton</b> so the semaphore is genuinely process-wide. The optional
/// duration hook feeds the <c>password_hash_duration_milliseconds</c> histogram (§12); the hasher
/// itself takes no dependency on the metrics type, keeping DataAccess free of that concern
/// (the same decoupling pattern as <see cref="SchemaMigrationRunner"/>'s failure callback).
///
/// <para>The <b>startup floor guard</b> (§3.2) lives in <c>RestaurantOptions.Validate</c>, not
/// here: this type honours whatever parameters it is handed. It never logs or stores the
/// plaintext password and zeroes the transient password bytes after each computation.</para>
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher<Person>, IDisposable
{
    /// <summary>Per-hash salt length in bytes (§3.2).</summary>
    public const int SaltByteCount = 16;

    /// <summary>Argon2 tag (output) length in bytes (§3.2).</summary>
    public const int TagByteCount = 32;

    private readonly Argon2HashingOptions _options;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly Action<double>? _onHashDurationMilliseconds;

    public Argon2idPasswordHasher(Argon2HashingOptions options, Action<double>? onHashDurationMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentHashes, 1);

        _options = options;
        _concurrencyGate = new SemaphoreSlim(options.MaxConcurrentHashes, options.MaxConcurrentHashes);
        _onHashDurationMilliseconds = onHashDurationMilliseconds;
    }

    public string HashPassword(Person user, string password)
    {
        // The user is intentionally unused: there is no per-user pepper (a single KnownSecret would
        // couple every hash to one key, complicating rotation with no benefit over Argon2id here).
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteCount);
        byte[] tag = ComputeTag(password, salt, _options.MemoryKibibytes, _options.Iterations, _options.Parallelism);

        return Argon2PhcString.Encode(
            new Argon2Parameters(_options.MemoryKibibytes, _options.Iterations, _options.Parallelism, salt, tag));
    }

    public PasswordVerificationResult VerifyHashedPassword(Person user, string hashedPassword, string providedPassword)
    {
        // A malformed or empty stored hash, or an empty attempt, is simply "no match" — never an exception.
        if (string.IsNullOrEmpty(hashedPassword)
            || string.IsNullOrEmpty(providedPassword)
            || !Argon2PhcString.TryParse(hashedPassword, out Argon2Parameters? stored))
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] candidateTag = ComputeTag(
            providedPassword, stored.Salt, stored.MemoryKibibytes, stored.Iterations, stored.Parallelism);

        if (!CryptographicOperations.FixedTimeEquals(candidateTag, stored.Tag))
        {
            return PasswordVerificationResult.Failed;
        }

        return Argon2PhcString.NeedsRehash(
                stored, _options.MemoryKibibytes, _options.Iterations, _options.Parallelism)
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    private byte[] ComputeTag(string password, byte[] salt, int memoryKibibytes, int iterations, int parallelism)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

        // Bound total concurrent Argon2 computations before allocating ~MemoryKibibytes for this one.
        _concurrencyGate.Wait();
        try
        {
            long startedAt = Stopwatch.GetTimestamp();

            using Argon2id argon2 = new(passwordBytes)
            {
                Salt = salt,
                MemorySize = memoryKibibytes,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };

            byte[] tag = argon2.GetBytes(TagByteCount);
            _onHashDurationMilliseconds?.Invoke(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return tag;
        }
        finally
        {
            _concurrencyGate.Release();
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public void Dispose() => _concurrencyGate.Dispose();
}

/// <summary>
/// The Argon2id cost parameters plus the concurrency bound (TECHNICAL_SPECIFICATION §3.2/§13).
/// A tiny record so <see cref="Argon2idPasswordHasher"/> stays free of the web layer's
/// <c>RestaurantOptions</c>; the composition root maps one to the other.
/// </summary>
public sealed record Argon2HashingOptions(
    int MemoryKibibytes,
    int Iterations,
    int Parallelism,
    int MaxConcurrentHashes);
