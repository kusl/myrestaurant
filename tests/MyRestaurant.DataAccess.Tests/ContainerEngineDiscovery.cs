using System.Runtime.CompilerServices;

namespace MyRestaurant.DataAccess.Tests;

/// <summary>
/// Points Testcontainers at a <b>rootless Podman</b> socket when one is available and nothing else
/// has been configured (BUILD_PROGRESS: container-dependent tests; ADR-0004 — rootless Podman is the
/// canonical engine).
///
/// <para><b>Why this exists.</b> Testcontainers for .NET resolves its container endpoint from, in
/// order: the <c>DOCKER_HOST</c> environment variable, <c>~/.testcontainers.properties</c>, and
/// finally the Docker default <c>unix:///var/run/docker.sock</c>. On a rootless-Podman host (the
/// canonical setup) none of those exist, so every integration test skipped with
/// "Failed to connect to Docker endpoint at 'unix:///var/run/docker.sock'" — even while
/// <c>run.sh</c> was happily starting PostgreSQL through <c>podman-compose</c> on the same machine.
/// Podman's Docker-compatible API socket lives at
/// <c>$XDG_RUNTIME_DIR/podman/podman.sock</c> and only exists once the user socket unit is active:
/// <c>systemctl --user enable --now podman.socket</c> (one time, no root).</para>
///
/// <para><b>Why a module initializer.</b> Testcontainers snapshots its environment-derived
/// configuration in static singletons on first touch of any Testcontainers type. A module
/// initializer runs when this test assembly loads — strictly before any fixture constructs a
/// builder — so the variables set here are guaranteed to be visible to that snapshot. Setting them
/// inside a fixture would be a race against other fixtures.</para>
///
/// <para><b>What it never does.</b> Explicit user configuration always wins: if
/// <c>DOCKER_HOST</c> or <c>TESTCONTAINERS_HOST_OVERRIDE</c> is set, or a
/// <c>~/.testcontainers.properties</c> file exists, or the Docker socket itself is present, this
/// initializer changes nothing. When it does select Podman it also disables Ryuk (the resource
/// reaper) unless the caller configured it: Ryuk needs to bind-mount the engine socket into a
/// privileged helper container, which is unreliable under rootless Podman, and every fixture here
/// disposes its own container anyway.</para>
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
        // XDG_RUNTIME_DIR is set on every systemd login session (Fedora, Debian, Ubuntu, ...).
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return;
        }

        string podmanSocket = Path.Combine(runtimeDirectory, "podman", "podman.sock");
        if (!File.Exists(podmanSocket))
        {
            // The engine is installed but its API socket is not active; PostgreSqlFixture's skip
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
