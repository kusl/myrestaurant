using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Npgsql;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// A table's currently open sitting (§5.1) and the usernames on its roster, in join order.
/// </summary>
internal sealed record OpenSitting(Guid SittingIdentifier, IReadOnlyList<string> MemberUsernames);

/// <summary>
/// The three columns §5.3 stamps together when a sitting is closed, and the username behind the third.
///
/// <para><b>Why a scenario reads these rows.</b> §16.3 scenario 10's claim is "totals match", and the
/// two screens it can read both compute their figure at render time — the till's header from the
/// <c>sitting_bill</c> view, the guest's from a C# sum over the same view. Neither can distinguish "the
/// stamped total is correct" from "both screens are summing the same live data and nothing was stamped
/// at all", and §5.3's whole promise is about the stamped value: it is computed under the
/// <c>FOR UPDATE</c> and is <em>never rewritten</em> afterwards. Only the column says so.</para>
///
/// <para>The three arrive together because the schema will not have them any other way — <c>table_sitting</c>
/// carries <c>CHECK ((closed_at IS NULL) = (closed_by_person_identifier IS NULL))</c> and the same paired
/// check on the total — so a reader that returned one of them would be describing a state the database
/// cannot hold.</para>
///
/// <para>A username rather than a person identifier, for the reason <see cref="OpenSitting"/> carries
/// usernames: it is what a scenario knows about the account it created, and "the counter closed it"
/// is a claim about <em>which</em> person pressed the button.</para>
/// </summary>
internal sealed record SettledSitting(
    Guid SittingIdentifier,
    decimal SettledTotalAmount,
    DateTimeOffset ClosedAt,
    string ClosedByUsername);

