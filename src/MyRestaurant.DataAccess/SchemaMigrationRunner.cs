using System.Reflection;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace MyRestaurant.DataAccess;

/// <summary>
/// Applies the embedded SQL migrations with DbUp at startup (ADR-0012, TECHNICAL_SPECIFICATION
/// §14/§16). Behaviour:
/// <list type="bullet">
///   <item>Bounded boot retry: at compose start the web container can race PostgreSQL, so
///   connection failures are retried a fixed number of times before giving up.</item>
///   <item>Fail-fast: a genuine migration failure (a bad script) throws immediately — it is
///   never retried, and the application must not bind HTTP with a half-applied schema
///   (§17: "half-applied schema").</item>
///   <item>Idempotent: DbUp journals executed scripts, so a second run is a no-op — this is
///   what lets <c>/healthz/ready</c> assert "migrations current" cheaply.</item>
/// </list>
/// Migration logging goes to the console via DbUp's <c>LogToConsole()</c>; a custom
/// <c>IUpgradeLog</c> is avoided deliberately because its interface shape varies across DbUp
/// versions (BUILD_PROGRESS: known caveats). If the DbUp API differs from what is pinned, this
/// is the most likely place a build break appears — adjust the builder calls here.
/// </summary>
public sealed class SchemaMigrationRunner
{
    private readonly string _connectionString;
    private readonly Action<string>? _onAttemptFailed;

    public SchemaMigrationRunner(string connectionString, Action<string>? onAttemptFailed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _onAttemptFailed = onAttemptFailed ?? (message => Console.Error.WriteLine(message));
    }

    /// <summary>How many times to retry a connection failure before failing fast.</summary>
    public int MaximumAttempts { get; init; } = 30;

    /// <summary>Delay between connection-failure retries.</summary>
    public TimeSpan DelayBetweenAttempts { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ensures the database exists and brings the schema fully up to date, blocking until done.
    /// Throws on a migration failure or once connection retries are exhausted.
    /// </summary>
    public void Run()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                EnsureDatabase.For.PostgresqlDatabase(_connectionString);

                UpgradeEngine upgrader = BuildUpgradeEngine();
                DatabaseUpgradeResult result = upgrader.PerformUpgrade();

                if (!result.Successful)
                {
                    // A script/logic failure is NOT transient — fail fast, no retry.
                    throw new SchemaMigrationException(
                        $"Database migration failed on script '{result.ErrorScript?.Name ?? "(unknown)"}'.",
                        result.Error);
                }

                return;
            }
            catch (Exception exception) when (attempt < MaximumAttempts && IsTransient(exception))
            {
                _onAttemptFailed?.Invoke(
                    $"Database not ready (attempt {attempt}/{MaximumAttempts}): {exception.Message}. " +
                    $"Retrying in {DelayBetweenAttempts.TotalSeconds:0}s.");
                Thread.Sleep(DelayBetweenAttempts);
            }
        }
    }

    /// <summary>
    /// True when the schema is fully applied (no scripts pending). Used by the readiness probe.
    /// Any connection error propagates so the probe reports "not ready".
    /// </summary>
    public bool IsUpToDate() => !BuildUpgradeEngine().IsUpgradeRequired();

    private UpgradeEngine BuildUpgradeEngine()
    {
        Assembly assembly = typeof(SchemaMigrationRunner).Assembly;

        return DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(
                assembly,
                resourceName =>
                    resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                    && resourceName.Contains(".Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        SchemaMigrationException => false, // our own fail-fast wrapper — never retry
        NpgsqlException { IsTransient: true } => true,
        NpgsqlException => true,           // connection-refused during boot presents here
        System.Net.Sockets.SocketException => true,
        TimeoutException => true,
        _ => false,
    };
}

/// <summary>Thrown when a migration script fails — a fatal, non-transient condition.</summary>
public sealed class SchemaMigrationException : Exception
{
    public SchemaMigrationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
