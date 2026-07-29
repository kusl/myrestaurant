using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Npgsql;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One scenario's private stack: its own database, its own data-protection keys, the real web
/// application in its own process on its own loopback port, and one browser context holding one page
/// and one virtual authenticator — plus any additional isolated contexts the scenario asks for.
///
/// <para><b>Why a child process rather than <c>WebApplicationFactory</c>.</b> Playwright drives a real
/// browser over a real socket, and <c>WebApplicationFactory</c>'s in-memory <c>TestServer</c> has no
/// socket to connect to. Beyond that, <c>Program.cs</c> is top-level statements that <c>return 1</c>
/// on invalid configuration, so its generated entry point is not a <c>TEntryPoint</c> a factory can
/// use without opening up the assembly. Booting the built binary is also the more honest test: it
/// exercises the same composition root, the same DbUp migration pass, and the same fail-fast
/// configuration validation a deployment does.</para>
///
/// <para><b>Why one instance per scenario rather than one per class.</b> §16.3's first scenario needs
/// a database with <em>no</em> administrator; its thirteenth needs one with an administrator who has
/// both a passkey and TOTP. Sharing a stack would force an execution order, and xUnit deliberately
/// does not promise one. A fresh database plus a fresh process costs a few seconds and buys
/// scenarios that can be read, and run, in any order.</para>
///
/// <para><b>Why more than one browser context.</b> Several scenarios need two or three principals at the
/// same time in the same restaurant: an administrator, the tablet on the table, a guest with a phone.
/// Cookies are per-context, so each of those is a context — and for the display device it is not merely
/// hygiene. <c>DisplayDeviceAuthenticationMiddleware</c> ignores the device credential whenever the
/// Identity cookie already authenticated the request, so a screen paired inside the administrator's
/// browser resolves as the administrator and never renders a join code at all. See
/// <see cref="OpenIsolatedPageAsync"/>.</para>
///
/// <para><b>The origin.</b> The app is served over <c>http://localhost:{port}</c> and
/// <c>RESTAURANT_PUBLIC_ORIGIN</c> is set to <c>https://localhost:{port}</c> — the scheme mismatch is
/// deliberate and load-bearing in two directions. §13 refuses to start on a non-https public origin,
/// so the configured value must say https; and Chromium treats <c>localhost</c> as a secure context
/// regardless of scheme, so WebAuthn ceremonies run and <c>Secure</c> cookies (the §3.1 authentication
/// cookie is <c>CookieSecurePolicy.Always</c>, and so is the §4.2 display credential) are accepted over
/// plain HTTP. The host matches, which is all
/// <see cref="MyRestaurant.WebApplication.Identity.WebAuthnOriginPolicy"/> and the §3.3
/// relying-party derivation actually compare.</para>
/// </summary>
internal sealed class RestaurantInstance : IAsyncDisposable
{
    /// <summary>
    /// A one-hour rotation window by default. §13's floor is ten seconds and the app's own default is
    /// sixty, but a scenario that computes "the token from the previous window" and then asks a server
    /// to validate it is racing the boundary at any short setting. An hour makes that race impossible
    /// without changing anything the assertions depend on: §4.3 accepts the current and previous
    /// window whatever their width.
    /// </summary>
    internal const int DefaultTableJoinTokenRotationSeconds = 3600;

    private const string RestaurantName = "End To End Restaurant";
    private const int DiagnosticOutputCharacterLimit = 8000;
    private const int PageTimeoutMilliseconds = 30_000;

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _outputGate;
    private readonly StringBuilder _output;
    private readonly Process _process;
    private readonly string _dataProtectionKeysDirectory;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;

    /// <summary>
    /// Contexts handed out by <see cref="OpenIsolatedPageAsync"/>, closed in reverse on disposal. A
    /// plain list: one instance belongs to one scenario, and xUnit runs a single test on a single flow
    /// of control, so there is no second thread to guard against.
    /// </summary>
    private readonly List<IBrowserContext> _isolatedContexts = [];

