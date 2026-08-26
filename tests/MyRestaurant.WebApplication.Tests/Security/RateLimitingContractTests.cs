using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyRestaurant.DataAccess;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Security;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

public sealed class RateLimitingContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ApplicationSourceDirectory = "src";

    private static readonly Regex OptIn = new(@"EnableRateLimiting\(\s*([^)]*?)\s*\)");

    [Fact]
    public void TheSurfaceListDescribesEveryLimitedSurfaceExactlyOnce()
    {
        IReadOnlyList<RateLimitedSurface> surfaces = RateLimitedSurfaces.All;

        Assert.True(
            surfaces.Count >= 2,
            string.Create(
                CultureInfo.InvariantCulture,
                $"RateLimitedSurfaces.All holds {surfaces.Count} surface(s). Two are specified —"
                + $" /display/pair (§4.2) and /register (§11.8) — and every assertion in this file"
                + $" passes vacuously against a shorter list, so this one runs first."));

        foreach (RateLimitedSurface surface in surfaces)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(surface.PolicyName),
                "A rate-limited surface has a blank policy name. The middleware matches a policy by"
                + " that string; a blank one is a policy no attribute can name.");

            Assert.False(
                string.IsNullOrWhiteSpace(surface.Refusal),
                $"The '{surface.PolicyName}' surface has a blank refusal sentence. A 429 with an empty"
                + " body is a bare error page, which is what the OnRejected handler exists to avoid.");

            Assert.NotNull(surface.Partition);
        }

        List<string> repeated = surfaces
            .GroupBy(surface => surface.PolicyName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(
            repeated.Count == 0,
            $"RateLimitedSurfaces.All registers these policy names more than once:"
            + $" {string.Join(", ", repeated)}. AddPolicy throws on a duplicate name, so this is a"
            + " startup failure rather than a shadowed policy — but it fails at the composition root"
            + " with no mention of which surface was doubled.");
    }

    [Fact]
    public void NoTwoSurfacesShareARefusalAndNoneBorrowsTheFallback()
    {
        IReadOnlyList<RateLimitedSurface> surfaces = RateLimitedSurfaces.All;

        List<string> shared = surfaces
            .GroupBy(surface => surface.Refusal, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" and ", group.Select(surface => surface.PolicyName)))
            .ToList();

        Assert.True(
            shared.Count == 0,
            $"These surfaces are refused in identical words: {string.Join("; ", shared)}. That is the"
            + " defect F-115 is about, arriving from the other side: the mechanism now permits a"
            + " sentence per policy, and two policies sharing one means a caller is told about a"
            + " surface they were not using. §17's objection was never to the wording, it was to the"
            + " wording being wrong and looking deliberate.");

        List<string> borrowed = surfaces
            .Where(surface => string.Equals(
                surface.Refusal,
                RateLimitedSurfaces.GenericRefusal,
                StringComparison.Ordinal))
            .Select(surface => surface.PolicyName)
            .ToList();

        Assert.True(
            borrowed.Count == 0,
            $"These surfaces are refused with RateLimitedSurfaces.GenericRefusal:"
            + $" {string.Join(", ", borrowed)}. That string is the answer for a policy that could not"
            + " be identified. A surface in the list has been identified by definition, so using it"
            + " here throws away the one thing the dispatch was built to recover.");
    }

    [Fact]
    public void TheDispatchResolvesEveryPolicyAndFallsBackForAnythingElse()
    {
        foreach (RateLimitedSurface surface in RateLimitedSurfaces.All)
        {
            Assert.Equal(surface.Refusal, RateLimitedSurfaces.RefusalFor(surface.PolicyName));
        }

        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor("no-such-policy"));
        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor(null));
        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor(string.Empty));

        string differentlyCased = RateLimitedSurfaces.PairingPolicy.ToUpperInvariant();

        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor(differentlyCased));
    }

    [Fact]
    public void EveryOptInInTheTreeNamesARegisteredPolicy()
    {
        IReadOnlyDictionary<string, string> constants = PolicyNameConstants();
        List<string> arguments = [];
        List<string> failures = [];
        int filesRead = 0;

        foreach (string path in SourceFiles(ApplicationSourceDirectory))
        {
            filesRead++;

            string text = SourceCode.WithoutComments(File.ReadAllText(path));
            string relative = RelativeTo(RepositoryRoot(), path);

            foreach (Match match in OptIn.Matches(text))
            {
                string argument = match.Groups[1].Value.Trim();
                arguments.Add(argument);

                const string Prefix = nameof(RateLimitedSurfaces) + ".";

                if (!argument.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{relative} opts in with '{argument}', which is not a member of"
                        + $" {nameof(RateLimitedSurfaces)}. A policy name written twice is a policy name"
                        + " that can drift; name the constant.");
                    continue;
                }

                string member = argument[Prefix.Length..];

                if (!constants.ContainsKey(member))
                {
                    failures.Add(
                        $"{relative} opts in with '{argument}', and"
                        + $" {nameof(RateLimitedSurfaces)}.{member} is not a policy-name constant."
                        + " An unknown policy is not a startup failure: it throws on the first request"
                        + " to reach the endpoint.");
                }
            }
        }

        Assert.True(
            filesRead >= 50,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {filesRead} source file(s) were read under '{ApplicationSourceDirectory}'. The"
                + $" assertion below passes against an empty walk (F-41), so this floor runs first."));

        Assert.True(
            arguments.Count >= 2,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {arguments.Count} [EnableRateLimiting] attribute(s) were found under"
                + $" '{ApplicationSourceDirectory}'. Two surfaces are specified to carry one, so a"
                + $" smaller number means the pattern stopped matching rather than that the tree is"
                + $" clean."));

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void EveryPolicyNameConstantIsARegisteredPolicy()
    {
        IReadOnlyDictionary<string, string> constants = PolicyNameConstants();

        Assert.True(
            constants.Count >= 2,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {constants.Count} public string constant(s) were found on"
                + $" {nameof(RateLimitedSurfaces)}. Two policy names are specified, and the assertion"
                + $" below passes against an empty set (F-41)."));

        HashSet<string> registered = RateLimitedSurfaces.All
            .Select(surface => surface.PolicyName)
            .ToHashSet(StringComparer.Ordinal);

        List<string> unregistered = constants
            .Where(constant => !registered.Contains(constant.Value))
            .Select(constant => $"{constant.Key} = \"{constant.Value}\"")
            .ToList();

        Assert.True(
            unregistered.Count == 0,
            $"{nameof(RateLimitedSurfaces)} declares these policy-name constants that"
            + $" {nameof(RateLimitedSurfaces)}.All never registers: {string.Join(", ", unregistered)}."
            + " A page naming one would compile and deploy, and throw on its first request. Either add"
            + " the surface — with a refusal sentence of its own — or delete the constant.");
    }

    [Fact]
    public void TheLimiterIsRegisteredAndRefusesWithTooManyRequests()
    {
        using ServiceProvider provider = BuildProvider();

        RateLimiterOptions limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.Equal(StatusCodes.Status429TooManyRequests, limiter.RejectionStatusCode);
        Assert.NotNull(limiter.OnRejected);

        Assert.Null(limiter.GlobalLimiter);
    }

    private static IReadOnlyDictionary<string, string> PolicyNameConstants()
        => typeof(RateLimitedSurfaces)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(
                field => field.Name,
                field => (string)field.GetRawConstantValue()!,
                StringComparer.Ordinal);

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
        services.AddSingleton<IDatabaseConnectionFactory, UnusedConnectionFactory>();
        services.AddSingleton(RestaurantOptions.FromConfiguration(new ConfigurationBuilder().Build()));

        services.AddRestaurantRateLimiting();

        return services.BuildServiceProvider();
    }

    private static IEnumerable<string> SourceFiles(string directory)
        => Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), directory), "*.*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string RelativeTo(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}.");
    }

    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Contract tests must not open a database connection.");
    }
}