/// <summary>
/// How many <c>kitchen_notification</c> rows of each §10 kind exist for one sitting: the
/// <c>initial</c> ones written inside a send's own transaction (§10.1) and the <c>reminder</c> ones
/// the background scan wrote afterwards (§8.4, §10.2).
///
/// <para><b>Why a scenario reads rows at all here.</b> §16.3 scenario 8's whole claim is the word
/// "exactly", and the board cannot carry it: its unseen count is circuit memory that a cook clears
/// with one tap, so "the badge never went up again" is consistent with a second reminder having been
/// written and broadcast to nobody. The <c>UNIQUE (order_event_identifier, kind)</c> constraint is
/// what actually makes a reminder singular, and this is the only way to see it hold.</para>
/// </summary>
internal sealed record KitchenNotificationTally(int Initial, int Reminder);

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
///
/// <para><b>This block sat at the top of the file until F-114</b>, as a second
/// <c>&lt;summary&gt;</c> element stacked above <see cref="OpenSitting"/>'s — so a reader
/// hovering that four-line record was handed an essay about child processes and WebAuthn
/// origins, and this class carried no summary at all. C# has no file-level documentation
/// comment; a <c>///</c> block binds to the next declaration whatever it was written about.</para>
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

    /// <summary>
    /// <c>KITCHEN_SUBMISSION_REMINDER_SECONDS</c> unless a scenario asks for something else — the
    /// application's own default, and the number §16.3 scenario 8 is literally written in terms of.
    ///
    /// <para>Every scenario but that one wants it left alone rather than merely left long. §8.4's scan
    /// is the only thing in the system that writes because <em>nobody</em> acted, and a scenario that
    /// sends and then spends thirty seconds asserting on something else would, at a short setting,
    /// acquire a reminder alert it never asked for and never mentions. At sixty it cannot: no scenario
    /// here holds a send untouched for a minute except the one that means to.</para>
    /// </summary>
    internal const int DefaultKitchenSubmissionReminderSeconds = 60;

    /// <summary>
    /// <c>RESTAURANT_CURRENCY_CODE</c> for every instance (§13), named rather than left as a literal
    /// inside <see cref="CreateProcess"/> so that a scenario asserting on money can format its
    /// expectation through <c>MoneyText.Format</c> with the same code the application was handed.
    ///
    /// <para>§16.3 scenario 9's whole subject is two amounts on a screen, and there are exactly two ways
    /// to write that assertion: hard-code <c>"$11.00"</c>, which is a claim about this constant that
    /// silently becomes a claim about nothing the day it changes; or compute it, which is a claim about
    /// the adjustment. Reading it back is the same discipline
    /// <see cref="TableJoinTokenRotationSeconds"/> and <see cref="KitchenSubmissionReminderSeconds"/>
    /// already follow.</para>
    /// </summary>
    internal const string CurrencyCode = "USD";

    /// <summary>
    /// The width §11.12 is written for, in CSS pixels, and the project's primary handset target: an
    /// iPhone SE (2020) in portrait.
    ///
    /// <para><b>Why 375 and not something smaller.</b> The rule in §11.12 is a direction rather than a
    /// number — handheld first, widened by one query — so any narrow viewport would exercise it. 375 is
    /// the one that makes a failure mean something to a person: it is the device F-59 was reported from,
    /// it is the narrowest screen in common use rather than the narrowest conceivable one, and a barrier
    /// at 320 would be asserting a property this project has never claimed.</para>
    /// </summary>
    internal const int HandheldViewportWidth = 375;

    /// <summary>
    /// The same handset's height. It matters less than the width — nothing here asserts on vertical
    /// position — but a viewport 375 wide and 720 tall is not a phone, and a scenario that scrolls looks
    /// different from one that does not.
    /// </summary>
    internal const int HandheldViewportHeight = 667;

    private const string RestaurantName = "End To End Restaurant";
    private const int DiagnosticOutputCharacterLimit = 8000;
    private const int PageTimeoutMilliseconds = 30_000;

    /// <summary>
    /// The script <c>App.razor</c> loads to start a circuit. Probed at startup — see
    /// <see cref="VerifyInteractivityAsync"/> — because without it every interactive surface silently
    /// degrades to prerendered HTML, which no scenario would report as anything but a timeout.
    /// </summary>
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

    /// <summary>
    /// Contexts handed out by <see cref="OpenIsolatedPageAsync"/>, closed in reverse on disposal. A
    /// plain list: one instance belongs to one scenario, and xUnit runs a single test on a single flow
    /// of control, so there is no second thread to guard against.
    /// </summary>
    private readonly List<IBrowserContext> _isolatedContexts = [];

    /// <summary>
    /// Virtual authenticators attached to those contexts on request, detached in reverse before the
    /// contexts they belong to are closed. Kept apart from <see cref="Authenticator"/> because that one
    /// belongs to <see cref="Page"/> and is disposed with it.
    /// </summary>
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

    /// <summary>
    /// The <c>KITCHEN_SUBMISSION_REMINDER_SECONDS</c> this instance was configured with (§13). Read
    /// back rather than restated so a scenario's patience is computed from what the application was
    /// actually given, the way every window computation is computed from
    /// <see cref="TableJoinTokenRotationSeconds"/>.
    /// </summary>
    internal int KitchenSubmissionReminderSeconds { get; }

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

    /// <summary>
    /// A second (or third) browser in the same restaurant: its own cookie jar, its own page, the same
    /// origin. This is how a scenario holds an administrator, a display device and a guest at once.
    ///
    /// <para><b>No virtual authenticator unless one is asked for.</b> A display device has no
    /// credentials of its own beyond the §4.2 cookie, so paying CDP setup for every tablet would be
    /// waste. A guest who registers a passkey (§4.3) is the case that needs one, and it must be on
    /// <em>their</em> context rather than the administrator's: a WebAuthn credential is scoped to the
    /// authenticator that minted it, so a guest passkey created against <see cref="Authenticator"/>
    /// would be offered back to whoever is signing in on <see cref="Page"/> and to nobody else. Pass
    /// <paramref name="withVirtualAuthenticator"/> to get one.</para>
    /// </summary>
    /// <param name="withVirtualAuthenticator">Attach a CDP virtual authenticator to the new context.</param>
    /// <param name="handheld">
    /// Lay this context out at <see cref="HandheldViewportWidth"/>×<see cref="HandheldViewportHeight"/>
    /// (§11.12). A viewport is a property of the context, so this affects nothing else — which is worth
    /// stating because the opposite was believed for a slice, and F-62 is that belief.
    /// </param>
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

    /// <summary>
    /// The options every context in this instance is built from: the base URL, and a viewport only when
    /// one was asked for.
    ///
    /// <para><b>Why <c>null</c> rather than a default size in the wide case.</b> Playwright's own default
    /// is 1280×720 and leaving <see cref="BrowserNewContextOptions.ViewportSize"/> unset is what selects
    /// it. Writing 1280×720 here would be the same number in a second place — the mechanism behind F-48,
    /// F-50 and F-56 — and would silently pin fifteen scenarios to a figure this project never chose.
    /// </para>
    /// </summary>
    private static BrowserNewContextOptions ContextOptions(string baseUrl, bool handheld) => new()
    {
        BaseURL = baseUrl,
        ViewportSize = handheld
            ? new ViewportSize { Width = HandheldViewportWidth, Height = HandheldViewportHeight }
            : null,
    };

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

    /// <summary>
    /// The open sitting on a table (§5.1) and who is on it, or <c>null</c> when the table has none.
    ///
    /// <para>§16.3 scenario 3 ends on the words "sitting created", and that is a claim about rows: a
    /// sitting was opened on <em>this</em> table, it is still open, and the person who scanned is a
    /// member of it. The surface can show that a join happened — it says so, and the roster names the
    /// guest — but it cannot distinguish "joined the sitting" from "joined a second sitting the unique
    /// index should have prevented", which is the interesting failure. So this reads the two rows and
    /// counts them, and the scenario asserts on both the page and the database.</para>
    ///
    /// <para>Usernames rather than identifiers, because that is what a scenario knows about the guest
    /// it registered.</para>
    /// </summary>
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

    /// <summary>
    /// What §5.3 stamped on a closed sitting, or <c>null</c> when that sitting is still open.
    ///
    /// <para>Scoped by the sitting rather than by the table, unlike <see cref="ReadOpenSittingAsync"/>,
    /// and the difference is not stylistic. A table has at most one <em>open</em> sitting — the partial
    /// unique index says so — but it may have any number of closed ones, and the very next guest to scan
    /// opens another. "The settled sitting on this table" is therefore not a question with one answer,
    /// while "this sitting, the one the counter opened the bill for" is. A scenario has that identifier:
    /// <c>CounterJourneys.OpenSittingAsync</c> returns it off the URL it followed.</para>
    ///
    /// <para>An open sitting returns <c>null</c> rather than throwing, because "not closed" is a real and
    /// interesting answer — it is precisely what a scenario asserting that a close did <em>not</em>
    /// happen would want, and a caller that meant to close first can say so in its own words.</para>
    /// </summary>
    internal async Task<SettledSitting?> ReadSettledSittingAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // INNER JOIN on the closer rather than LEFT: the schema's paired CHECKs make a closed row
        // without an actor impossible, so a row that failed to join would mean the constraint had been
        // dropped — and silently reporting that as "still open" would be the worst available answer.
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

        // Npgsql materialises `timestamptz` as a UTC DateTime, so the kind is restated rather than
        // assumed — the same conversion every reader in the data-access layer carries.
        decimal settledTotalAmount = reader.GetDecimal(0);
        DateTime closedAt = reader.GetDateTime(1);
        string closedByUsername = reader.GetString(2);

        return new SettledSitting(
            sittingIdentifier,
            settledTotalAmount,
            new DateTimeOffset(DateTime.SpecifyKind(closedAt, DateTimeKind.Utc)),
            closedByUsername);
    }

    /// <summary>
    /// Counts the <c>kitchen_notification</c> rows for a sitting, by kind (§10.1's <c>initial</c>,
    /// §10.2's <c>reminder</c>).
    ///
    /// <para>The second place this harness reaches past every surface, and for a reason of the same
    /// shape as <see cref="ReadJoinSecretAsync"/>'s: the fact being asserted is one no screen renders.
    /// A reminder's singularity is enforced by <c>UNIQUE (order_event_identifier, kind)</c> and
    /// observed by §8.4's <c>RETURNING</c>, and the board's badge is a count in circuit memory that a
    /// cook clears with one tap — so a board that stayed quiet is consistent with a row that was
    /// written twice, and only the rows tell those apart.</para>
    ///
    /// <para>Scoped by sitting rather than by order event, because a scenario knows which table it sat
    /// at and does not know what identifier the send it pressed was given. The join is the §8.2 chain
    /// the reminder scan itself walks: notification → event → order → sitting.</para>
    /// </summary>
    internal async Task<KitchenNotificationTally> ReadKitchenNotificationsAsync(
        Guid sittingIdentifier,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // `notified_event` rather than `event`: the latter is a PostgreSQL keyword and an alias that
        // needs quoting to be legal is an alias waiting to be edited into a syntax error.
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

                // COUNT(*) is bigint. Narrowed here rather than carried as a long: a sitting with more
                // than int.MaxValue kitchen notifications is not a case worth a wider type.
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
                        // The column has a CHECK constraint listing exactly those two, so this is
                        // unreachable until a migration adds a third — at which point a scenario
                        // counting the old two silently would be worse than one that says so.
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
        // Authenticators first: each one holds a CDP session on a context that is about to close.
        for (int index = _isolatedAuthenticators.Count - 1; index >= 0; index--)
        {
            await _isolatedAuthenticators[index].DisposeAsync();
        }

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
        variables["RESTAURANT_CURRENCY_CODE"] = CurrencyCode;
        variables["RESTAURANT_DATABASE_CONNECTION_STRING"] = connectionString;
        variables["DATA_PROTECTION_KEYS_DIRECTORY"] = dataProtectionKeysDirectory;
        variables["KITCHEN_SUBMISSION_REMINDER_SECONDS"] =
            kitchenSubmissionReminderSeconds.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>
    /// Refuses to hand back an instance whose pages cannot become interactive.
    ///
    /// <para><b>Why a probe and not a scenario's problem.</b> <c>/healthz/ready</c> proves the process
    /// answers, the configuration binds and the schema is current — and says nothing about whether a
    /// browser can start a Blazor circuit. If <c>_framework/blazor.web.js</c> is missing, every page still
    /// renders: prerendering produces the whole document server-side, so a display shows a table label and
    /// a perfectly good QR code, a kitchen board shows its columns, and nothing anywhere reports an error.
    /// They simply never change again. Scenario 2 experienced that as "the QR did not advance within 60s"
    /// and scenario 15 as "no code signed by the rotated secret" — two mysteries with one cause and no
    /// mention of it in either message.</para>
    ///
    /// <para><b>Why it can be missing at all.</b> The framework's own JavaScript is a static <em>web
    /// asset</em>. <c>dotnet publish</c> copies those into <c>wwwroot/</c>; a plain <c>dotnet build</c>
    /// leaves them in the NuGet cache and describes them in a build-time manifest which
    /// <c>WebHost.ConfigureWebDefaults</c> loads only when the environment is Development. This harness
    /// boots the <em>build</em> output with <c>ASPNETCORE_ENVIRONMENT=Production</c>, which is exactly the
    /// combination that has neither. <c>Program.cs</c> now asks for that manifest itself outside
    /// Development, so this probe should pass; it stays because the cost is one request per instance and
    /// the failure it catches is invisible by nature.</para>
    /// </summary>
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