    private RestaurantInstance(
        Process process,
        StringBuilder output,
        Lock outputGate,
        string dataProtectionKeysDirectory,
        string connectionString,
        string baseUrl,
        string publicOrigin,
        int tableJoinTokenRotationSeconds,
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
        Page = page;
        Authenticator = authenticator;
    }

    /// <summary>The Npgsql connection string for this instance's own database.</summary>
    internal string ConnectionString { get; }

    /// <summary>The origin the browser talks to, e.g. <c>http://localhost:41235</c>.</summary>
    internal string BaseUrl { get; }

    /// <summary>
    /// The <c>RESTAURANT_PUBLIC_ORIGIN</c> this instance was configured with, e.g.
    /// <c>https://localhost:41235</c> — the origin the application embeds in every join URL, and
    /// therefore the origin a scenario must use when computing the QR it expects to see (§4.3).
    /// </summary>
    internal string PublicOrigin { get; }

    /// <summary>The <c>TABLE_JOIN_TOKEN_ROTATION_SECONDS</c> this instance was configured with (§13).</summary>
    internal int TableJoinTokenRotationSeconds { get; }

    /// <summary>The one page in this instance's context; relative URLs resolve against <see cref="BaseUrl"/>.</summary>
    internal IPage Page { get; }

    /// <summary>The CDP virtual authenticator attached to <see cref="Page"/> (§3.3 ceremonies).</summary>
    internal VirtualAuthenticator Authenticator { get; }

