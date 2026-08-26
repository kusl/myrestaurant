using System.Reflection;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace MyRestaurant.DataAccess;

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

    public int MaximumAttempts { get; init; } = 30;

    public TimeSpan DelayBetweenAttempts { get; init; } = TimeSpan.FromSeconds(2);

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

            .WithVariablesDisabled()
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        SchemaMigrationException => false,
        NpgsqlException { IsTransient: true } => true,
        NpgsqlException => true,
        System.Net.Sockets.SocketException => true,
        TimeoutException => true,
        _ => false,
    };
}

public sealed class SchemaMigrationException : Exception
{
    public SchemaMigrationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
