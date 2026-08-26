using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Npgsql;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record OpenSitting(Guid SittingIdentifier, IReadOnlyList<string> MemberUsernames);

internal sealed record SettledSitting(
    Guid SittingIdentifier,
    decimal SettledTotalAmount,
    DateTimeOffset ClosedAt,
    string ClosedByUsername);

internal sealed record KitchenNotificationTally(int Initial, int Reminder);

internal sealed class RestaurantInstance : IAsyncDisposable
{
    internal const int DefaultTableJoinTokenRotationSeconds = 3600;

    internal const int DefaultKitchenSubmissionReminderSeconds = 60;

    internal const string CurrencyCode = "USD";

    internal const int HandheldViewportWidth = 375;

    internal const int HandheldViewportHeight = 667;

    private const string RestaurantName = "End To End Restaurant";
    private const int DiagnosticOutputCharacterLimit = 8000;
    private const int PageTimeoutMilliseconds = 30_000;

    private const string BlazorScriptPath = "/_framework/blazor.web.js";

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _outputGate;
    private readonly StringBuilder _output;
    private readonly Process _process;
    private readonly string _dataProtectionKeysDirectory;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;

    private readonly List<IBrowserContext> _isolatedContexts = [];

    private readonly List<VirtualAuthenticator> _isolatedAuthenticators = [];

    private RestaurantInstance(
        Process process,
        StringBuilder output,
        Lock outputGate,
        string dataProtectionKeysDirectory,
        string connectionString,
        string baseUrl,
        string publicOrigin,
        int tableJoinTokenRotationSeconds,
        int kitchenSubmissionReminderSeconds,
        IBrowser browser,
        IBrowserContext context,
        IPage page,
        VirtualAuthenticator authenticator)
    {
        _process = process;
        _output = output;
        _outputGate = outputGate;
        _dataProtectionKeysDirectory = dataProtectionKeysDirectory;
        _browser = browser;
        _context = context;

        ConnectionString = connectionString;
        BaseUrl = baseUrl;
        PublicOrigin = publicOrigin;
        TableJoinTokenRotationSeconds = tableJoinTokenRotationSeconds;
        KitchenSubmissionReminderSeconds = kitchenSubmissionReminderSeconds;
        Page = page;
        Authenticator = authenticator;
    }

    internal string ConnectionString { get; }

    internal string BaseUrl { get; }

    internal string PublicOrigin { get; }

    internal int TableJoinTokenRotationSeconds { get; }

    internal int KitchenSubmissionReminderSeconds { get; }

    internal IPage Page { get; }

    internal VirtualAuthenticator Authenticator { get; }

    internal string DiagnosticOutput
    {
        get
        {
            lock (_outputGate)
            {
                return Tail(_output.ToString());
            }
        }
    }

    internal static async Task<RestaurantInstance> StartAsync(
        IBrowser browser,
        string administrativeConnectionString,
        WebApplicationLaunch launch,
        int ordinal,
        int tableJoinTokenRotationSeconds,
        int kitchenSubmissionReminderSeconds,
        bool handheld,
        CancellationToken cancellationToken)
    {
        string databaseName = string.Create(
            CultureInfo.InvariantCulture,
            $"myrestaurant_e2e_{ordinal}_{DateTime.UtcNow:HHmmssfff}");

        string connectionString = await CreateDatabaseAsync(
            administrativeConnectionString, databaseName, cancellationToken);

        string dataProtectionKeysDirectory = Path.Combine(
            Path.GetTempPath(), "myrestaurant-end-to-end", databaseName);
        Directory.CreateDirectory(dataProtectionKeysDirectory);

        int port = ReserveLoopbackPort();
        string baseUrl = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}");
        string publicOrigin = string.Create(CultureInfo.InvariantCulture, $"https://localhost:{port}");

        StringBuilder output = new();
        Lock outputGate = new();
        Process process = CreateProcess(
            launch,
            port,
            publicOrigin,
            connectionString,
            dataProtectionKeysDirectory,
            tableJoinTokenRotationSeconds,
            kitchenSubmissionReminderSeconds);

