using DotNet.Testcontainers.Configurations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests.Harness;

public sealed class RestaurantHarness : IAsyncLifetime
{
    public const string OptInVariableName = "MYRESTAURANT_E2E";

    private const string PostgreSqlImage = "docker.io/library/postgres:17-alpine";

    private static readonly string[] UnresolvableReferenceMarkers =
    [
        "short-name",
        "unqualified-search",
        "did not resolve to an alias",
    ];

    private static readonly string[] BrowserInstallArguments = ["install", "chromium"];

    private int _instanceCounter;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private PostgreSqlContainer? _container;
    private string? _administrativeConnectionString;
    private WebApplicationLaunch? _launch;

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
            _container = new PostgreSqlBuilder(PostgreSqlImage)
                .WithDatabase("postgres")
                .WithUsername("myrestaurant")
                .WithPassword("myrestaurant")
                .WithLogger(NullLogger.Instance)
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
