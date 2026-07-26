using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Displays;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Security;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Displays;

/// <summary>
/// Integration tests for <see cref="DapperDisplayDevicePairing"/> and
/// <see cref="DapperDisplayDeviceDirectory"/> (TECHNICAL_SPECIFICATION §4.2) against a real PostgreSQL 17
/// container. They pin the properties the pairing surface depends on: the plaintext code is never
/// stored, only its SHA-256 hash; redeeming it once creates the device and burns the code; every way of
/// failing — unknown, reused, expired, malformed, table deactivated — writes nothing; a code typed the
/// way a human types it still works; and revocation is stamped once and stays stamped.
///
/// <para>Data is arranged through the real <see cref="DapperTableAdministration"/> so the pairing
/// service is tested against rows written exactly the way the app writes them. Each test truncates the
/// people, tables, and display tables first (xUnit builds a fresh instance per test and runs them
/// sequentially). Own <see cref="PostgreSqlFixture"/>; if no container engine is available every test
/// skips — mirroring <see cref="Tables.TableAdministrationTests"/>.</para>
/// </summary>
public sealed class DisplayDevicePairingTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2E$dGFndGFndGFndGFndGFndGFndGFndGFndGE";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 3, 9, 17, 15, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public DisplayDevicePairingTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
            """
            TRUNCATE TABLE person, restaurant_table, table_display_device, table_display_pairing_code CASCADE;
            """,
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
    public async Task IssuePairingCodeAsync_StoresOnlyTheHashOfAWellFormedSingleUseCode()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 1", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);

        IssuePairingCodeResult result = await Pairing()
            .IssuePairingCodeAsync(tableId, administrator, CodeLifetime, cancellationToken);

        Assert.Equal(IssuePairingCodeOutcome.Issued, result.Outcome);
        Assert.NotNull(result.Code);
        Assert.True(PairingCode.IsWellFormed(result.Code!));
        Assert.Equal(_clock.UtcNow + CodeLifetime, result.ExpiresAt);

        PairingCodeProbeRow row = await ReadOnlyPairingCodeAsync(cancellationToken);
        Assert.Equal(tableId, row.RestaurantTableIdentifier);
        Assert.Equal(administrator, row.CreatedByPersonIdentifier);
        Assert.Equal(_clock.UtcNow, row.CreatedAt);
        Assert.Equal(_clock.UtcNow + CodeLifetime, row.ExpiresAt);
        Assert.Null(row.UsedAt);

        // §4.2: stored hashed. The row must hold sha256(code) and nowhere the plaintext.
        Assert.Equal(Sha256Hashing.Hash(result.Code!), row.CodeHash);
        Assert.Equal(Sha256Hashing.HashByteCount, row.CodeHash.Length);
    }

    [Fact]
    public async Task IssuePairingCodeAsync_RefusesADeactivatedTableAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Patio", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        Assert.Equal(
            TableActivationOutcome.Changed,
            await Administration().SetTableActiveAsync(tableId, isActive: false, cancellationToken));

        IssuePairingCodeResult result = await Pairing()
            .IssuePairingCodeAsync(tableId, administrator, CodeLifetime, cancellationToken);

        Assert.Equal(IssuePairingCodeOutcome.TableUnavailable, result.Outcome);
        Assert.Null(result.Code);
        Assert.Equal(0, await CountAsync("SELECT count(*)::int FROM table_display_pairing_code;", cancellationToken));
    }

    [Fact]
    public async Task RedeemPairingCodeAsync_CreatesTheDeviceAndBurnsTheCode()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 4", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        string code = await IssueCodeAsync(tableId, administrator, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        RedeemPairingCodeResult result = await Pairing()
            .RedeemPairingCodeAsync(code, "Window tablet", cancellationToken);

        Assert.Equal(RedeemPairingCodeOutcome.Paired, result.Outcome);
        Assert.Equal(tableId, result.TableIdentifier);
        Assert.Equal("Table 4", result.TableLabel);
        Assert.NotNull(result.DeviceIdentifier);
        Assert.NotNull(result.DeviceSecret);

        // 32 CSPRNG bytes as unpadded Base64Url is 43 characters (§4.2).
        Assert.Equal(43, result.DeviceSecret!.Length);
        Assert.DoesNotContain(':', result.DeviceSecret, StringComparison.Ordinal);

        DisplayDeviceProbeRow device = await ReadOnlyDeviceAsync(cancellationToken);
        Assert.Equal(result.DeviceIdentifier, device.TableDisplayDeviceIdentifier);
        Assert.Equal(tableId, device.RestaurantTableIdentifier);
        Assert.Equal("Window tablet", device.DeviceLabel);
        Assert.Equal(administrator, device.PairedByPersonIdentifier);
        Assert.Equal(_clock.UtcNow, device.PairedAt);
        Assert.Null(device.RevokedAt);
        Assert.Null(device.RevokedByPersonIdentifier);
        Assert.Null(device.LastSeenAt);

        // §4.2: the server stores only sha256(secret) — of the Base64Url text that travels in the cookie.
        Assert.Equal(Sha256Hashing.Hash(result.DeviceSecret), device.DeviceSecretHash);

        PairingCodeProbeRow burnt = await ReadOnlyPairingCodeAsync(cancellationToken);
        Assert.Equal(_clock.UtcNow, burnt.UsedAt);

        // And the directory shows it, with the pairer resolved to a username and no hash in sight.
        IReadOnlyList<TableDisplayDeviceSummary> listed =
            await Directory().ListDevicesForTableAsync(tableId, cancellationToken);
        TableDisplayDeviceSummary summary = Assert.Single(listed);
        Assert.Equal("Window tablet", summary.DeviceLabel);
        Assert.Equal("ada", summary.PairedByUsername);
        Assert.False(summary.IsRevoked);
        Assert.Null(summary.RevokedByUsername);
        Assert.Null(summary.LastSeenAt);
    }

    [Fact]
    public async Task RedeemPairingCodeAsync_IsSingleUse()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 5", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        string code = await IssueCodeAsync(tableId, administrator, cancellationToken);

        Assert.Equal(
            RedeemPairingCodeOutcome.Paired,
            (await Pairing().RedeemPairingCodeAsync(code, "First", cancellationToken)).Outcome);

        RedeemPairingCodeResult second = await Pairing().RedeemPairingCodeAsync(code, "Second", cancellationToken);

        // §4.2: single-use. A used code is indistinguishable from one that never existed.
        Assert.Equal(RedeemPairingCodeOutcome.CodeNotRecognized, second.Outcome);
        Assert.Null(second.DeviceSecret);
        Assert.Equal(1, await CountAsync("SELECT count(*)::int FROM table_display_device;", cancellationToken));
    }

    [Fact]
    public async Task RedeemPairingCodeAsync_RejectsAnExpiredCode()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 6", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        string code = await IssueCodeAsync(tableId, administrator, cancellationToken);

        _clock.UtcNow = _clock.UtcNow + CodeLifetime + TimeSpan.FromSeconds(1);

        RedeemPairingCodeResult result = await Pairing().RedeemPairingCodeAsync(code, "Late", cancellationToken);

        Assert.Equal(RedeemPairingCodeOutcome.CodeNotRecognized, result.Outcome);
        Assert.Equal(0, await CountAsync("SELECT count(*)::int FROM table_display_device;", cancellationToken));

        // The code is untouched, not burnt: a failed attempt "burns nothing but the rate budget" (§4.2).
        Assert.Null((await ReadOnlyPairingCodeAsync(cancellationToken)).UsedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SHORT")]
    [InlineData("ABCDEFGHIJKLMNOP")]
    [InlineData("ABCDEFG!")]
    public async Task RedeemPairingCodeAsync_RejectsCodesThatCannotBeOurs(string presented)
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 7", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        _ = await IssueCodeAsync(tableId, administrator, cancellationToken);

        RedeemPairingCodeResult result = await Pairing().RedeemPairingCodeAsync(presented, null, cancellationToken);

        Assert.Equal(RedeemPairingCodeOutcome.CodeNotRecognized, result.Outcome);
        Assert.Equal(0, await CountAsync("SELECT count(*)::int FROM table_display_device;", cancellationToken));
    }

    [Fact]
    public async Task RedeemPairingCodeAsync_AcceptsTheCodeTheWayAPersonTypesIt()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 8", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        string code = await IssueCodeAsync(tableId, administrator, cancellationToken);

        // Lower case, hyphenated in the middle, and padded with the space a phone keyboard adds.
        string asTyped = $" {code[..4].ToLowerInvariant()}-{code[4..].ToLowerInvariant()} ";

        RedeemPairingCodeResult result = await Pairing().RedeemPairingCodeAsync(asTyped, null, cancellationToken);

        Assert.Equal(RedeemPairingCodeOutcome.Paired, result.Outcome);
        Assert.Equal(tableId, result.TableIdentifier);
    }

    [Fact]
    public async Task RedeemPairingCodeAsync_LabelsTheDeviceFromItsTableWhenNoneIsGiven()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 9", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        string code = await IssueCodeAsync(tableId, administrator, cancellationToken);

        RedeemPairingCodeResult result = await Pairing().RedeemPairingCodeAsync(code, "   ", cancellationToken);

        Assert.Equal(RedeemPairingCodeOutcome.Paired, result.Outcome);
        Assert.Equal("Table 9 display", (await ReadOnlyDeviceAsync(cancellationToken)).DeviceLabel);
    }

    [Fact]
    public async Task RedeemPairingCodeAsync_RefusesWhenTheTableWasDeactivatedAfterTheCodeWasIssued()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 10", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        string code = await IssueCodeAsync(tableId, administrator, cancellationToken);

        Assert.Equal(
            TableActivationOutcome.Changed,
            await Administration().SetTableActiveAsync(tableId, isActive: false, cancellationToken));

        RedeemPairingCodeResult result = await Pairing().RedeemPairingCodeAsync(code, "Tablet", cancellationToken);

        // §4.1: a deactivated table takes no new displays. Nothing is written, and the code is not burnt.
        Assert.Equal(RedeemPairingCodeOutcome.TableUnavailable, result.Outcome);
        Assert.Equal(0, await CountAsync("SELECT count(*)::int FROM table_display_device;", cancellationToken));
        Assert.Null((await ReadOnlyPairingCodeAsync(cancellationToken)).UsedAt);
    }

    [Fact]
    public async Task RevokeDeviceAsync_StampsOnceThenReportsAlreadyRevoked()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 11", cancellationToken);
        Guid administrator = await SeedPersonAsync("ada", cancellationToken);
        Guid other = await SeedPersonAsync("grace", cancellationToken);
        string code = await IssueCodeAsync(tableId, administrator, cancellationToken);
        Guid deviceId = (await Pairing().RedeemPairingCodeAsync(code, "Tablet", cancellationToken)).DeviceIdentifier!.Value;

        _clock.UtcNow = _clock.UtcNow.AddHours(3);
        DateTimeOffset revokedAt = _clock.UtcNow;

        Assert.Equal(
            RevokeDisplayDeviceOutcome.Revoked,
            await Pairing().RevokeDeviceAsync(deviceId, administrator, cancellationToken));

        // A second attempt, by someone else and later, must not overwrite who revoked it or when.
        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        Assert.Equal(
            RevokeDisplayDeviceOutcome.AlreadyRevoked,
            await Pairing().RevokeDeviceAsync(deviceId, other, cancellationToken));

        DisplayDeviceProbeRow row = await ReadOnlyDeviceAsync(cancellationToken);
        Assert.Equal(revokedAt, row.RevokedAt);
        Assert.Equal(administrator, row.RevokedByPersonIdentifier);

        TableDisplayDeviceSummary summary =
            Assert.Single(await Directory().ListDevicesForTableAsync(tableId, cancellationToken));
        Assert.True(summary.IsRevoked);
        Assert.Equal("ada", summary.RevokedByUsername);
    }

    [Fact]
    public async Task RevokeDeviceAsync_ReportsAMissingDevice()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid administrator = await SeedPersonAsync("ada", cancellationToken);

        Assert.Equal(
            RevokeDisplayDeviceOutcome.DeviceNotFound,
            await Pairing().RevokeDeviceAsync(_identifiers.Create(), administrator, cancellationToken));
    }

    [Fact]
    public async Task GetDeviceAsync_ReturnsNullForAnUnknownDevice()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await Directory().GetDeviceAsync(_identifiers.Create(), cancellationToken));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperTableAdministration Administration() => new(_connectionFactory!, _clock);

    private DapperDisplayDevicePairing Pairing() => new(_connectionFactory!, _clock, _identifiers);

    private DapperDisplayDeviceDirectory Directory() => new(_connectionFactory!);

    private async Task<Guid> CreateTableAsync(string label, CancellationToken cancellationToken)
    {
        Guid tableId = _identifiers.Create();
        Assert.Equal(
            CreateTableOutcome.Created,
            await Administration().CreateTableAsync(tableId, label, cancellationToken));
        return tableId;
    }

    private async Task<string> IssueCodeAsync(Guid tableId, Guid administrator, CancellationToken cancellationToken)
    {
        IssuePairingCodeResult issued = await Pairing()
            .IssuePairingCodeAsync(tableId, administrator, CodeLifetime, cancellationToken);
        Assert.Equal(IssuePairingCodeOutcome.Issued, issued.Outcome);
        return issued.Code!;
    }

    /// <summary>Seeds a bare active person (a password, no roles, no obligations) and returns its id.</summary>
    private async Task<Guid> SeedPersonAsync(string username, CancellationToken cancellationToken)
    {
        Guid id = _identifiers.Create();
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person (
                person_identifier, username, display_name, email_address, phone_number,
                password_hash, totp_secret_protected, must_change_password, must_enroll_totp,
                security_stamp, failed_access_count, lockout_end_at, is_active, created_at)
            VALUES (
                @Id, @Username, NULL, NULL, NULL,
                @PasswordHash, NULL, false, false,
                @Stamp, 0, NULL, true, @CreatedAt);
            """,
            new
            {
                Id = id,
                Username = username,
                PasswordHash = SamplePasswordHash,
                Stamp = Guid.NewGuid(),
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken));
        return id;
    }

    private async Task<PairingCodeProbeRow> ReadOnlyPairingCodeAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<PairingCodeProbeRow>(new CommandDefinition(
            """
            SELECT restaurant_table_identifier  AS RestaurantTableIdentifier,
                   code_hash                    AS CodeHash,
                   created_by_person_identifier AS CreatedByPersonIdentifier,
                   created_at                   AS CreatedAt,
                   expires_at                   AS ExpiresAt,
                   used_at                      AS UsedAt
            FROM table_display_pairing_code;
            """,
            cancellationToken: cancellationToken));
    }

    private async Task<DisplayDeviceProbeRow> ReadOnlyDeviceAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<DisplayDeviceProbeRow>(new CommandDefinition(
            """
            SELECT table_display_device_identifier AS TableDisplayDeviceIdentifier,
                   restaurant_table_identifier     AS RestaurantTableIdentifier,
                   device_label                    AS DeviceLabel,
                   device_secret_hash              AS DeviceSecretHash,
                   paired_by_person_identifier     AS PairedByPersonIdentifier,
                   paired_at                       AS PairedAt,
                   revoked_at                      AS RevokedAt,
                   revoked_by_person_identifier    AS RevokedByPersonIdentifier,
                   last_seen_at                    AS LastSeenAt
            FROM table_display_device;
            """,
            cancellationToken: cancellationToken));
    }

    private async Task<int> CountAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
    }

    // Plain mutable POCOs so Dapper's default property mapping applies; the SELECTs alias their
    // snake_case columns to these PascalCase names.
    private sealed class PairingCodeProbeRow
    {
        public Guid RestaurantTableIdentifier { get; set; }
        public byte[] CodeHash { get; set; } = [];
        public Guid CreatedByPersonIdentifier { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? UsedAt { get; set; }
    }

    private sealed class DisplayDeviceProbeRow
    {
        public Guid TableDisplayDeviceIdentifier { get; set; }
        public Guid RestaurantTableIdentifier { get; set; }
        public string DeviceLabel { get; set; } = string.Empty;
        public byte[] DeviceSecretHash { get; set; } = [];
        public Guid PairedByPersonIdentifier { get; set; }
        public DateTimeOffset PairedAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public Guid? RevokedByPersonIdentifier { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }
}