    /// <summary>
    /// The tail of the web application's console output. This is where a scenario failure is actually
    /// explained: the browser only ever sees a 500, while the server has the stack.
    /// </summary>
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
            tableJoinTokenRotationSeconds);

        process.OutputDataReceived += (_, arguments) => Append(output, outputGate, arguments.Data);
        process.ErrorDataReceived += (_, arguments) => Append(output, outputGate, arguments.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        IBrowserContext? context = null;

        try
        {
            await WaitForReadinessAsync(process, baseUrl, output, outputGate, cancellationToken);

            context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = baseUrl });
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

    /// <summary>
    /// A second (or third) browser in the same restaurant: its own cookie jar, its own page, the same
    /// origin. This is how a scenario holds an administrator, a display device and a guest at once.
    ///
    /// <para>No virtual authenticator is attached. The ones that need one are the account journeys, and
    /// they run on <see cref="Page"/>; a display device has no credentials of its own beyond the §4.2
    /// cookie, and a guest who registers a passkey will want an authenticator on a context of their own,
    /// which is a later scenario's business rather than a default worth paying for here.</para>
    /// </summary>
    internal async Task<IPage> OpenIsolatedPageAsync()
    {
        IBrowserContext context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { BaseURL = BaseUrl });

        _isolatedContexts.Add(context);

        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(PageTimeoutMilliseconds);

        return page;
    }

    /// <summary>
    /// Arranges an active table with a caller-chosen join secret (§4.1), by direct insert rather than
    /// through the administration surface. Deliberate: §16.3 scenario 14 is about the token algorithm's
    /// window arithmetic, and routing it through a sign-in, a role check and a form would make a
    /// token-window assertion fail for six unrelated reasons. Scenarios 2 and 15, which <em>are</em> about
    /// the administration flow, create their tables through the UI instead
    /// (<see cref="AdministrationJourneys.CreateTableAsync"/>).
    /// </summary>
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

    /// <summary>
    /// Reads a table's current <c>join_secret</c> straight out of the row.
    ///
    /// <para>This is the one place the harness deliberately reaches past every surface, and the reason is
    /// the property under test rather than convenience. §4.1 says the join secret never leaves the server:
    /// no page renders it, <c>ITableDirectory</c> refuses to select it, and rotation replaces it without
    /// showing anyone either value. A scenario that must verify what the display is showing therefore has
    /// exactly two options — decode the QR on screen, or know the secret — and the second is the one that
    /// does not require a computer-vision dependency to answer a question about HMAC arithmetic.</para>
    ///
    /// <para>Unlike <c>ITableJoinSecretReader</c> this is <em>not</em> gated on <c>is_active</c>: a
    /// scenario about a deactivated table still needs to know what it would have signed with.</para>
    /// </summary>
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

    public async ValueTask DisposeAsync()
    {
        // Reverse order, so a context is never left open behind one that failed to close.
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

    // --- process plumbing ------------------------------------------------------------------------

    private static Process CreateProcess(
        WebApplicationLaunch launch,
        int port,
        string publicOrigin,
        string connectionString,
        string dataProtectionKeysDirectory,
        int tableJoinTokenRotationSeconds)
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

        // Kestrel binds both loopback families for the literal host `localhost`, so the browser
        // reaches it whether the name resolves to 127.0.0.1 or ::1.
        variables["ASPNETCORE_URLS"] = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}");
        variables["ASPNETCORE_ENVIRONMENT"] = "Production";
        variables["DOTNET_ENVIRONMENT"] = "Production";
        variables["ASPNETCORE_CONTENTROOT"] = launch.ContentRoot;

        // §13, in full. Nothing is left to the app's own defaults: a scenario that failed because a
        // default moved would be a scenario nobody could debug.
        variables["RESTAURANT_NAME"] = RestaurantName;
        variables["RESTAURANT_PUBLIC_ORIGIN"] = publicOrigin;
        variables["RESTAURANT_TRUSTED_ORIGIN_PATTERNS"] = "https://*.trycloudflare.com";
        variables["RESTAURANT_TIME_ZONE"] = "America/New_York";
        variables["RESTAURANT_CLOCK_FORMAT"] = "12-hour";
        variables["RESTAURANT_CURRENCY_CODE"] = "USD";
        variables["RESTAURANT_DATABASE_CONNECTION_STRING"] = connectionString;
        variables["DATA_PROTECTION_KEYS_DIRECTORY"] = dataProtectionKeysDirectory;
        variables["KITCHEN_SUBMISSION_REMINDER_SECONDS"] = "60";
        variables["TABLE_JOIN_TOKEN_ROTATION_SECONDS"] =
            tableJoinTokenRotationSeconds.ToString(CultureInfo.InvariantCulture);
        variables["TABLE_JOIN_GRANT_MINUTES"] = "10";
        variables["TABLE_DISPLAY_PAIRING_CODE_MINUTES"] = "10";

        // Exactly the §3.2 floor. Argon2id at production parameters would spend most of each
        // scenario's runtime hashing one password, and the floor is a value the guard accepts, so the
        // sign-in path under test is the real one.
        variables["ARGON2_MEMORY_KIBIBYTES"] = "19456";
        variables["ARGON2_ITERATIONS"] = "2";
        variables["ARGON2_PARALLELISM"] = "1";
        variables["ARGON2_MAX_CONCURRENT_HASHES"] = "4";

        // No collector is listening, and Program.cs only attaches the OTLP exporters when one is
        // configured. Inheriting a developer's endpoint would fill the log with connection refusals.
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
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The probe itself timed out; try again until the deadline.
            }

            await Task.Delay(ReadinessPollInterval, cancellationToken);
        }

        await StopProcessAsync(process);

        throw new InvalidOperationException(
            $"/healthz/ready did not answer 200 within {ReadinessTimeout.TotalSeconds:F0}s."
            + $"\n--- web application output ---\n{Snapshot(output, outputGate)}");
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
            // Already gone.
        }
        catch (OperationCanceledException)
        {
            // It did not die politely within the timeout; the test run is ending regardless.
        }
    }

    // --- database plumbing -----------------------------------------------------------------------

    /// <summary>
    /// Creates this instance's database. The name is interpolated rather than parameterized because
    /// <c>CREATE DATABASE</c> takes no parameters in PostgreSQL; it is assembled here from an
    /// interlocked counter and a timestamp, so no caller-supplied text ever reaches it.
    /// The schema itself is applied by the application's own DbUp pass at startup (§17), which is one
    /// more thing these scenarios therefore prove.
    /// </summary>
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

    // --- odds and ends ---------------------------------------------------------------------------

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
            // A leftover temp directory is not worth failing a scenario over.
        }
        catch (UnauthorizedAccessException)
        {
            // Likewise.
        }
    }
}
