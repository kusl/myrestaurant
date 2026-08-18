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
///
/// <para><b><c>0005</c> is the first migration whose interesting output is a CONSTRAINT rather than a
/// relation or a column.</b> It adds <c>menu_item.menu_section_identifier</c> in three statements — add
/// nullable, backfill, tighten — so a script that stopped after the first leaves a column every other
/// fact here finds and every integration test writes NULL into.
/// <see cref="Run_MakesTheMenuItemSectionReferenceMandatory"/> is the assertion that the <c>NOT NULL</c>
/// and the foreign key actually landed.</para>
///
/// <para><b><c>0006</c> is back to the cheapest shape a migration in this tree can have</b>, which is the
/// shape <c>0003</c> had: two relations and nothing existing touched. So it needs no new <em>kind</em> of
/// fact — two rows on <see cref="KeyRelations"/> are the whole of what this file has to say about it, and
/// the interesting properties of those tables (the media-type vocabulary agreeing with the domain, the byte
/// cap being the database's) are asserted where they can be observed, in
/// <c>Menu/MenuItemImageTests.cs</c>, against writes rather than against DDL.</para>
///
/// <para><b>This file is also the gate on <c>WithVariablesDisabled()</c>, and that is deliberate
/// (F-78).</b> dbup-core substitutes <c>$name$</c> before the splitter runs, and PostgreSQL spells a
/// dollar-quoted body the same way, so <c>0004</c>'s <c>DO $migrate_menu_item_event_checks$</c> was read
/// as a reference to an undefined variable and threw before its first statement — which took every fact
/// here, and every test whose fixture applies the schema, red at once. The repair is one builder call in
/// <see cref="SchemaMigrationRunner"/>, and the script keeps its <em>tagged</em> body rather than being
/// reduced to <c>$$</c> precisely so that no separate assertion is needed: delete that call and this
/// class fails on the next run. No new test is added for it, on F-47's reasoning — a gate that already
/// exists and already blocks does not need a monument beside it.</para>
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
        "public.menu_item_image",       // 0006
        "public.menu_item_image_event", // 0006
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
        "menu_item.description",                        // 0004
        "menu_item.display_order",                      // 0004
        "menu_item_event.new_description",              // 0004
        "menu_item_event.new_display_order",            // 0004
        "menu_item.menu_section_identifier",            // 0005
        "menu_item_event.new_menu_section_identifier",  // 0005
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
            "menu_item_event_section_payload",
            "menu_item_event_type_vocabulary",
        })
        {
            Assert.Contains(expected, found);
        }

        // And nothing generated survives. A leftover menu_item_event_check would mean the DO block ran
        // against a partial list, which is the failure mode 0005 inherited and did not meet.
        Assert.DoesNotContain("menu_item_event_check", found);
        Assert.DoesNotContain("menu_item_event_check1", found);
    }

    /// <summary>
    /// <c>0005</c>'s three structural facts, which no other test in this tree can see.
    ///
    /// <para><b>Why a column check is not enough here.</b> <c>menu_item.menu_section_identifier</c> is
    /// added <c>NULL</c>, backfilled, and then tightened — three statements — and a script that stopped
    /// after the first leaves a column that <see cref="Run_AddsKeyColumn"/> finds and every integration
    /// test happily writes NULL into. The <c>NOT NULL</c> and the foreign key are the whole point of the
    /// migration, and they are the two things its own header calls the expensive part.</para>
    ///
    /// <para>The index is asserted for a different reason: PostgreSQL does not index the referencing side
    /// of a foreign key on its own, so an absent index is a silent scan of <c>menu_item</c> on every
    /// statement touching a section, and nothing else here would ever say so.</para>
    /// </summary>
    [Fact]
    public async Task Run_MakesTheMenuItemSectionReferenceMandatory()
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        BuildRunner(connectionString).Run();

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        bool notNull = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT attnotnull
                FROM pg_attribute
                WHERE attrelid = 'menu_item'::regclass
                  AND attname = 'menu_section_identifier'
                """,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(notNull, "0005 must leave menu_item.menu_section_identifier NOT NULL (§7).");

        bool foreignKey = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'menu_item'::regclass
                      AND contype = 'f'
                      AND conname = 'menu_item_menu_section_reference')
                """,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(foreignKey, "0005 must name the foreign key so a later migration can drop it by name.");

        bool index = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT to_regclass('public.menu_item_section_index') IS NOT NULL",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(index, "0005 must index the referencing side of the foreign key.");
    }

    /// <summary>
    /// The seed is conditional, and this is the branch every test in this project takes: a fresh database
    /// has no menu items, so it gets <b>no</b> section and the administrator names their own (§7).
    ///
    /// <para>The other branch — an existing installation whose items are backfilled under one seeded
    /// heading — cannot be reached from here, because this fixture's database is created empty and DbUp
    /// applies every script in one pass. It is exercised by hand against a populated database and
    /// recorded in <c>BUILD_PROGRESS.md</c> as such rather than claimed here.</para>
    /// </summary>
    [Fact]
    public async Task Run_SeedsNoSectionOnAFreshDatabase()
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        BuildRunner(connectionString).Run();

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        int sections = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*)::int FROM menu_section",
                cancellationToken: TestContext.Current.CancellationToken));

        // Not Assert.Equal(0, …): this fixture's container is shared with nothing, but a migration that
        // seeded unconditionally would put exactly one row here and the message should say which rule
        // was broken rather than which number was wrong.
        Assert.True(
            sections == 0,
            $"§7: a fresh database gets no sections and the administrator names their own. Found"
                + $" {sections}, which means 0005's EXISTS (SELECT 1 FROM menu_item) guard did not hold.");
    }

    private static SchemaMigrationRunner BuildRunner(string connectionString)
        => new(connectionString)
        {
            // Keep the test snappy: the container is already up, so no long connection-retry budget.
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        };
}
