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

/// <summary>
/// A rate-limited surface is refused in its own words (TECHNICAL_SPECIFICATION §4.2, §11.8, §16.4, §17,
/// <b>F-115</b>).
///
/// <para><b>Why this exists.</b> §17 carried, for eleven slices, a paragraph explaining that
/// `/register` could not be given a rate limit without a mechanism change — because
/// <c>RateLimiterOptions.OnRejected</c> and <c>RejectionStatusCode</c> are single-valued, so a second
/// <c>AddRateLimiter</c> call takes the refusal wording away from the first, and a refused registration
/// would have answered with §4.2's <em>"too many pairing attempts"</em>. That paragraph also named the
/// fix. What is worth noticing is the shape of the eleven slices: <b>the ruling, the reason and the
/// remedy were all written down and correct, and nothing in this tree could tell whether they had been
/// acted on.</b> The wall was documented; being documented is not being closed.</para>
///
/// <para><b>What is decidable here, and what is not.</b> Whether a limit is the <em>right</em> limit is
/// a judgement about a dining room that no test will make — and this file deliberately asserts nothing
/// about the numbers, which live in <c>RestaurantOptionsTests</c> beside every other bound. What is
/// decidable is the structure §17 asked for: that a policy cannot exist without a sentence of its own,
/// that no two surfaces share one, that the dispatch resolves each policy to its own sentence, that an
/// unresolvable policy gets a vague sentence rather than a wrong one, and that the tree does not name a
/// policy the list never registered.</para>
///
/// <para><b>Why the subject is computed twice over, from two different directions.</b> F-58's lesson:
/// a gate that names its subject is a gate about one file. So the page scan finds every
/// <c>[EnableRateLimiting]</c> under <c>src/</c> without naming a page, and the constant scan finds every
/// public string literal on <see cref="RateLimitedSurfaces"/> by reflection without naming a policy. The
/// two close each other's hole: the scan catches an attribute naming a policy the list does not have,
/// and the reflection catches a policy constant that no entry in the list uses — which is the half a
/// text scan cannot see, because an unused constant appears in no attribute.</para>
///
/// <para><b>The page scan was wrong on arrival and its own summary said why it was safe (F-116).</b> It
/// read source <em>text</em>, and one of the files it reads explains <c>[EnableRateLimiting]</c> in a
/// documentation comment that spells the form with a placeholder argument — so the first run of this
/// class on a real tree reported the file it exists to protect as opting into a policy called
/// <em>…</em>. It now reads source <em>code</em>, through <see cref="SourceCode"/>. Worth recording
/// beside the fix: the sensitivity proof accompanying this class emulated the scan over the same files
/// with the same pattern and did not reproduce the failure, because the emulation was written against
/// the tree the authoring session imagined rather than the one it shipped.</para>
///
/// <para><b>Pure.</b> Reads the surface list, one service provider that opens no connection, and two
/// directories of source text. No server, no container, no engine.</para>
/// </summary>
public sealed class RateLimitingContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ApplicationSourceDirectory = "src";

    /// <summary>
    /// An opt-in as it is written in this tree, in either language. Razor spells it
    /// <c>@attribute [EnableRateLimiting(…)]</c> and C# would spell it <c>[EnableRateLimiting(…)]</c>;
    /// the attribute name and its argument are what both have in common.
    ///
    /// <para><b>The open parenthesis is not what makes this safe, and believing it was is F-116.</b> This
    /// pattern shipped with a paragraph claiming that the parenthesis distinguished a use of the attribute
    /// from a mention of one in the prose explaining it, on F-67's authority. F-67 is about an
    /// <em>identifier</em> — a gate keying on <c>Foo</c> catches every sentence containing the word, a gate
    /// keying on <c>Foo(</c> does not — and it does not transfer to a <em>form</em>. The very file this
    /// gate protects explains the attribute in a documentation comment spelling the whole form with a
    /// placeholder for its argument, exactly as the two illustrations above do, so the first real run
    /// reported a finding on a correct tree: <c>RateLimitedSurfaces.cs</c> <em>opts in with</em> a
    /// horizontal ellipsis. <b>The prose was not the mistake.</b> A comment explaining a construct spells
    /// that construct; the mistake was a scan that could read comments, and the fix is that it no longer
    /// can — see <see cref="SourceCode"/>, and note that the mention is deliberately still in the tree so
    /// that the fix is load-bearing rather than theoretical.</para>
    /// </summary>
    private static readonly Regex OptIn = new(@"EnableRateLimiting\(\s*([^)]*?)\s*\)");

    /// <summary>
    /// Every surface is a policy name, a sentence and a partitioner, and no two of them collide.
    ///
    /// <para>Asserted first and on its own, because every assertion below is satisfied by an empty list
    /// (F-41) — and a list that had lost an entry to a merge would produce exactly that, silently, on the
    /// surface whose whole subject is a refusal nobody reads until it happens.</para>
    /// </summary>
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

    /// <summary>
    /// <b>This is F-115.</b> No two surfaces are refused in the same words, and no surface is refused in
    /// the fallback's words. Those are the two ways the old arrangement's defect would come back: one
    /// sentence serving two endpoints, or a surface added to the list without one and quietly inheriting
    /// the generic wording, which reads as deliberate and is not.
    /// </summary>
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

    /// <summary>
    /// The dispatch answers each registered policy with that policy's sentence, and anything else with
    /// the fallback. The second half is the one worth having: it is the only assertion in this file that
    /// exercises the branch §17 was afraid of, and it asserts that the branch is <em>vague</em> rather
    /// than that it is absent.
    /// </summary>
    [Fact]
    public void TheDispatchResolvesEveryPolicyAndFallsBackForAnythingElse()
    {
        foreach (RateLimitedSurface surface in RateLimitedSurfaces.All)
        {
            Assert.Equal(surface.Refusal, RateLimitedSurfaces.RefusalFor(surface.PolicyName));
        }

        // A policy name nobody registered, a null endpoint's absent attribute, and an empty string —
        // the three shapes GetMetadata<EnableRateLimitingAttribute>()?.PolicyName can produce that are
        // not a registered name.
        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor("no-such-policy"));
        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor(null));
        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor(string.Empty));

        // Ordinal, matching the framework's own policy lookup. A case-insensitive match here would
        // succeed on a string the middleware would have failed on, which is a worse answer than the
        // fallback: it would report a surface that was never refused.
        string differentlyCased = RateLimitedSurfaces.PairingPolicy.ToUpperInvariant();

        Assert.Equal(RateLimitedSurfaces.GenericRefusal, RateLimitedSurfaces.RefusalFor(differentlyCased));
    }

    /// <summary>
    /// Every <c>[EnableRateLimiting]</c> under <c>src/</c> names a policy the surface list registers.
    ///
    /// <para>Unknown policy names do not fail at startup — the framework resolves a policy when a request
    /// arrives at the endpoint, so a typo here is an exception on the first visit to an <em>anonymous</em>
    /// page, which is the worst place in this application to discover one. The argument must additionally
    /// be a member access on <see cref="RateLimitedSurfaces"/> rather than a literal: a literal would be a
    /// second copy of the policy name, joined to the first only by somebody remembering to edit it, which
    /// is F-50's shape at the smallest possible stakes.</para>
    /// </summary>
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

            // Code, not text (F-116). A documentation comment that spells the attribute is prose about
            // an opt-in and not one, and this scan's own subject is a file whose comments do exactly
            // that.
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

    /// <summary>
    /// Every policy-name constant on <see cref="RateLimitedSurfaces"/> is a policy the list registers.
    ///
    /// <para>The half the scan above cannot reach: a constant that no attribute names appears in no
    /// attribute, so a text scan reports nothing about it. Left alone it is a policy name that looks
    /// available and is not — and the surface that opted into it would compile, deploy, and throw on its
    /// first anonymous visitor.</para>
    ///
    /// <para><b>The subject is the type's string literals, which is why <c>GenericRefusal</c> is
    /// <c>static readonly</c> and not <c>const</c>.</b> That one keyword is what lets this assertion say
    /// <em>every public string constant</em> instead of <em>every public string constant whose name ends
    /// in a word I chose</em>. A naming convention would have made this a gate about spelling; the
    /// keyword makes it a gate about the type.</para>
    /// </summary>
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

    /// <summary>
    /// The limiter is registered by <c>AddRestaurantRateLimiting</c>, refuses with 429, carries a
    /// rejection handler, and adds no global limiter.
    ///
    /// <para>Moved here from <c>DisplaysWiringTests</c> in the slice that moved the registration it
    /// asserts. It is the same claim it always was, about the same options object, asked of the extension
    /// that now owns it — and asking it of <c>AddRestaurantDisplays</c> would now pass against a
    /// framework default of 503 while proving nothing.</para>
    /// </summary>
    [Fact]
    public void TheLimiterIsRegisteredAndRefusesWithTooManyRequests()
    {
        using ServiceProvider provider = BuildProvider();

        // AddRateLimiter is what makes app.UseRateLimiter() legal; without it the middleware throws at
        // startup. Resolving the options proves the call happened and pins the refusal status: the
        // framework default is 503, which would misreport a spent budget as a sick server.
        RateLimiterOptions limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.Equal(StatusCodes.Status429TooManyRequests, limiter.RejectionStatusCode);
        Assert.NotNull(limiter.OnRejected);

        // No global limiter: only endpoints carrying [EnableRateLimiting] are affected, so nothing else
        // in the application silently acquires a budget — and the refusal dispatch can rely on an
        // endpoint being in scope when it runs.
        Assert.Null(limiter.GlobalLimiter);
    }

    /// <summary>
    /// The public string literals declared on <see cref="RateLimitedSurfaces"/>, by member name. Literal
    /// constants only — <c>IsLiteral</c> is what excludes <c>GenericRefusal</c>, which is
    /// <c>static readonly</c> for that purpose.
    /// </summary>
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

        // The prerequisites Program.cs registers before AddRestaurantRateLimiting. RestaurantOptions is
        // among them because the registration partitioner resolves it per request; the connection factory
        // is never used here — resolution constructs, it does not connect.
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> every other contract test in this repository uses,
    /// and it throws rather than skips for the same reason: a check that quietly declines to run is worse
    /// than none.
    /// </summary>
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

    /// <summary>This test never opens a connection; this makes that explicit.</summary>
    private sealed class UnusedConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Contract tests must not open a database connection.");
    }
}
