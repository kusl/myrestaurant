using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Tables;

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
        Assert.Equal(stored, read);

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

        Assert.Null(read);
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

        Assert.NotEqual(before, after);
        Assert.Equal(
            JoinTokenValidationResult.Invalid,
            JoinTokenService.Validate(after, tableId, tokenFromOldSecret, _clock.UtcNow, RotationSeconds));
    }

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
