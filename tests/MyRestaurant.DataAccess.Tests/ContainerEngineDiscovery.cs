using System.Runtime.CompilerServices;

namespace MyRestaurant.DataAccess.Tests;

internal static class ContainerEngineDiscovery
{
    [ModuleInitializer]
    internal static void Initialize()
    {
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

        if (!OperatingSystem.IsLinux() || File.Exists("/var/run/docker.sock"))
        {
            return;
        }

        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return;
        }

        string podmanSocket = Path.Combine(runtimeDirectory, "podman", "podman.sock");
        if (!File.Exists(podmanSocket))
        {
            return;
        }

        Environment.SetEnvironmentVariable("DOCKER_HOST", $"unix://{podmanSocket}");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED")))
        {
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
        }
    }
}
