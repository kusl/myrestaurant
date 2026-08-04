using Microsoft.Playwright;
using Testcontainers.PostgreSql;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// The one expensive thing the §16.3 scenarios share: a PostgreSQL 17 container and a Chromium
/// browser, started once for the whole scenario class. Everything a single scenario must not share
/// with another — its database, its web application process, its browser contexts, its cookies, its
/// virtual authenticator — belongs to <see cref="RestaurantInstance"/> instead.
///
/// <para><b>Opt-in, on purpose.</b> These scenarios skip unless <c>MYRESTAURANT_E2E</c> is set. The
/// first run downloads a Chromium build of roughly 150 MB into <c>~/.cache/ms-playwright</c>, and a
/// plain <c>dotnet test</c> has no business doing that unasked — the whole suite is otherwise
/// offline once packages are restored. <c>scripts/ci_local.sh --with-e2e</c> and CI's
/// <c>end-to-end</c> job set the variable; nothing else does.</para>
///
/// <para><b>Every unavailability is a skip, never a failure.</b> No opt-in, no container engine, no
/// browser, no build output: each sets <see cref="SkipReason"/> and each scenario calls
/// <c>Assert.SkipUnless</c> — the same discipline the data-access integration tests follow. A missing
/// tool is not a broken product, and a suite that cannot tell the difference is a suite people stop
/// reading.</para>
/// </summary>
public sealed class RestaurantHarness : IAsyncLifetime
{
    /// <summary>The environment variable that opts in to the end-to-end scenarios.</summary>
    public const string OptInVariableName = "MYRESTAURANT_E2E";

    private const string PostgreSqlImage = "postgres:17-alpine";

    /// <summary>
    /// Installs only Chromium. The scenarios are single-browser by design: §16.3 is about this
    /// product's flows, and the WebAuthn virtual authenticator is a Chrome DevTools Protocol feature
    /// with no Firefox or WebKit equivalent, so a cross-browser matrix would be a matrix of one.
    /// </summary>
    private static readonly string[] BrowserInstallArguments = ["install", "chromium"];

    private int _instanceCounter;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private PostgreSqlContainer? _container;
    private string? _administrativeConnectionString;
    private WebApplicationLaunch? _launch;

    /// <summary>Non-null when the scenarios cannot run; the reason to pass to <c>Assert.SkipUnless</c>.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!IsOptedIn())
        {
            SkipReason =
                $"The §16.3 end-to-end scenarios are opt-in: set {OptInVariableName}=1 to run them."
                + " The first run downloads Chromium into ~/.cache/ms-playwright, and each scenario"
                + " starts PostgreSQL in a container and boots the built web application."
                + " `scripts/ci_local.sh --with-e2e` sets it for you.";
            return;
        }

        if (!WebApplicationLocator.TryLocate(out WebApplicationLaunch? launch, out string? locationFailure))
        {
            SkipReason = "The built web application could not be found: " + locationFailure;
            return;
        }

        _launch = launch;

        try
        {
            // Documented programmatic equivalent of `playwright install chromium`, and the only one
            // that does not require PowerShell on the host. A no-op once the browser is present.
            int installExitCode = Microsoft.Playwright.Program.Main(BrowserInstallArguments);
            if (installExitCode != 0)
            {
                SkipReason =
                    $"`playwright install chromium` exited with {installExitCode}. Install the browser"
                    + " by hand and re-run, or clear ~/.cache/ms-playwright and try again.";
                return;
            }

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (Exception exception)
        {
            SkipReason =
                "Chromium could not be started: " + exception.Message
                + " — on a minimal Linux host the browser's shared libraries may be missing; install"
                + " them once with `playwright install --with-deps chromium` (that step needs root,"
                + " which is why the harness does not attempt it).";
            return;
        }

        try
        {
            // The default database is `postgres` because the harness connects to it only to CREATE
            // DATABASE for each scenario; no scenario ever uses this connection for anything else.
            _container = new PostgreSqlBuilder(PostgreSqlImage)
                .WithDatabase("postgres")
                .WithUsername("myrestaurant")
                .WithPassword("myrestaurant")
                .Build();

            await _container.StartAsync();
            _administrativeConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            SkipReason =
                "A container engine (Podman/Docker) was not reachable: " + exception.Message +
                " — on a rootless-Podman host, activate the user API socket once with" +
                " `systemctl --user enable --now podman.socket` and re-run; the tests discover it" +
                " automatically.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Brings up one isolated instance: a fresh database, a fresh data-protection key directory, the
    /// web application on its own loopback port, and a browser context with a virtual authenticator.
    /// Further contexts — a display device, a guest — come from
    /// <see cref="RestaurantInstance.OpenIsolatedPageAsync"/>.
    /// </summary>
    /// <param name="tableJoinTokenRotationSeconds">
    /// <c>TABLE_JOIN_TOKEN_ROTATION_SECONDS</c> for this instance (§13). There is no right shared
    /// default, which is why it is a parameter: scenario 14 wants a window long enough that "the
    /// previous window" cannot roll over mid-assertion, while scenarios 2 and 15 want one short enough
    /// that a boundary is actually crossed inside a test's patience. §4.3 accepts the current and
    /// previous window whatever their width, so nothing an assertion depends on changes with it.
    /// </param>
    /// <param name="kitchenSubmissionReminderSeconds">
    /// <c>KITCHEN_SUBMISSION_REMINDER_SECONDS</c> for this instance (§13). A parameter for the same
    /// reason the rotation is one, and with the opposite bias: exactly one scenario — §16.3's
    /// eighth — wants it short enough to sit through, and every other wants it left at the
    /// application's own sixty so that §8.4's scan cannot fire during a wait about something else.
    /// The reminder <em>rule</em> does not change with it; only how long a send has to be ignored.
    /// </param>
    internal async Task<RestaurantInstance> StartInstanceAsync(
        int tableJoinTokenRotationSeconds = RestaurantInstance.DefaultTableJoinTokenRotationSeconds,
        int kitchenSubmissionReminderSeconds = RestaurantInstance.DefaultKitchenSubmissionReminderSeconds,
        CancellationToken cancellationToken = default)
    {
        if (SkipReason is not null || _browser is null || _administrativeConnectionString is null || _launch is null)
        {
            throw new InvalidOperationException(
                "The end-to-end harness is unavailable; a scenario must call Assert.SkipUnless on"
                + $" {nameof(SkipReason)} before asking for an instance.");
        }

        int ordinal = Interlocked.Increment(ref _instanceCounter);

        return await RestaurantInstance.StartAsync(
            _browser,
            _administrativeConnectionString,
            _launch,
            ordinal,
            tableJoinTokenRotationSeconds,
            kitchenSubmissionReminderSeconds,
            cancellationToken);
    }

    private static bool IsOptedIn()
    {
        string? value = Environment.GetEnvironmentVariable(OptInVariableName);

        return value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
