using Dapper;
using MyRestaurant.DataAccess;
using Npgsql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

/// <summary>
/// Integration tests for the DbUp migration runner (TECHNICAL_SPECIFICATION §14/§16) against a real
/// PostgreSQL 17 container: the initial schema applies cleanly, a second run is a no-op (idempotent),
/// <see cref="SchemaMigrationRunner.IsUpToDate"/> reports current, and the key relations and the
/// <c>citext</c> extension exist afterwards.
/// </summary>
public sealed class SchemaMigrationRunnerTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public SchemaMigrationRunnerTests(PostgreSqlFixture fixture) => _fixture = fixture;

    // A representative subset of the 22 tables and 5 views — enough to prove the script ran to the end.
    public static TheoryData<string> KeyRelations =>
    [
        "public.person",
        "public.passkey_credential",
        "public.menu_item",
        "public.guest_order",
        "public.order_event",
        "public.order_operation_line_added",
        "public.table_sitting",
        "public.order_current_line",   // view
        "public.order_current_state",  // view
        "public.sitting_bill",         // view
        "public.kitchen_pending_line", // view
    ];

    [Fact]
    public void Run_AppliesSchema_AndIsIdempotent()
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        SchemaMigrationRunner runner = BuildRunner(connectionString);

        // First run applies everything and reports current.
        runner.Run();
        Assert.True(runner.IsUpToDate());

        // Second run must be a harmless no-op (DbUp journals executed scripts).
        Exception? secondRun = Record.Exception(() => runner.Run());
        Assert.Null(secondRun);
        Assert.True(runner.IsUpToDate());
    }

    [Fact]
    public async Task Run_CreatesCitextExtension()
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        BuildRunner(connectionString).Run();

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        bool citextInstalled = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citext')",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(citextInstalled);
    }

    [Theory]
    [MemberData(nameof(KeyRelations))]
    public async Task Run_CreatesKeyRelation(string relation)
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        BuildRunner(connectionString).Run();

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        bool exists = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT to_regclass(@Relation) IS NOT NULL",
                new { Relation = relation },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(exists, $"Expected relation '{relation}' to exist after migration.");
    }

    private static SchemaMigrationRunner BuildRunner(string connectionString)
        => new(connectionString)
        {
            // Keep the test snappy: the container is already up, so no long connection-retry budget.
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        };
}