        process.OutputDataReceived += (_, arguments) => Append(output, outputGate, arguments.Data);
        process.ErrorDataReceived += (_, arguments) => Append(output, outputGate, arguments.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        IBrowserContext? context = null;

        try
        {
            await WaitForReadinessAsync(process, baseUrl, output, outputGate, cancellationToken);
            await VerifyInteractivityAsync(baseUrl, cancellationToken);

            context = await browser.NewContextAsync(ContextOptions(baseUrl, handheld));
            IPage page = await context.NewPageAsync();
            page.SetDefaultTimeout(PageTimeoutMilliseconds);

            VirtualAuthenticator authenticator = await VirtualAuthenticator.AttachAsync(context, page);

            return new RestaurantInstance(
                process,
                output,
                outputGate,
                dataProtectionKeysDirectory,
                connectionString,
                baseUrl,
                publicOrigin,
                tableJoinTokenRotationSeconds,
                kitchenSubmissionReminderSeconds,
                browser,
                context,
                page,
                authenticator);
        }
        catch
        {
            if (context is not null)
            {
                await context.CloseAsync();
            }

            await StopProcessAsync(process);
            DeleteDirectoryQuietly(dataProtectionKeysDirectory);
            throw;
        }
    }

    internal async Task<IPage> OpenIsolatedPageAsync(
        bool withVirtualAuthenticator = false,
        bool handheld = false)
    {
        IBrowserContext context = await _browser.NewContextAsync(ContextOptions(BaseUrl, handheld));

        _isolatedContexts.Add(context);

        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(PageTimeoutMilliseconds);

        if (withVirtualAuthenticator)
        {
            _isolatedAuthenticators.Add(await VirtualAuthenticator.AttachAsync(context, page));
        }

        return page;
    }

    private static BrowserNewContextOptions ContextOptions(string baseUrl, bool handheld) => new()
    {
        BaseURL = baseUrl,
        ViewportSize = handheld
            ? new ViewportSize { Width = HandheldViewportWidth, Height = HandheldViewportHeight }
            : null,
    };

    internal async Task<Guid> InsertActiveTableAsync(
        string label,
        byte[] joinSecret,
        CancellationToken cancellationToken)
    {
        Guid tableIdentifier = Guid.CreateVersion7();

        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = new(
            """
            INSERT INTO restaurant_table
                (restaurant_table_identifier, label, join_secret, is_active, created_at)
            VALUES
                (@table_identifier, @label, @join_secret, true, @created_at);
            """,
            connection);

        command.Parameters.AddWithValue("table_identifier", tableIdentifier);
        command.Parameters.AddWithValue("label", label);
        command.Parameters.AddWithValue("join_secret", joinSecret);
        command.Parameters.AddWithValue("created_at", DateTime.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return tableIdentifier;
    }

    internal async Task<byte[]> ReadJoinSecretAsync(Guid tableIdentifier, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = new(
            """
            SELECT join_secret
            FROM restaurant_table
            WHERE restaurant_table_identifier = @table_identifier;
            """,
            connection);

        command.Parameters.AddWithValue("table_identifier", tableIdentifier);

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        if (value is not byte[] joinSecret)
        {
            throw new InvalidOperationException(
                $"No restaurant_table row exists for {tableIdentifier:D}, so it has no join secret.");
        }

        return joinSecret;
    }

    internal async Task<OpenSitting?> ReadOpenSittingAsync(Guid tableIdentifier, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        Guid sittingIdentifier;

        await using (NpgsqlCommand sitting = new(
            """
            SELECT table_sitting_identifier
            FROM table_sitting
            WHERE restaurant_table_identifier = @table_identifier AND closed_at IS NULL;
            """,
            connection))
        {
            sitting.Parameters.AddWithValue("table_identifier", tableIdentifier);

            object? value = await sitting.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid found)
            {
                return null;
            }

            sittingIdentifier = found;
        }

        List<string> members = [];

        await using (NpgsqlCommand roster = new(
            """
            SELECT p.username
            FROM table_sitting_member m
            JOIN person p ON p.person_identifier = m.person_identifier
            WHERE m.table_sitting_identifier = @sitting_identifier
            ORDER BY m.joined_at;
            """,
            connection))
        {
            roster.Parameters.AddWithValue("sitting_identifier", sittingIdentifier);

            await using NpgsqlDataReader reader = await roster.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                members.Add(reader.GetString(0));
            }
        }

