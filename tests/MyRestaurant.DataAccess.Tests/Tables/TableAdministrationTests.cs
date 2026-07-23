using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Tables;

/// <summary>
/// Integration tests for <see cref="DapperTableAdministration"/> and <see cref="DapperTableDirectory"/>
/// (table management, TECHNICAL_SPECIFICATION §4.1) against a real PostgreSQL 17 container. They pin the
/// behaviours that make the slice correct: creating a table writes a row with a 32-byte join secret and
/// an unset <c>join_secret_rotated_at</c>; labels are unique; rename detects no-change and collisions;
/// rotating the secret changes the stored bytes and stamps <c>join_secret_rotated_at</c>;
/// deactivate/reactivate flips <c>is_active</c>; and the directory reads tables back oldest-first
/// without ever exposing the secret.
///
/// <para>Each test truncates <c>restaurant_table CASCADE</c> first (xUnit builds a fresh instance per
/// test and runs them sequentially). Own <c>IClassFixture</c> (own container); if no container engine
/// is available, every test skips — mirroring <see cref="AccountAdministrationTests"/>.</para>
/// </summary>
public sealed class TableAdministrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public TableAdministrationTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    public async Task CreateTableAsync_WritesTableWithJoinSecret_AndDirectoryReadsItBack()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperTableAdministration administration = Build();

        Guid tableId = _identifiers.Create();
        CreateTableOutcome outcome = await administration.CreateTableAsync(tableId, "  Table 5  ", cancellationToken);

        Assert.Equal(CreateTableOutcome.Created, outcome);

        TableProbeRow row = await ReadTableAsync(tableId, cancellationToken);
        Assert.Equal("Table 5", row.Label); // trimmed
        Assert.True(row.IsActive);
        Assert.Equal(32, row.JoinSecret.Length);
        Assert.Null(row.JoinSecretRotatedAt);

        RestaurantTableSummary? summary = await Directory().GetTableAsync(tableId, cancellationToken);
        Assert.NotNull(summary);
        Assert.Equal("Table 5", summary!.Label);
        Assert.True(summary.IsActive);
        Assert.Null(summary.JoinSecretRotatedAt);
        Assert.Equal(tableId, summary.TableIdentifier);
    }

    [Fact]
    public async Task CreateTableAsync_DuplicateLabel_ReturnsLabelTaken_AndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperTableAdministration administration = Build();

        Assert.Equal(
            CreateTableOutcome.Created,
            await administration.CreateTableAsync(_identifiers.Create(), "Table 1", cancellationToken));

        CreateTableOutcome duplicate =
            await administration.CreateTableAsync(_identifiers.Create(), "Table 1", cancellationToken);

        Assert.Equal(CreateTableOutcome.LabelTaken, duplicate);
        Assert.Equal(1, await CountTablesAsync(cancellationToken));
    }

    [Fact]
    public async Task RenameTableAsync_Renames_ThenSameLabelIsNoChange_ThenCollisionIsLabelTaken()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperTableAdministration administration = Build();

        Guid first = _identifiers.Create();
        await administration.CreateTableAsync(first, "Table 1", cancellationToken);
        await administration.CreateTableAsync(_identifiers.Create(), "Table 2", cancellationToken);

        Assert.Equal(
            RenameTableOutcome.Renamed,
            await administration.RenameTableAsync(first, "Patio", cancellationToken));
        Assert.Equal("Patio", (await ReadTableAsync(first, cancellationToken)).Label);

        Assert.Equal(
            RenameTableOutcome.NoChange,
            await administration.RenameTableAsync(first, "Patio", cancellationToken));

        Assert.Equal(
            RenameTableOutcome.LabelTaken,
            await administration.RenameTableAsync(first, "Table 2", cancellationToken));

        // The failed collision left the label untouched.
        Assert.Equal("Patio", (await ReadTableAsync(first, cancellationToken)).Label);
    }

    [Fact]
    public async Task RenameTableAsync_UnknownTable_ReturnsTableNotFound()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            RenameTableOutcome.TableNotFound,
            await Build().RenameTableAsync(_identifiers.Create(), "Ghost", cancellationToken));
    }

    [Fact]
    public async Task RotateJoinSecretAsync_ChangesSecretAndStampsRotatedAt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperTableAdministration administration = Build();

        Guid tableId = _identifiers.Create();
        await administration.CreateTableAsync(tableId, "Bar 1", cancellationToken);
        byte[] original = (await ReadTableAsync(tableId, cancellationToken)).JoinSecret;

        Assert.Equal(
            RotateJoinSecretOutcome.Rotated,
            await administration.RotateJoinSecretAsync(tableId, cancellationToken));

        TableProbeRow afterFirst = await ReadTableAsync(tableId, cancellationToken);
        Assert.Equal(32, afterFirst.JoinSecret.Length);
        Assert.NotEqual(original, afterFirst.JoinSecret); // secret changed (element-wise byte comparison)
        Assert.NotNull(afterFirst.JoinSecretRotatedAt);
        Assert.Equal(_clock.UtcNow, afterFirst.JoinSecretRotatedAt.Value); // stamped at the operation instant

        // A second rotation changes it again.
        await administration.RotateJoinSecretAsync(tableId, cancellationToken);
        byte[] afterSecond = (await ReadTableAsync(tableId, cancellationToken)).JoinSecret;
        Assert.NotEqual(afterFirst.JoinSecret, afterSecond);
    }

    [Fact]
    public async Task RotateJoinSecretAsync_UnknownTable_ReturnsTableNotFound()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            RotateJoinSecretOutcome.TableNotFound,
            await Build().RotateJoinSecretAsync(_identifiers.Create(), cancellationToken));
    }

    [Fact]
    public async Task SetTableActiveAsync_Deactivates_ThenReactivates_AndNoChangeWhenAlready()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperTableAdministration administration = Build();

        Guid tableId = _identifiers.Create();
        await administration.CreateTableAsync(tableId, "Table 9", cancellationToken);

        Assert.Equal(
            TableActivationOutcome.Changed,
            await administration.SetTableActiveAsync(tableId, false, cancellationToken));
        Assert.False((await ReadTableAsync(tableId, cancellationToken)).IsActive);

        Assert.Equal(
            TableActivationOutcome.NoChange,
            await administration.SetTableActiveAsync(tableId, false, cancellationToken));

        Assert.Equal(
            TableActivationOutcome.Changed,
            await administration.SetTableActiveAsync(tableId, true, cancellationToken));
        Assert.True((await ReadTableAsync(tableId, cancellationToken)).IsActive);
    }

    [Fact]
    public async Task SetTableActiveAsync_UnknownTable_ReturnsTableNotFound()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            TableActivationOutcome.TableNotFound,
            await Build().SetTableActiveAsync(_identifiers.Create(), false, cancellationToken));
    }

    [Fact]
    public async Task ListTablesAsync_ReturnsEveryTableOldestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DapperTableAdministration administration = Build();

        // Advance the clock between creations so created_at differs and the oldest-first ordering is
        // exercised independently of the label. Labels are deliberately out of alphabetical order.
        await administration.CreateTableAsync(_identifiers.Create(), "Zeta", cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await administration.CreateTableAsync(_identifiers.Create(), "Alpha", cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await administration.CreateTableAsync(_identifiers.Create(), "Mu", cancellationToken);

        IReadOnlyList<RestaurantTableSummary> tables = await Directory().ListTablesAsync(cancellationToken);

        Assert.Equal(new[] { "Zeta", "Alpha", "Mu" }, tables.Select(table => table.Label).ToArray());
    }

    [Fact]
    public async Task GetTableAsync_UnknownTable_ReturnsNull()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await Directory().GetTableAsync(_identifiers.Create(), cancellationToken));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperTableAdministration Build() => new(_connectionFactory!, _clock);

    private DapperTableDirectory Directory() => new(_connectionFactory!);

    private async Task<TableProbeRow> ReadTableAsync(Guid tableId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<TableProbeRow>(new CommandDefinition(
            """
            SELECT label AS Label, is_active AS IsActive, join_secret AS JoinSecret,
                   join_secret_rotated_at AS JoinSecretRotatedAt
            FROM restaurant_table WHERE restaurant_table_identifier = @Id;
            """,
            new { Id = tableId }, cancellationToken: cancellationToken));
    }

    private async Task<int> CountTablesAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM restaurant_table;", cancellationToken: cancellationToken));
    }

    // Plain mutable POCO so Dapper's default property mapping applies; the SELECT aliases its
    // snake_case columns to these PascalCase names. join_secret (bytea) maps to byte[].
    private sealed class TableProbeRow
    {
        public string Label { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public byte[] JoinSecret { get; set; } = [];
        public DateTimeOffset? JoinSecretRotatedAt { get; set; }
    }
}
