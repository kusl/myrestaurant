using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using MyRestaurant.Domain.Security;

namespace MyRestaurant.DataAccess.Identity;

public sealed class Argon2idPasswordHasher : IPasswordHasher<Person>, IDisposable
{
    public const int SaltByteCount = 16;

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
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteCount);
        byte[] tag = ComputeTag(password, salt, _options.MemoryKibibytes, _options.Iterations, _options.Parallelism);

        return Argon2PhcString.Encode(
            new Argon2Parameters(_options.MemoryKibibytes, _options.Iterations, _options.Parallelism, salt, tag));
    }

    public PasswordVerificationResult VerifyHashedPassword(Person user, string hashedPassword, string providedPassword)
    {
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

public sealed record Argon2HashingOptions(
    int MemoryKibibytes,
    int Iterations,
    int Parallelism,
    int MaxConcurrentHashes);
