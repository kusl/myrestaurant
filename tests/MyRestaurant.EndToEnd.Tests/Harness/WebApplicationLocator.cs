using System.Diagnostics.CodeAnalysis;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// Everything needed to start one copy of the already-built web application as a child process.
/// </summary>
/// <param name="RepositoryRoot">The directory holding <c>MyRestaurant.slnx</c>.</param>
/// <param name="ContentRoot">
/// The web application's <em>source</em> directory. It becomes <c>ASPNETCORE_CONTENTROOT</c>, because
/// <c>Program.cs</c> serves assets with <c>UseStaticFiles()</c> and <c>wwwroot</c> is not copied into
/// <c>bin</c> — without this, <c>js/passkey.js</c> would 404 and every passkey ceremony would fail
/// with no browser-side clue why.
/// </param>
/// <param name="FileName">The executable to start: the apphost, or the <c>dotnet</c> muxer.</param>
/// <param name="Arguments">Empty for the apphost; the managed assembly path for the muxer.</param>
/// <param name="WorkingDirectory">The build output directory.</param>
internal sealed record WebApplicationLaunch(
    string RepositoryRoot,
    string ContentRoot,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

/// <summary>
/// Finds the web application's build output by walking up from this test assembly's own.
///
/// <para>The configuration and target framework are read from this assembly's output path rather than
/// injected as an MSBuild constant, which keeps the two trees automatically in step: a Debug test run
/// looks for a Debug web application, a Release run for a Release one. That matters because CI builds
/// and tests in Release while a workstation usually does not, and a harness that hard-coded either
/// would silently boot a stale binary from the other.</para>
/// </summary>
internal static class WebApplicationLocator
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ProjectName = "MyRestaurant.WebApplication";

    internal static bool TryLocate(
        [NotNullWhen(true)] out WebApplicationLaunch? launch,
        [NotNullWhen(false)] out string? failureReason)
    {
        launch = null;

        // .../tests/MyRestaurant.EndToEnd.Tests/bin/<Configuration>/<TargetFramework>/
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

    /// <summary>
    /// The <c>dotnet</c> muxer, only needed when a build produced no apphost. <c>DOTNET_ROOT</c> wins;
    /// otherwise it is derived from the shared framework this test process is itself running on, which
    /// lives at <c>&lt;dotnet-root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>.
    /// </summary>
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
