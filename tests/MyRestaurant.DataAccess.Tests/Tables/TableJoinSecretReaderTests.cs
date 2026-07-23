using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Tables;

/// <summary>
/// Integration tests for <see cref="DapperTableJoinSecretReader"/> (the server-only join-secret read,
/// TECHNICAL_SPECIFICATION §4.1/§4.3) against a real PostgreSQL 17 container. They pin the properties the
/// join-token service relies on: the reader returns the exact 32 bytes stored for an active table (real
/// signing material — a token computed from them validates); it returns <c>null</c> for an unknown table
/// and for a deactivated one (the §4.1 active-gate); and after a rotation it returns the new bytes, so a
/// token minted from the old secret no longer validates.
///
/// <para>Data is arranged through the real <see cref="DapperTableAdministration"/> (create / rotate /
/// deactivate) so the reader is tested against rows written exactly the way the app writes them. Each
/// test truncates <c>restaurant_table CASCADE</c> first (xUnit builds a fresh instance per test and runs
/// them sequentially). Own <see cref="PostgreSqlFixture"/>; if no container engine is available, every
/// test skips — mirroring <see cref="TableAdministrationTests"/>.</para>
/// </summary>
public sealed class TableJoinSecretReaderTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const int RotationSeconds = 60;

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public TableJoinSecretReaderTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is null)
        {
            return;
        }

        new SchemaMigrationRunner(_fixture.ConnectionString)
        {
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        }.Run();

        _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "TRUNCATE TABLE restaurant_table CASCADE;",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadActiveJoinSecretAsync_ReturnsTheStoredSecret_ThatSignsAValidToken()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = _identifiers.Create();
        Assert.Equal(CreateTableOutcome.Created, await Administration().CreateTableAsync(tableId, "Table 5", cancellationToken));

        byte[] stored = await ReadRawSecretAsync(tableId, cancellationToken);
        byte[]? read = await Reader().ReadActiveJoinSecretAsync(tableId, cancellationToken);

        Assert.NotNull(read);
        Assert.Equal(SecretGenerator.JoinSecretByteCount, read!.Length);
        Assert.Equal(stored, read); // exact bytes, compared element-wise

        // The returned bytes are the live signing material: a token computed from them validates.
        string token = JoinTokenService.ComputeCurrentToken(read, tableId, _clock.UtcNow, RotationSeconds);
        Assert.Equal(
            JoinTokenValidationResult.Valid,
            JoinTokenService.Validate(read, tableId, token, _clock.UtcNow, RotationSeconds));
    }

    [Fact]
    public async Task ReadActiveJoinSecretAsync_ReturnsNull_ForUnknownTable()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        byte[]? read = await Reader().ReadActiveJoinSecretAsync(_identifiers.Create(), cancellationToken);

        Assert.Null(read);
    }

    [Fact]
    public async Task ReadActiveJoinSecretAsync_ReturnsNull_ForDeactivatedTable()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = _identifiers.Create();
        Assert.Equal(CreateTableOutcome.Created, await Administration().CreateTableAsync(tableId, "Patio", cancellationToken));
        Assert.Equal(
            TableActivationOutcome.Changed,
            await Administration().SetTableActiveAsync(tableId, isActive: false, cancellationToken));

        byte[]? read = await Reader().ReadActiveJoinSecretAsync(tableId, cancellationToken);

        Assert.Null(read); // §4.1: a deactivated table has no readable secret
    }

    [Fact]
    public async Task ReadActiveJoinSecretAsync_ReflectsRotation_OldTokenNoLongerValidates()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = _identifiers.Create();
        Assert.Equal(CreateTableOutcome.Created, await Administration().CreateTableAsync(tableId, "Bar 1", cancellationToken));

        byte[] before = (await Reader().ReadActiveJoinSecretAsync(tableId, cancellationToken))!;
        string tokenFromOldSecret = JoinTokenService.ComputeCurrentToken(before, tableId, _clock.UtcNow, RotationSeconds);

        Assert.Equal(
            RotateJoinSecretOutcome.Rotated,
            await Administration().RotateJoinSecretAsync(tableId, cancellationToken));

        byte[] after = (await Reader().ReadActiveJoinSecretAsync(tableId, cancellationToken))!;

        Assert.NotEqual(before, after); // the reader tracks the rotated bytes
        Assert.Equal(
            JoinTokenValidationResult.Invalid,
            JoinTokenService.Validate(after, tableId, tokenFromOldSecret, _clock.UtcNow, RotationSeconds));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperTableAdministration Administration() => new(_connectionFactory!, _clock);

    private DapperTableJoinSecretReader Reader() => new(_connectionFactory!);

    private async Task<byte[]> ReadRawSecretAsync(Guid tableId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return (await connection.ExecuteScalarAsync<byte[]>(new CommandDefinition(
            "SELECT join_secret FROM restaurant_table WHERE restaurant_table_identifier = @Id;",
            new { Id = tableId },
            cancellationToken: cancellationToken)))!;
    }
}
