using Dapper;
using MyRestaurant.DataAccess;
using Npgsql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

public sealed class SchemaMigrationRunnerTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public SchemaMigrationRunnerTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public static TheoryData<string> KeyRelations =>
    [
        "public.person",
        "public.passkey_credential",
        "public.menu_item",
        "public.menu_section",
        "public.menu_section_event",
        "public.menu_item_image",
        "public.menu_item_image_event",
        "public.menu_item_reaction_event",
        "public.menu_item_reaction_current",
        "public.guest_order",
        "public.order_event",
        "public.order_operation_line_added",
        "public.table_sitting",
        "public.order_current_line",
        "public.order_current_state",
        "public.sitting_bill",
        "public.kitchen_pending_line",
    ];

    public static TheoryData<string> KeyColumnsAddedByAlter =>
    [
        "menu_item.description",
        "menu_item.display_order",
        "menu_item_event.new_description",
        "menu_item_event.new_display_order",
        "menu_item.menu_section_identifier",
        "menu_item_event.new_menu_section_identifier",
    ];

    [Fact]
    public void Run_AppliesSchema_AndIsIdempotent()
    {
        Assert.SkipUnless(_fixture.ConnectionString is not null, _fixture.SkipReason ?? "No container engine.");
        string connectionString = _fixture.ConnectionString!;

        SchemaMigrationRunner runner = BuildRunner(connectionString);

        runner.Run();
        Assert.True(runner.IsUpToDate());

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

        Assert.DoesNotContain("menu_item_event_check", found);
        Assert.DoesNotContain("menu_item_event_check1", found);
    }

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

        Assert.True(
            sections == 0,
            $"§7: a fresh database gets no sections and the administrator names their own. Found"
                + $" {sections}, which means 0005's EXISTS (SELECT 1 FROM menu_item) guard did not hold.");
    }

    private static SchemaMigrationRunner BuildRunner(string connectionString)
        => new(connectionString)
        {
            MaximumAttempts = 3,
            DelayBetweenAttempts = TimeSpan.FromMilliseconds(200),
        };
}
