using System.Runtime.CompilerServices;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// Points Testcontainers at a <b>rootless Podman</b> socket when one is available and nothing else
/// has been configured (ADR-0004 — rootless Podman is the canonical engine).
///
/// <para>This duplicates <c>MyRestaurant.DataAccess.Tests.ContainerEngineDiscovery</c> on purpose,
/// and the duplication is not accidental sloppiness: a <see cref="ModuleInitializerAttribute"/> runs
/// once per <em>assembly</em> load, and Testcontainers snapshots its environment-derived
/// configuration into static singletons the first time any of its types is touched. A shared helper
/// in one test project cannot run inside the other's process, and both projects run as their own test
/// host. The alternative — a shared "test support" project — would be one more project in the
/// solution for thirty lines that must not have a public API. See the DataAccess copy for the full
/// rationale; the logic below is identical.</para>
/// </summary>
internal static class ContainerEngineDiscovery
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Respect anything the user configured explicitly — env vars or the properties file.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE")))
        {
            return;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, ".testcontainers.properties")))
        {
            return;
        }

        // If the Docker default endpoint exists, Testcontainers will find it on its own.
        if (!OperatingSystem.IsLinux() || File.Exists("/var/run/docker.sock"))
        {
            return;
        }

        // Rootless Podman publishes its Docker-compatible API under the user runtime directory.
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return;
        }

        string podmanSocket = Path.Combine(runtimeDirectory, "podman", "podman.sock");
        if (!File.Exists(podmanSocket))
        {
            // The engine is installed but its API socket is not active; RestaurantHarness's skip
            // message tells the developer the one-time `systemctl --user enable --now podman.socket`.
            return;
        }

        Environment.SetEnvironmentVariable("DOCKER_HOST", $"unix://{podmanSocket}");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED")))
        {
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
        }
    }
}
