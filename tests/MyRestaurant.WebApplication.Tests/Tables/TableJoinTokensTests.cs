using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Tables;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Unit tests for <see cref="TableJoinTokens"/> — the application-layer wrapper over the vector-tested
/// domain <see cref="JoinTokenService"/> (TECHNICAL_SPECIFICATION §4.3–§4.5). No database: the secret
/// read is faked by a stub <see cref="ITableJoinSecretReader"/>, so these pin the wrapper's own logic —
/// building the current QR (token, scan URL, inline SVG, next-rotation instant) for an active table,
/// returning nothing for a table with no secret, and mapping a presented token to the domain validation
/// result (current, previous, or neither) including the missing-table → invalid case. Options come from
/// an empty configuration (all §13 defaults: rotation 60 s, origin <c>https://localhost:8443</c>); the
/// real <see cref="RestaurantMetrics"/> is constructed so the emission call path runs, though the counter
/// value is not asserted here (integration/wiring cover the plumbing).
/// </summary>
public sealed class TableJoinTokensTests : IDisposable
{
    private const int RotationSeconds = 60;

    private static readonly Guid KnownTable = Guid.Parse("0192f000-0000-7000-8000-000000000abc");
    private static readonly byte[] KnownSecret = CreateSecret();

    private readonly DateTimeOffset _now = new(2026, 7, 17, 12, 0, 30, TimeSpan.Zero);
    private readonly ServiceProvider _metricsProvider;
    private readonly RestaurantMetrics _metrics;
    private readonly RestaurantOptions _options;

    public TableJoinTokensTests()
    {
        _metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _metrics = new RestaurantMetrics(_metricsProvider.GetRequiredService<IMeterFactory>());
        _options = RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _metricsProvider.Dispose();
    }

    [Fact]
    public async Task DescribeCurrentAsync_ForActiveTable_ProducesTheCurrentTokenUrlSvgAndNextRotation()
    {
        TableJoinTokens tokens = Build(secretForKnownTable: true);

        TableJoinQrCode? qr = await tokens.DescribeCurrentAsync(KnownTable, TestContext.Current.CancellationToken);

        Assert.NotNull(qr);
        Assert.Equal(KnownTable, qr!.TableIdentifier);

        string expectedToken = JoinTokenService.ComputeCurrentToken(KnownSecret, KnownTable, _now, RotationSeconds);
        Assert.Equal(expectedToken, qr.Token);
        Assert.Equal(JoinTokenService.BuildJoinUrl(_options.PublicOrigin, KnownTable, expectedToken), qr.JoinUrl);
        Assert.Equal(JoinTokenService.NextRotationInstant(_now, RotationSeconds), qr.NextRotationAt);
        Assert.Equal(_now, qr.GeneratedAt);

        // Rendered server-side as a self-contained inline SVG (§4.3), not a client call.
        Assert.StartsWith("<svg", qr.QrCodeSvg, StringComparison.Ordinal);
        Assert.Contains("viewBox", qr.QrCodeSvg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeCurrentAsync_ForTableWithNoSecret_ReturnsNull()
    {
        TableJoinTokens tokens = Build(secretForKnownTable: false);

        TableJoinQrCode? qr = await tokens.DescribeCurrentAsync(KnownTable, TestContext.Current.CancellationToken);

        Assert.Null(qr);
    }

    [Fact]
    public async Task ValidateAsync_ForCurrentWindowToken_IsValid()
    {
        TableJoinTokens tokens = Build(secretForKnownTable: true);
        string token = JoinTokenService.ComputeCurrentToken(KnownSecret, KnownTable, _now, RotationSeconds);

        JoinTokenValidationResult result = await tokens.ValidateAsync(KnownTable, token, TestContext.Current.CancellationToken);

        Assert.Equal(JoinTokenValidationResult.Valid, result);
    }

    [Fact]
    public async Task ValidateAsync_ForPreviousWindowToken_IsValid()
    {
        TableJoinTokens tokens = Build(secretForKnownTable: true);
        long previousWindow = JoinTokenService.CurrentWindowIndex(_now, RotationSeconds) - 1;
        string token = JoinTokenService.ComputeToken(KnownSecret, KnownTable, previousWindow);

        JoinTokenValidationResult result = await tokens.ValidateAsync(KnownTable, token, TestContext.Current.CancellationToken);

        Assert.Equal(JoinTokenValidationResult.Valid, result);
    }

    [Fact]
    public async Task ValidateAsync_ForGarbageToken_IsInvalid()
    {
        TableJoinTokens tokens = Build(secretForKnownTable: true);

        JoinTokenValidationResult result = await tokens.ValidateAsync(KnownTable, "not-a-real-token", TestContext.Current.CancellationToken);

        Assert.Equal(JoinTokenValidationResult.Invalid, result);
    }

    [Fact]
    public async Task ValidateAsync_WhenTableHasNoSecret_IsInvalid()
    {
        TableJoinTokens tokens = Build(secretForKnownTable: false);

        // Even a token that *would* be correct against the secret is invalid when the table has none (§4.1).
        string token = JoinTokenService.ComputeCurrentToken(KnownSecret, KnownTable, _now, RotationSeconds);
        JoinTokenValidationResult result = await tokens.ValidateAsync(KnownTable, token, TestContext.Current.CancellationToken);

        Assert.Equal(JoinTokenValidationResult.Invalid, result);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private TableJoinTokens Build(bool secretForKnownTable)
        => new(
            new StubSecretReader(secretForKnownTable ? KnownSecret : null),
            _metrics,
            new FixedClock(_now),
            _options);

    private static byte[] CreateSecret()
    {
        byte[] secret = new byte[SecretGenerator.JoinSecretByteCount];
        for (int index = 0; index < secret.Length; index++)
        {
            secret[index] = (byte)index;
        }

        return secret;
    }

    /// <summary>Returns the configured secret only for <see cref="KnownTable"/>; every other table has none.</summary>
    private sealed class StubSecretReader : ITableJoinSecretReader
    {
        private readonly byte[]? _secret;

        public StubSecretReader(byte[]? secret) => _secret = secret;

        public Task<byte[]?> ReadActiveJoinSecretAsync(Guid tableIdentifier, CancellationToken cancellationToken = default)
            => Task.FromResult(tableIdentifier == KnownTable ? _secret : null);
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }
    }
}
