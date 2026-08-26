using System.Diagnostics.CodeAnalysis;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed record WebApplicationLaunch(
    string RepositoryRoot,
    string ContentRoot,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

internal static class WebApplicationLocator
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ProjectName = "MyRestaurant.WebApplication";

    internal static bool TryLocate(
        [NotNullWhen(true)] out WebApplicationLaunch? launch,
        [NotNullWhen(false)] out string? failureReason)
    {
        launch = null;

        DirectoryInfo targetFrameworkDirectory = new(AppContext.BaseDirectory);
        DirectoryInfo? configurationDirectory = targetFrameworkDirectory.Parent;
        if (configurationDirectory is null)
        {
            failureReason = $"no build configuration could be read out of '{AppContext.BaseDirectory}'.";
            return false;
        }

        string targetFramework = targetFrameworkDirectory.Name;
        string configuration = configurationDirectory.Name;

        DirectoryInfo? repositoryRoot = FindRepositoryRoot(targetFrameworkDirectory);
        if (repositoryRoot is null)
        {
            failureReason = $"walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}.";
            return false;
        }

        string contentRoot = Path.Combine(repositoryRoot.FullName, "src", ProjectName);
        if (!Directory.Exists(contentRoot))
        {
            failureReason = $"the web application source directory '{contentRoot}' does not exist.";
            return false;
        }

        string outputDirectory = Path.Combine(contentRoot, "bin", configuration, targetFramework);
        string apphostPath = Path.Combine(outputDirectory, ProjectName);
        string assemblyPath = Path.Combine(outputDirectory, ProjectName + ".dll");

        if (File.Exists(apphostPath))
        {
            launch = new WebApplicationLaunch(repositoryRoot.FullName, contentRoot, apphostPath, [], outputDirectory);
            failureReason = null;
            return true;
        }

        if (!File.Exists(assemblyPath))
        {
            failureReason =
                $"neither '{apphostPath}' nor '{assemblyPath}' exists — build the solution in the"
                + $" '{configuration}' configuration first (`dotnet build MyRestaurant.slnx"
                + $" --configuration {configuration}`).";
            return false;
        }

        if (!TryFindDotnetMuxer(out string? muxerPath))
        {
            failureReason =
                $"'{assemblyPath}' exists but no apphost was produced alongside it, and the `dotnet`"
                + " muxer could not be located (set DOTNET_ROOT).";
            return false;
        }

        launch = new WebApplicationLaunch(
            repositoryRoot.FullName, contentRoot, muxerPath, [assemblyPath], outputDirectory);
        failureReason = null;
        return true;
    }

    private static DirectoryInfo? FindRepositoryRoot(DirectoryInfo start)
    {
        for (DirectoryInfo? candidate = start; candidate is not null; candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryFindDotnetMuxer([NotNullWhen(true)] out string? muxerPath)
    {
        string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            string configured = Path.Combine(dotnetRoot, executableName);
            if (File.Exists(configured))
            {
                muxerPath = configured;
                return true;
            }
        }

        string coreLibraryPath = typeof(object).Assembly.Location;
        if (!string.IsNullOrEmpty(coreLibraryPath))
        {
            string? sharedFrameworkDirectory = Path.GetDirectoryName(coreLibraryPath);
            DirectoryInfo? root = sharedFrameworkDirectory is null
                ? null
                : new DirectoryInfo(sharedFrameworkDirectory).Parent?.Parent?.Parent;

            if (root is not null)
            {
                string derived = Path.Combine(root.FullName, executableName);
                if (File.Exists(derived))
                {
                    muxerPath = derived;
                    return true;
                }
            }
        }

        muxerPath = null;
        return false;
    }
}
