using Dapper;
using MyRestaurant.DataAccess;
using Npgsql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

/// <summary>
/// Integration tests for the DbUp migration runner (TECHNICAL_SPECIFICATION §14/§16) against a real
/// PostgreSQL 17 container: the initial schema applies cleanly, a second run is a no-op (idempotent),
/// <see cref="SchemaMigrationRunner.IsUpToDate"/> reports current, and the key relations, the key columns
/// and the <c>citext</c> extension exist afterwards.
///
/// <para><b>Why columns are checked at all, when a relation check would be cheaper.</b> Because
/// <c>0004</c> is the first migration in this tree that is entirely an <c>ALTER</c>: it creates no
/// relation, so every existing fact in this file passes on a tree where it never ran. And it is the first
/// to depend on <c>dbup-postgresql</c>'s dollar-quote handling — a splitter that broke the <c>DO</c> block
/// would leave the table with no CHECK constraints and the columns absent, which is precisely a state no
/// relation check can see.</para>
/// </summary>
public sealed class SchemaMigrationRunnerTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public SchemaMigrationRunnerTests(PostgreSqlFixture fixture) => _fixture = fixture;

    // A representative subset of the tables and views — enough to prove every script ran to the end. The
    // counts this comment used to carry ("the 22 tables and 5 views") are derivable from the migrations
    // and were therefore a second copy of a fact that goes stale the moment one is added, which is what
    // 0003 just did (F-47).
    public static TheoryData<string> KeyRelations =>
    [
        "public.person",
        "public.passkey_credential",
        "public.menu_item",
        "public.menu_section",       // 0003
        "public.menu_section_event", // 0003
        "public.guest_order",
        "public.order_event",
        "public.order_operation_line_added",
        "public.table_sitting",
        "public.order_current_line",   // view
        "public.order_current_state",  // view
        "public.sitting_bill",         // view
        "public.kitchen_pending_line", // view
    ];

    /// <summary>
    /// Columns a later migration added by <c>ALTER</c>, as <c>table.column</c>. Deliberately not every
    /// column in the schema: <c>0001</c>'s are proven by the relation existing, and a census of all of
    /// them would be a second copy of the DDL that goes stale the moment one is added (F-47). What earns a
    /// row here is a column that arrived <em>after</em> its table did, because that is the only kind whose
    /// absence leaves every other fact in this file green.
    /// </summary>
    public static TheoryData<string> KeyColumnsAddedByAlter =>
    [
        "menu_item.description",              // 0004
        "menu_item.display_order",            // 0004
        "menu_item_event.new_description",    // 0004
        "menu_item_event.new_display_order",  // 0004
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

    [Theory]
    [MemberData(nameof(KeyColumnsAddedByAlter))]
    public async Task Run_AddsKeyColumn(string qualifiedColumn)
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        BuildRunner(connectionString).Run();

        string[] parts = qualifiedColumn.Split('.');
        Assert.Equal(2, parts.Length);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        bool exists = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = @Table
                      AND column_name = @Column)
                """,
                new { Table = parts[0], Column = parts[1] },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(exists, $"Expected column '{qualifiedColumn}' to exist after migration.");
    }

    /// <summary>
    /// <c>0004</c> replaced <c>menu_item_event</c>'s four generated CHECK names with named ones, inside a
    /// dollar-quoted <c>DO</c> block — the first in this tree, and therefore the first statement whose
    /// survival depends on <c>dbup-postgresql</c>'s splitter consuming a tagged block rather than breaking
    /// it at the first internal semicolon.
    ///
    /// <para><b>The names are asserted, not the count.</b> A count would be one fact written twice, and
    /// <c>0005</c> adds a fifth (F-47). The names are what <c>0005</c> will <c>DROP CONSTRAINT</c> by, so
    /// they are the thing whose absence would break it — and a broken splitter shows up here as every one
    /// of them missing rather than as a migration failure, because <c>DO</c> with a truncated body is
    /// still valid SQL that does less.</para>
    /// </summary>
    [Fact]
    public async Task Run_NamesTheMenuItemEventCheckConstraints()
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        BuildRunner(connectionString).Run();

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        IEnumerable<string> names = await connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT conname
                FROM pg_constraint
                WHERE conrelid = 'menu_item_event'::regclass
                  AND contype = 'c'
                ORDER BY conname
                """,
                cancellationToken: TestContext.Current.CancellationToken));

        HashSet<string> found = [.. names];

        foreach (string expected in new[]
        {
            "menu_item_event_description_payload",
            "menu_item_event_display_order_payload",
            "menu_item_event_name_payload",
            "menu_item_event_price_payload",
            "menu_item_event_type_vocabulary",
        })
        {
            Assert.Contains(expected, found);
        }

        // And nothing generated survives. A leftover menu_item_event_check would mean the DO block ran
        // against a partial list, which is the failure mode 0005 would inherit.
        Assert.DoesNotContain("menu_item_event_check", found);
        Assert.DoesNotContain("menu_item_event_check1", found);
    }

    private static SchemaMigrationRunner BuildRunner(string connectionString)
        => new(connectionString)
        {
            // Keep the test snappy: the container is already up, so no long connection-retry budget.
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        };
}