        return new OpenSitting(sittingIdentifier, members);
    }

    internal async Task<SettledSitting?> ReadSettledSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = new(
            """
            SELECT sitting.settled_total_amount,
                   sitting.closed_at,
                   closer.username
            FROM table_sitting AS sitting
            INNER JOIN person AS closer
                    ON closer.person_identifier = sitting.closed_by_person_identifier
            WHERE sitting.table_sitting_identifier = @sitting_identifier
              AND sitting.closed_at IS NOT NULL;
            """,
            connection);

        command.Parameters.AddWithValue("sitting_identifier", sittingIdentifier);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        decimal settledTotalAmount = reader.GetDecimal(0);
        DateTime closedAt = reader.GetDateTime(1);
        string closedByUsername = reader.GetString(2);

        return new SettledSitting(
            sittingIdentifier,
            settledTotalAmount,
            new DateTimeOffset(DateTime.SpecifyKind(closedAt, DateTimeKind.Utc)),
            closedByUsername);
    }

    internal async Task<KitchenNotificationTally> ReadKitchenNotificationsAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = new(
            """
            SELECT notification.kind AS kind, COUNT(*) AS tally
            FROM kitchen_notification AS notification
            INNER JOIN order_event AS notified_event
                    ON notified_event.order_event_identifier = notification.order_event_identifier
            INNER JOIN guest_order
                    ON guest_order.guest_order_identifier = notified_event.guest_order_identifier
            WHERE guest_order.table_sitting_identifier = @sitting_identifier
            GROUP BY notification.kind;
            """,
            connection);

        command.Parameters.AddWithValue("sitting_identifier", sittingIdentifier);

        int initial = 0;
        int reminder = 0;

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string kind = reader.GetString(0);

                int tally = (int)reader.GetInt64(1);

                switch (kind)
                {
                    case "initial":
                        initial = tally;
                        break;

                    case "reminder":
                        reminder = tally;
                        break;

                    default:

                        throw new InvalidOperationException(
                            $"kitchen_notification.kind held '{kind}', which is neither 'initial' nor"
                            + " 'reminder'. §8.2's CHECK constraint allowed exactly those two when this"
                            + " was written; a migration has widened it and this read needs updating.");
                }
            }
        }

        return new KitchenNotificationTally(initial, reminder);
    }

    public async ValueTask DisposeAsync()
    {
        for (int index = _isolatedAuthenticators.Count - 1; index >= 0; index--)
        {
            await _isolatedAuthenticators[index].DisposeAsync();
        }

        for (int index = _isolatedContexts.Count - 1; index >= 0; index--)
        {
            await _isolatedContexts[index].CloseAsync();
        }

        await Authenticator.DisposeAsync();
        await _context.CloseAsync();
        await StopProcessAsync(_process);
        _process.Dispose();
        DeleteDirectoryQuietly(_dataProtectionKeysDirectory);
    }

    private static Process CreateProcess(
        WebApplicationLaunch launch,
        int port,
        string publicOrigin,
        string connectionString,
        string dataProtectionKeysDirectory,
        int tableJoinTokenRotationSeconds,
        int kitchenSubmissionReminderSeconds)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = launch.FileName,
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        IDictionary<string, string?> variables = startInfo.Environment;

        variables["ASPNETCORE_URLS"] = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}");
        variables["ASPNETCORE_ENVIRONMENT"] = "Production";
        variables["DOTNET_ENVIRONMENT"] = "Production";
        variables["ASPNETCORE_CONTENTROOT"] = launch.ContentRoot;

        variables["RESTAURANT_NAME"] = RestaurantName;
        variables["RESTAURANT_PUBLIC_ORIGIN"] = publicOrigin;
        variables["RESTAURANT_TRUSTED_ORIGIN_PATTERNS"] = "https://*.trycloudflare.com";
        variables["RESTAURANT_TIME_ZONE"] = "America/New_York";
        variables["RESTAURANT_CLOCK_FORMAT"] = "12-hour";
        variables["RESTAURANT_CURRENCY_CODE"] = CurrencyCode;
        variables["RESTAURANT_DATABASE_CONNECTION_STRING"] = connectionString;
        variables["DATA_PROTECTION_KEYS_DIRECTORY"] = dataProtectionKeysDirectory;
        variables["KITCHEN_SUBMISSION_REMINDER_SECONDS"] =
            kitchenSubmissionReminderSeconds.ToString(CultureInfo.InvariantCulture);
        variables["TABLE_JOIN_TOKEN_ROTATION_SECONDS"] =
            tableJoinTokenRotationSeconds.ToString(CultureInfo.InvariantCulture);
        variables["TABLE_JOIN_GRANT_MINUTES"] = "10";
        variables["TABLE_DISPLAY_PAIRING_CODE_MINUTES"] = "10";

        variables["ARGON2_MEMORY_KIBIBYTES"] = "19456";
        variables["ARGON2_ITERATIONS"] = "2";
        variables["ARGON2_PARALLELISM"] = "1";
        variables["ARGON2_MAX_CONCURRENT_HASHES"] = "4";

        variables.Remove("OTEL_EXPORTER_OTLP_ENDPOINT");
        variables.Remove("UPTRACE_DSN");

        return new Process { StartInfo = startInfo };
    }

    private static async Task WaitForReadinessAsync(
        Process process,
        string baseUrl,
        StringBuilder output,
        Lock outputGate,
        CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ReadinessTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The web application exited with code {process.ExitCode} before it became ready."
                    + $"\n--- web application output ---\n{Snapshot(output, outputGate)}");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    baseUrl + "/healthz/ready", cancellationToken);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(ReadinessPollInterval, cancellationToken);
        }

        await StopProcessAsync(process);

        throw new InvalidOperationException(
            $"/healthz/ready did not answer 200 within {ReadinessTimeout.TotalSeconds:F0}s."
            + $"\n--- web application output ---\n{Snapshot(output, outputGate)}");
    }

    private static async Task VerifyInteractivityAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };

        HttpStatusCode status;
        try
        {
            using HttpResponseMessage response = await client.GetAsync(baseUrl + BlazorScriptPath, cancellationToken);
            status = response.StatusCode;
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Could not fetch {BlazorScriptPath} from {baseUrl}, so no page can become interactive.",
                exception);
        }

        if (status == HttpStatusCode.OK)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{baseUrl}{BlazorScriptPath} answered {(int)status} instead of 200, so this instance can"
            + " serve no interactive page: every surface would render once, from prerendering, and then"
            + " never update — a table display frozen on its first QR, a kitchen board that never alerts."
            + " The usual cause is that the framework's static web assets are unreachable: they live in"
            + " wwwroot only after `dotnet publish`, and a build output's manifest is loaded by"
            + " WebHost.ConfigureWebDefaults only in the Development environment. Program.cs loads that"
            + " manifest itself for other environments; if this fires, check that it still does.");
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using CancellationTokenSource shutdown = new(ShutdownTimeout);
                await process.WaitForExitAsync(shutdown.Token);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string> CreateDatabaseAsync(
        string administrativeConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(administrativeConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = new($"CREATE DATABASE \"{databaseName}\";", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        NpgsqlConnectionStringBuilder builder = new(administrativeConnectionString)
        {
            Database = databaseName,
        };

        return builder.ConnectionString;
    }

    private static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Append(StringBuilder output, Lock outputGate, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (outputGate)
        {
            output.AppendLine(line);
        }
    }

    private static string Snapshot(StringBuilder output, Lock outputGate)
    {
        lock (outputGate)
        {
            return Tail(output.ToString());
        }
    }

    private static string Tail(string text)
        => text.Length <= DiagnosticOutputCharacterLimit
            ? text
            : text[^DiagnosticOutputCharacterLimit..];

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
