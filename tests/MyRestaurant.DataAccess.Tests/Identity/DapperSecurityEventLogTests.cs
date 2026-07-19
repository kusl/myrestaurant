using System.Data.Common;
using Dapper;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.Domain.Identifiers;
using Xunit;

namespace MyRestaurant.DataAccess.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="DapperSecurityEventLog"/> (TECHNICAL_SPECIFICATION §3.5, §8.2,
/// §16.2) against a real PostgreSQL 17 container: a self-inflicted event stores a null actor; an
/// administrative event stores the acting administrator; and the CHECK-backed vocabulary is guarded
/// client-side (that last test needs no container and always runs). If no container engine is
/// available, the database-backed tests skip.
/// </summary>
public sealed class DapperSecurityEventLogTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private NpgsqlDatabaseConnectionFactory? _connectionFactory;

    public DapperSecurityEventLogTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        if (_fixture.ConnectionString is not null)
        {
            new SchemaMigrationRunner(_fixture.ConnectionString)
            {
                MaximumAttempts = 3,
                DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
            }.Run();

            _connectionFactory = new NpgsqlDatabaseConnectionFactory(_fixture.ConnectionString);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionFactory is not null)
        {
            await _connectionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task RecordAsync_SelfInflictedEvent_StoresRowWithNullActor()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid subject = await InsertPersonAsync(UniqueUsername("subject"), cancellationToken);
        DapperSecurityEventLog log = BuildLog();

        await log.RecordAsync(subject, actorPersonIdentifier: null, SecurityEventType.SignInSucceeded, cancellationToken);

        SecurityEventRow row = await ReadSingleEventAsync(subject, cancellationToken);
        Assert.Equal(SecurityEventType.SignInSucceeded, row.EventType);
        Assert.Null(row.ActorPersonIdentifier);
        Assert.Equal(_clock.UtcNow, row.OccurredAt);
    }

    [Fact]
    public async Task RecordAsync_AdministrativeEvent_StoresTheActingAdministrator()
    {
        SkipIfNoContainer();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid subject = await InsertPersonAsync(UniqueUsername("staff"), cancellationToken);
        Guid administrator = await InsertPersonAsync(UniqueUsername("admin"), cancellationToken);
        DapperSecurityEventLog log = BuildLog();

        await log.RecordAsync(subject, administrator, SecurityEventType.RoleGranted, cancellationToken);

        SecurityEventRow row = await ReadSingleEventAsync(subject, cancellationToken);
        Assert.Equal(SecurityEventType.RoleGranted, row.EventType);
        Assert.Equal(administrator, row.ActorPersonIdentifier);
    }

    [Fact]
    public async Task RecordAsync_UnknownEventType_ThrowsBeforeTouchingTheDatabase()
    {
        // No container needed: the client-side guard rejects the value before opening a connection.
        // The token is passed so xUnit's test cancellation stays responsive (xUnit1051).
        DapperSecurityEventLog log = new(new ThrowingConnectionFactory(), _clock, new UuidV7IdentifierFactory());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => log.RecordAsync(
                Guid.CreateVersion7(),
                actorPersonIdentifier: null,
                "not_a_real_event",
                TestContext.Current.CancellationToken));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private DapperSecurityEventLog BuildLog()
        => new(_connectionFactory!, _clock, new UuidV7IdentifierFactory());

    private void SkipIfNoContainer()
        => Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");

    private static string UniqueUsername(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private async Task<Guid> InsertPersonAsync(string username, CancellationToken cancellationToken)
    {
        Guid identifier = Guid.CreateVersion7();
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO person (person_identifier, username, security_stamp, created_at)
            VALUES (@Id, @Username, @Stamp, @CreatedAt);
            """,
            new
            {
                Id = identifier,
                Username = username,
                Stamp = Guid.NewGuid(),
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken));

        return identifier;
    }

    private async Task<SecurityEventRow> ReadSingleEventAsync(Guid subject, CancellationToken cancellationToken)
    {
        // Alias snake_case columns to the row's PascalCase properties so Dapper matches by name without
        // the global MatchNamesWithUnderscores setting the store deliberately avoids.
        await using DbConnection connection = await _connectionFactory!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<SecurityEventRow>(new CommandDefinition(
            """
            SELECT event_type               AS EventType,
                   actor_person_identifier  AS ActorPersonIdentifier,
                   occurred_at              AS OccurredAt
            FROM security_event
            WHERE subject_person_identifier = @Subject;
            """,
            new { Subject = subject },
            cancellationToken: cancellationToken));
    }

    /// <summary>One projected <c>security_event</c> row for assertions.</summary>
    private sealed class SecurityEventRow
    {
        public string EventType { get; init; } = string.Empty;
        public Guid? ActorPersonIdentifier { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
    }

    /// <summary>An <see cref="IDatabaseConnectionFactory"/> that must never be called; proves the guard runs first.</summary>
    private sealed class ThrowingConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The connection factory must not be reached for an invalid event type.");
    }
}
