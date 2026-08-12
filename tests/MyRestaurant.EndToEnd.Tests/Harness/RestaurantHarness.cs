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

    /// <summary>
    /// Fully qualified, and the same reference <c>compose.yaml</c> gives the <c>postgres</c> service
    /// (TECHNICAL_SPECIFICATION §14.1, <b>F-60</b>). Testcontainers passes this to the engine
    /// verbatim — <c>MatchImage.Match</c> records a registry only when the first slash-separated
    /// segment contains a <c>.</c> or a <c>:</c>, and its own comment says it "does not resolve or
    /// set the default domain and repository prefix" — so a short name here is resolved through
    /// <c>unqualified-search-registries</c>, which a stock Debian ships commented out. That is
    /// F-51's mechanism, and here its consequence is quieter and worse: the catch below turns it into
    /// a skip, so every §16.3 scenario declines to run and the suite reports success.
    /// </summary>
    private const string PostgreSqlImage = "docker.io/library/postgres:17-alpine";

    /// <summary>
    /// Fragments a container engine uses when it cannot turn an image reference into a registry.
    /// Matched case-insensitively against the whole exception chain, because the wording differs
    /// between Podman's own error and the Docker-compatible API's relay of it.
    /// </summary>
    private static readonly string[] UnresolvableReferenceMarkers =
    [
        "short-name",
        "unqualified-search",
        "did not resolve to an alias",
    ];

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
            SkipReason = DescribeContainerFailure(exception);
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
    /// <param name="handheld">
    /// Lay this instance's primary context out at 375×667 (§11.12) rather than at Playwright's default.
    /// One scenario — §16.3's sixteenth — sets it; every other leaves it alone.
    ///
    /// <para><b>Why this changes nothing for the other fifteen, stated because the opposite was believed
    /// for a slice (F-62).</b> A viewport belongs to a browser context, and this harness holds one
    /// <em>browser</em> from which <see cref="StartInstanceAsync"/> mints a fresh context per instance
    /// and <c>OpenIsolatedPageAsync</c> mints further ones on request. There is no shared default context
    /// to resize and nothing for a later scenario to inherit. The §11.12 barrier was deferred out of
    /// Slice 30 on the belief that there was, and that belief is what the ledger row is about.</para>
    /// </param>
    internal async Task<RestaurantInstance> StartInstanceAsync(
        int tableJoinTokenRotationSeconds = RestaurantInstance.DefaultTableJoinTokenRotationSeconds,
        int kitchenSubmissionReminderSeconds = RestaurantInstance.DefaultKitchenSubmissionReminderSeconds,
        bool handheld = false,
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
            handheld,
            cancellationToken);
    }

    /// <summary>
    /// Names what actually went wrong rather than the most common thing that goes wrong (<b>F-60</b>).
    /// Every unavailability used to be reported as "a container engine was not reachable", with a
    /// remediation about activating the Podman socket — so an operator whose engine was reachable and
    /// whose *image reference* was unresolvable was told to fix something that was not broken. Here
    /// the mis-diagnosis costs more than in the data-access fixture, because these scenarios
    /// are the only thing in this repository that exercises the product end to end, and their
    /// declining to run looks exactly like their passing.
    ///
    /// <para>Both branches name the image, which the previous message omitted entirely and which is
    /// the single most useful fact when a pull is what failed.</para>
    /// </summary>
    private static string DescribeContainerFailure(Exception exception)
    {
        string detail = Flatten(exception);

        if (MentionsUnresolvableReference(detail))
        {
            return
                $"The image reference '{PostgreSqlImage}' could not be resolved to a registry by this"
                + " container engine: " + detail
                + " — this is not an unreachable engine. A reference without a registry component is"
                + " resolved through `unqualified-search-registries`, which a stock Debian ships"
                + " commented out (F-51, F-60). The reference above is fully qualified, so if this is"
                + " what failed then the engine could not reach the registry it names, or the local"
                + " store has the image under a different name. Check network egress to that"
                + " registry, or pre-pull it by hand.";
        }

        return
            "A container engine (Podman/Docker) was not reachable: " + exception.Message
            + $" — the image this harness starts is '{PostgreSqlImage}'. On a rootless-Podman host,"
            + " activate the user API socket once with `systemctl --user enable --now podman.socket`"
            + " and re-run; the tests discover it automatically.";
    }

    private static bool MentionsUnresolvableReference(string detail)
    {
        foreach (string marker in UnresolvableReferenceMarkers)
        {
            if (detail.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The whole exception chain's text. Testcontainers wraps the engine's response, so the sentence
    /// that names the cause is routinely on an inner exception rather than on the one thrown.
    /// </summary>
    private static string Flatten(Exception exception)
    {
        List<string> messages = [];

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Length > 0)
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" | ", messages);
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
