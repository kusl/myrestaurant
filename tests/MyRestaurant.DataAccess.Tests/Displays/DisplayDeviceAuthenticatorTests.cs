using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Displays;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Displays;

public sealed class DisplayDeviceAuthenticatorTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2E$dGFndGFndGFndGFndGFndGFndGFndGFndGE";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 4, 2, 11, 0, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public DisplayDeviceAuthenticatorTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
    public async Task AuthenticateAsync_ReturnsTheSession_ForTheIssuedSecret()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice device = await PairDeviceAsync("Table 3", "Window tablet", cancellationToken);

        DisplayDeviceSession? session = await Authenticator()
            .AuthenticateAsync(device.DeviceIdentifier, device.Secret, cancellationToken);

        Assert.NotNull(session);
        Assert.Equal(device.DeviceIdentifier, session!.DeviceIdentifier);
        Assert.Equal(device.TableIdentifier, session.TableIdentifier);
        Assert.Equal("Window tablet", session.DeviceLabel);
        Assert.Equal("Table 3", session.TableLabel);
        Assert.True(session.TableIsActive);
    }

    [Theory]
    [InlineData("not-the-secret")]
    [InlineData("")]
    public async Task AuthenticateAsync_ReturnsNull_ForASecretThatDoesNotMatch(string presented)
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice device = await PairDeviceAsync("Table 3", "Tablet", cancellationToken);

        Assert.Null(await Authenticator().AuthenticateAsync(device.DeviceIdentifier, presented, cancellationToken));
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsNull_ForAnotherDevicesSecret()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice first = await PairDeviceAsync("Table 3", "First", cancellationToken);
        PairedDevice second = await PairDeviceAsync("Table 4", "Second", cancellationToken);

        Assert.Null(await Authenticator().AuthenticateAsync(first.DeviceIdentifier, second.Secret, cancellationToken));
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsNull_ForAnUnknownDevice()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice device = await PairDeviceAsync("Table 3", "Tablet", cancellationToken);

        Assert.Null(await Authenticator().AuthenticateAsync(_identifiers.Create(), device.Secret, cancellationToken));
        Assert.Null(await Authenticator().AuthenticateAsync(Guid.Empty, device.Secret, cancellationToken));
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsNull_OnceTheDeviceIsRevoked()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice device = await PairDeviceAsync("Table 3", "Tablet", cancellationToken);

        Assert.NotNull(await Authenticator().AuthenticateAsync(device.DeviceIdentifier, device.Secret, cancellationToken));

        Assert.Equal(
            RevokeDisplayDeviceOutcome.Revoked,
            await Pairing().RevokeDeviceAsync(device.DeviceIdentifier, device.AdministratorIdentifier, cancellationToken));

        Assert.Null(await Authenticator().AuthenticateAsync(device.DeviceIdentifier, device.Secret, cancellationToken));
        Assert.Null(await Authenticator().RevalidateAsync(device.DeviceIdentifier, cancellationToken));
    }

    [Fact]
    public async Task AuthenticateAsync_KeepsTheDeviceButReportsADeactivatedTable()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice device = await PairDeviceAsync("Table 3", "Tablet", cancellationToken);

        Assert.Equal(
            TableActivationOutcome.Changed,
            await Administration().SetTableActiveAsync(device.TableIdentifier, isActive: false, cancellationToken));

        DisplayDeviceSession? session = await Authenticator()
            .AuthenticateAsync(device.DeviceIdentifier, device.Secret, cancellationToken);

        Assert.NotNull(session);
        Assert.False(session!.TableIsActive);
    }

    [Fact]
    public async Task AuthenticateAsync_TouchesLastSeenAtAtMostOncePerMinute()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice device = await PairDeviceAsync("Table 3", "Tablet", cancellationToken);
        Assert.Null(await ReadLastSeenAtAsync(device.DeviceIdentifier, cancellationToken));

        DateTimeOffset first = _clock.UtcNow;
        Assert.NotNull(await Authenticator().AuthenticateAsync(device.DeviceIdentifier, device.Secret, cancellationToken));
        Assert.Equal(first, await ReadLastSeenAtAsync(device.DeviceIdentifier, cancellationToken));

        _clock.UtcNow = first.AddSeconds(30);
        Assert.NotNull(await Authenticator().AuthenticateAsync(device.DeviceIdentifier, device.Secret, cancellationToken));
        Assert.Equal(first, await ReadLastSeenAtAsync(device.DeviceIdentifier, cancellationToken));

        DateTimeOffset later = first.AddSeconds(61);
        _clock.UtcNow = later;
        Assert.NotNull(await Authenticator().AuthenticateAsync(device.DeviceIdentifier, device.Secret, cancellationToken));
        Assert.Equal(later, await ReadLastSeenAtAsync(device.DeviceIdentifier, cancellationToken));
    }

    [Fact]
    public async Task RevalidateAsync_ResolvesWithoutASecretAndAlsoHeartbeats()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PairedDevice device = await PairDeviceAsync("Table 3", "Tablet", cancellationToken);

        DisplayDeviceSession? session = await Authenticator().RevalidateAsync(device.DeviceIdentifier, cancellationToken);

        Assert.NotNull(session);
        Assert.Equal(device.TableIdentifier, session!.TableIdentifier);
        Assert.Equal(_clock.UtcNow, await ReadLastSeenAtAsync(device.DeviceIdentifier, cancellationToken));

        Assert.Null(await Authenticator().RevalidateAsync(_identifiers.Create(), cancellationToken));
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperTableAdministration Administration() => new(_connectionFactory!, _clock);

    private DapperDisplayDevicePairing Pairing() => new(_connectionFactory!, _clock, _identifiers);

    private DapperDisplayDeviceAuthenticator Authenticator() => new(_connectionFactory!, _clock);

    private async Task<PairedDevice> PairDeviceAsync(string tableLabel, string deviceLabel, CancellationToken cancellationToken)
    {
        Guid tableId = _identifiers.Create();
        Assert.Equal(
            CreateTableOutcome.Created,
            await Administration().CreateTableAsync(tableId, tableLabel, cancellationToken));

        Guid administrator = await SeedPersonAsync($"admin-{Guid.NewGuid():N}"[..16], cancellationToken);

        IssuePairingCodeResult issued = await Pairing()
            .IssuePairingCodeAsync(tableId, administrator, CodeLifetime, cancellationToken);
        Assert.Equal(IssuePairingCodeOutcome.Issued, issued.Outcome);

        RedeemPairingCodeResult redeemed = await Pairing()
            .RedeemPairingCodeAsync(issued.Code!, deviceLabel, cancellationToken);
        Assert.Equal(RedeemPairingCodeOutcome.Paired, redeemed.Outcome);

        return new PairedDevice(
            redeemed.DeviceIdentifier!.Value,
            tableId,
            administrator,
            redeemed.DeviceSecret!);
    }

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

    private async Task<DateTimeOffset?> ReadLastSeenAtAsync(Guid deviceIdentifier, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        DateTime? lastSeenAt = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            SELECT last_seen_at
            FROM table_display_device
            WHERE table_display_device_identifier = @DeviceIdentifier;
            """,
            new { DeviceIdentifier = deviceIdentifier },
            cancellationToken: cancellationToken));

        return lastSeenAt is { } value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : null;
    }

    private sealed record PairedDevice(
        Guid DeviceIdentifier,
        Guid TableIdentifier,
        Guid AdministratorIdentifier,
        string Secret);
}
