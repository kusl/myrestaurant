using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.DataAccess.Tests.Identity;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Sittings;

public sealed class SittingMembershipTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private const string SamplePasswordHash =
        "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2E$dGFndGFndGFndGFndGFndGFndGFndGFndGE";

    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 2, 3, 18, 30, 0, TimeSpan.Zero));
    private readonly UuidV7IdentifierFactory _identifiers = new();
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public SittingMembershipTests(PostgreSqlFixture fixture) => _fixture = fixture;

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
            "TRUNCATE TABLE person, person_role, restaurant_table, table_sitting, table_sitting_member CASCADE;",
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
    public async Task JoinTableAsync_FirstJoin_OpensTheSittingAndInsertsTheFirstMembership()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 1", cancellationToken);
        Guid personId = await SeedPersonAsync("ada", "Ada", cancellationToken);

        JoinTableResult result = await Membership().JoinTableAsync(tableId, personId, cancellationToken);

        Assert.Equal(JoinTableOutcome.SittingOpened, result.Outcome);
        Assert.True(result.MembershipInserted);
        Assert.True(result.IsMember);
        Assert.NotNull(result.SittingIdentifier);

        SittingProbeRow sitting = await ReadSittingAsync(result.SittingIdentifier!.Value, cancellationToken);
        Assert.Equal(tableId, sitting.RestaurantTableIdentifier);
        Assert.Equal(_clock.UtcNow, sitting.OpenedAt);
        Assert.Null(sitting.ClosedAt);
        Assert.Null(sitting.SettledTotalAmount);

        Assert.Equal(1, await CountSittingsAsync(cancellationToken));
        Assert.Equal(1, await CountMembersAsync(cancellationToken));
    }

    [Fact]
    public async Task JoinTableAsync_SecondPerson_JoinsTheSameOpenSitting()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 2", cancellationToken);
        Guid first = await SeedPersonAsync("ada", "Ada", cancellationToken);
        Guid second = await SeedPersonAsync("grace", "Grace", cancellationToken);

        JoinTableResult opened = await Membership().JoinTableAsync(tableId, first, cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
        JoinTableResult joined = await Membership().JoinTableAsync(tableId, second, cancellationToken);

        Assert.Equal(JoinTableOutcome.SittingOpened, opened.Outcome);
        Assert.Equal(JoinTableOutcome.JoinedOpenSitting, joined.Outcome);
        Assert.True(joined.MembershipInserted);
        Assert.Equal(opened.SittingIdentifier, joined.SittingIdentifier);

        Assert.Equal(1, await CountSittingsAsync(cancellationToken));
        Assert.Equal(2, await CountMembersAsync(cancellationToken));
    }

    [Fact]
    public async Task JoinTableAsync_SamePersonTwice_IsIdempotentAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 3", cancellationToken);
        Guid personId = await SeedPersonAsync("ada", "Ada", cancellationToken);

        JoinTableResult first = await Membership().JoinTableAsync(tableId, personId, cancellationToken);
        JoinTableResult again = await Membership().JoinTableAsync(tableId, personId, cancellationToken);

        Assert.Equal(JoinTableOutcome.AlreadyMember, again.Outcome);
        Assert.False(again.MembershipInserted);
        Assert.True(again.IsMember);
        Assert.Equal(first.SittingIdentifier, again.SittingIdentifier);

        Assert.Equal(1, await CountSittingsAsync(cancellationToken));
        Assert.Equal(1, await CountMembersAsync(cancellationToken));
    }

    [Fact]
    public async Task JoinTableAsync_DeactivatedTable_ReturnsTableUnavailableAndWritesNothing()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 4", cancellationToken);
        Guid personId = await SeedPersonAsync("ada", "Ada", cancellationToken);

        Assert.Equal(
            TableActivationOutcome.Changed,
            await Administration().SetTableActiveAsync(tableId, false, cancellationToken));

        JoinTableResult result = await Membership().JoinTableAsync(tableId, personId, cancellationToken);

        Assert.Equal(JoinTableOutcome.TableUnavailable, result.Outcome);
        Assert.False(result.IsMember);
        Assert.Null(result.SittingIdentifier);
        Assert.Equal(0, await CountSittingsAsync(cancellationToken));
        Assert.Equal(0, await CountMembersAsync(cancellationToken));
    }

    [Fact]
    public async Task JoinTableAsync_UnknownTable_ReturnsTableUnavailable()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid personId = await SeedPersonAsync("ada", "Ada", cancellationToken);

        JoinTableResult result = await Membership()
            .JoinTableAsync(_identifiers.Create(), personId, cancellationToken);

        Assert.Equal(JoinTableOutcome.TableUnavailable, result.Outcome);
        Assert.Equal(0, await CountSittingsAsync(cancellationToken));
    }

    [Fact]
    public async Task JoinTableAsync_AfterTheSittingIsClosed_OpensANewOne()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 5", cancellationToken);
        Guid personId = await SeedPersonAsync("ada", "Ada", cancellationToken);

        JoinTableResult first = await Membership().JoinTableAsync(tableId, personId, cancellationToken);
        await CloseSittingAsync(first.SittingIdentifier!.Value, personId, cancellationToken);

        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        JoinTableResult second = await Membership().JoinTableAsync(tableId, personId, cancellationToken);

        Assert.Equal(JoinTableOutcome.SittingOpened, second.Outcome);
        Assert.NotEqual(first.SittingIdentifier, second.SittingIdentifier);
        Assert.Equal(2, await CountSittingsAsync(cancellationToken));
        Assert.Equal(2, await CountMembersAsync(cancellationToken));
    }

    [Fact]
    public async Task GetOpenSittingForMemberAsync_ReturnsTheSittingForAMemberAndNullForEveryoneElse()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 6", cancellationToken);
        Guid member = await SeedPersonAsync("ada", "Ada", cancellationToken);
        Guid stranger = await SeedPersonAsync("mallory", null, cancellationToken);

        JoinTableResult joined = await Membership().JoinTableAsync(tableId, member, cancellationToken);

        TableSittingSummary? forMember = await Directory()
            .GetOpenSittingForMemberAsync(tableId, member, cancellationToken);
        TableSittingSummary? forStranger = await Directory()
            .GetOpenSittingForMemberAsync(tableId, stranger, cancellationToken);

        Assert.NotNull(forMember);
        Assert.Equal(joined.SittingIdentifier, forMember!.SittingIdentifier);
        Assert.Equal("Table 6", forMember.TableLabel);
        Assert.Equal(1, forMember.MemberCount);
        Assert.True(forMember.IsOpen);

        Assert.Null(forStranger);
    }

    [Fact]
    public async Task GetOpenSittingAsync_ReturnsNullOnceTheSittingIsClosed()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 7", cancellationToken);
        Guid personId = await SeedPersonAsync("ada", "Ada", cancellationToken);

        JoinTableResult joined = await Membership().JoinTableAsync(tableId, personId, cancellationToken);
        Assert.NotNull(await Directory().GetOpenSittingAsync(tableId, cancellationToken));

        await CloseSittingAsync(joined.SittingIdentifier!.Value, personId, cancellationToken);

        Assert.Null(await Directory().GetOpenSittingAsync(tableId, cancellationToken));
        Assert.Null(await Directory().GetOpenSittingForMemberAsync(tableId, personId, cancellationToken));
    }

    [Fact]
    public async Task ListOpenSittingsForPersonAsync_ReturnsEveryOpenSittingOldestFirst()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid patio = await CreateTableAsync("Patio", cancellationToken);
        Guid bar = await CreateTableAsync("Bar", cancellationToken);
        Guid personId = await SeedPersonAsync("ada", "Ada", cancellationToken);
        Guid other = await SeedPersonAsync("grace", "Grace", cancellationToken);

        await Membership().JoinTableAsync(patio, personId, cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await Membership().JoinTableAsync(bar, personId, cancellationToken);

        Guid window = await CreateTableAsync("Window", cancellationToken);
        await Membership().JoinTableAsync(window, other, cancellationToken);

        IReadOnlyList<TableSittingSummary> sittings = await Directory()
            .ListOpenSittingsForPersonAsync(personId, cancellationToken);

        Assert.Equal(new[] { "Patio", "Bar" }, sittings.Select(sitting => sitting.TableLabel).ToArray());
        Assert.All(sittings, sitting => Assert.True(sitting.IsOpen));
    }

    [Fact]
    public async Task ListMembersAsync_ReturnsTheRosterInJoinOrderAndFallsBackToTheUsername()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid tableId = await CreateTableAsync("Table 8", cancellationToken);
        Guid first = await SeedPersonAsync("ada", "Ada Lovelace", cancellationToken);
        Guid second = await SeedPersonAsync("grace", null, cancellationToken);

        JoinTableResult opened = await Membership().JoinTableAsync(tableId, first, cancellationToken);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
        await Membership().JoinTableAsync(tableId, second, cancellationToken);

        IReadOnlyList<SittingMemberSummary> roster = await Directory()
            .ListMembersAsync(opened.SittingIdentifier!.Value, cancellationToken);

        Assert.Equal(2, roster.Count);
        Assert.Equal(first, roster[0].PersonIdentifier);
        Assert.Equal("Ada Lovelace", roster[0].RosterName);
        Assert.Equal(second, roster[1].PersonIdentifier);

        Assert.Null(roster[1].DisplayName);
        Assert.Equal("grace", roster[1].RosterName);
    }

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private DapperSittingMembership Membership() => new(_connectionFactory!, _clock, _identifiers);

    private DapperSittingDirectory Directory() => new(_connectionFactory!);

    private DapperTableAdministration Administration() => new(_connectionFactory!, _clock);

    private async Task<Guid> CreateTableAsync(string label, CancellationToken cancellationToken)
    {
        Guid tableId = _identifiers.Create();
        Assert.Equal(
            CreateTableOutcome.Created,
            await Administration().CreateTableAsync(tableId, label, cancellationToken));
        return tableId;
    }

    private async Task<Guid> SeedPersonAsync(string username, string? displayName, CancellationToken cancellationToken)
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
                @Id, @Username, @DisplayName, NULL, NULL,
                @PasswordHash, NULL, false, false,
                @Stamp, 0, NULL, true, @CreatedAt);
            """,
            new
            {
                Id = id,
                Username = username,
                DisplayName = displayName,
                PasswordHash = SamplePasswordHash,
                Stamp = Guid.NewGuid(),
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken));
        return id;
    }

    private async Task CloseSittingAsync(Guid sittingId, Guid closedBy, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE table_sitting
            SET closed_at = @ClosedAt,
                closed_by_person_identifier = @ClosedBy,
                settled_total_amount = 0
            WHERE table_sitting_identifier = @SittingId;
            """,
            new { ClosedAt = _clock.UtcNow, ClosedBy = closedBy, SittingId = sittingId },
            cancellationToken: cancellationToken));
    }

    private async Task<SittingProbeRow> ReadSittingAsync(Guid sittingId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<SittingProbeRow>(new CommandDefinition(
            """
            SELECT restaurant_table_identifier AS RestaurantTableIdentifier,
                   opened_at                   AS OpenedAt,
                   closed_at                   AS ClosedAt,
                   settled_total_amount        AS SettledTotalAmount
            FROM table_sitting
            WHERE table_sitting_identifier = @SittingId;
            """,
            new { SittingId = sittingId },
            cancellationToken: cancellationToken));
    }

    private Task<int> CountSittingsAsync(CancellationToken cancellationToken)
        => CountAsync("SELECT count(*)::int FROM table_sitting;", cancellationToken);

    private Task<int> CountMembersAsync(CancellationToken cancellationToken)
        => CountAsync("SELECT count(*)::int FROM table_sitting_member;", cancellationToken);

    private async Task<int> CountAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
    }

    private sealed class SittingProbeRow
    {
        public Guid RestaurantTableIdentifier { get; set; }
        public DateTimeOffset OpenedAt { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
        public decimal? SettledTotalAmount { get; set; }
    }
}
