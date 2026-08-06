using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

/// <summary>
/// The §11.10 live-surface contract, asserted against the Razor sources themselves
/// (TECHNICAL_SPECIFICATION §11.10, §16.4).
///
/// <para><b>Why this exists.</b> F-44 fixed one surface because one scenario had failed on it, and
/// recorded in the same breath that <em>the other four</em> carried the same latent race. There is no
/// list of four, and that is F-47: <c>App.razor</c>'s <c>RenderModeForPage</c> makes every routable
/// page interactive unless it carries <c>[ExcludeFromInteractiveRouting]</c>, which is six pages plus
/// the island hosted inside <c>/table/{id}</c> — and <c>/table</c> had been one of them since M3 while
/// publishing nothing at all. A rule enforced as a list of examples is enforced as a list of examples
/// (F-46). So this test derives the set from the attribute rather than from anybody's memory.</para>
///
/// <para><b>Why it reads source text rather than rendering anything.</b> The property under test is a
/// property of the markup: that an attribute is present, on an interactive surface, bound to the right
/// expression. Rendering a Blazor component to assert it would need a test renderer, a DI container and
/// a database per surface, and would still be asserting the same string. The §16.3 scenarios already
/// exercise these attributes in a real browser; what they cannot do is notice a <em>seventh</em> surface
/// that nobody wrote a scenario for, which is precisely how F-47 survived four slices.</para>
///
/// <para><b>What it deliberately does not pin.</b> <c>data-live</c>'s expression is pinned exactly,
/// because "a circuit produced this markup" has one correct answer in this framework and it is
/// <c>RendererInfo.IsInteractive</c>. <c>data-loaded</c>'s is not: §11.10 defines that bit as
/// <em>the surface has what it renders itself for</em>, and on <c>TableDisplay</c> that means a join
/// code rather than a completed query. Pinning the body there would have forced the display to publish
/// an attribute that was always <c>true</c> whenever its element existed — true, and useless. So the
/// shape is pinned (a property named <c>IsLoadedAttributeValue</c> feeding the attribute) and the
/// predicate is each surface's own.</para>
///
/// <para>Pure: no server, no container, no clock. It reads files off the disk it was built from.</para>
/// </summary>
public sealed class LiveSurfaceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    /// <summary>§11.10's first bit, as it must appear in the markup.</summary>
    private const string LiveAttribute = "data-live=\"@IsLiveAttributeValue\"";

    /// <summary>§11.10's second bit, as it must appear in the markup.</summary>
    private const string LoadedAttribute = "data-loaded=\"@IsLoadedAttributeValue\"";

    /// <summary>The attribute names on their own, for catching one published some other way.</summary>
    private const string LiveAttributeName = "data-live=";
    private const string LoadedAttributeName = "data-loaded=";

    /// <summary>
    /// The one correct answer to "did a circuit produce this markup". Pinned in full, because a
    /// surface that answered it from anything else — a field set in <c>OnAfterRender</c>, a parameter,
    /// a literal — would be answering a different question and would still look right in dev tools.
    /// </summary>
    private const string LiveExpression =
        "private string IsLiveAttributeValue => RendererInfo.IsInteractive ? \"true\" : \"false\";";

    /// <summary>The shape of the second bit. The predicate after it is each surface's own — see remarks.</summary>
    private const string LoadedPropertyPrefix = "private string IsLoadedAttributeValue =>";

    /// <summary>
    /// What marks a surface as having a state in which it renders neither its content nor its own
    /// account of why there is none. Every one of the six spells it this way; the static-SSR pages use
    /// null-collection sentinels instead, which is not an accident — a statically rendered page emits
    /// its markup once, fully loaded, so it has nothing to publish.
    /// </summary>
    private const string LoadingStateField = "private bool _loaded;";

    private const string PageDirective = "@page ";
    private const string ExclusionAttribute = "@attribute [ExcludeFromInteractiveRouting]";
    private const string InteractiveIslandMarker = "@rendermode=\"InteractiveServer\"";

    /// <summary>
    /// The interactive set, written down on purpose so that adding a surface is a decision rather than
    /// an omission. This is the one list in this file, and it is a list of what the <em>rule</em> is
    /// expected to produce rather than a substitute for the rule: the assertion compares it against a
    /// set derived from <c>[ExcludeFromInteractiveRouting]</c>, so the two can only agree by being
    /// right. A new page that needs no loading state (as <c>Home</c> does not) still belongs here —
    /// the point of the speed bump is that somebody looked.
    /// </summary>
    private static readonly IReadOnlyList<string> ExpectedInteractiveComponents =
    [
        "CounterBoard",
        "CounterSitting",
        "Home",
        "KitchenBoard",
        "TableArea",
        "TableDisplay",
        "TableOrderSurface",
    ];

    /// <summary>
    /// A floor rather than an exact count. The assertion this guards is "the scan found the tree", and
    /// an exact number would fail on every unrelated page added — a gate that reports a finding on a
    /// correct tree is a gate people learn to bypass (F-41).
    /// </summary>
    private const int MinimumComponentsExpected = 30;

    /// <summary>Likewise a floor: the static-SSR pages are the great majority of this tree.</summary>
    private const int MinimumStaticPagesExpected = 20;

    private static readonly IReadOnlyList<RazorComponent> AllComponents = Discover();

    /// <summary>One <c>.razor</c> file, classified by §11.10's rule.</summary>
    private sealed record RazorComponent(
        string Name,
        string RelativePath,
        string Text,
        bool IsRoutablePage,
        bool IsExcludedFromInteractiveRouting,
        bool IsHostedAsInteractiveIsland)
    {
        /// <summary>
        /// §11.10's rule, and the whole reason this file reads sources instead of trusting a comment:
        /// a routable page is interactive unless it opted out, and a component hosted with an explicit
        /// <c>@rendermode="InteractiveServer"</c> is interactive wherever it lives.
        /// </summary>
        internal bool IsInteractive
            => IsHostedAsInteractiveIsland || (IsRoutablePage && !IsExcludedFromInteractiveRouting);

        internal bool IsStaticallyRoutedPage => IsRoutablePage && IsExcludedFromInteractiveRouting;

        internal bool HasLoadingState => Text.Contains(LoadingStateField, StringComparison.Ordinal);

        internal bool PublishesEitherBit
            => Text.Contains(LiveAttributeName, StringComparison.Ordinal)
                || Text.Contains(LoadedAttributeName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScanReadsTheTreeAndClassifiesTheComponentsThisContractIsAbout()
    {
        // A contract test that quietly found nothing would pass forever, which is the failure mode
        // F-41 named: a gate has to report what it looked at, and it has to be impossible to satisfy
        // vacuously. These three assertions are that, in order of how they would break.
        Assert.True(
            AllComponents.Count >= MinimumComponentsExpected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {AllComponents.Count} .razor file(s) were found under '{ComponentsRelativePath}',"
                + $" which is fewer than the {MinimumComponentsExpected} this tree has carried since M5."
                + $" The scan is looking in the wrong place rather than the tree having shrunk."));

        int staticPages = AllComponents.Count(component => component.IsStaticallyRoutedPage);
        Assert.True(
            staticPages >= MinimumStaticPagesExpected,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {staticPages} routable page(s) carry {ExclusionAttribute}. The exclusion"
                + $" attribute is how §11.10's rule tells the two kinds of surface apart, so if this is"
                + $" low the classification is broken and every other assertion here is meaningless."));

        string[] interactive = AllComponents
            .Where(component => component.IsInteractive)
            .Select(component => component.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedInteractiveComponents.Order(StringComparer.Ordinal).ToArray(), interactive);
    }

    [Fact]
    public void EveryInteractiveSurfaceWithALoadingState_PublishesTheLiveBit()
    {
        string[] offenders = AllComponents
            .Where(component => component.IsInteractive && component.HasLoadingState)
            .Where(component => !component.Text.Contains(LiveAttribute, StringComparison.Ordinal))
            .Select(component => component.RelativePath)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"§11.10 requires every interactive surface with a loading state to publish"
                + $" {LiveAttribute}, so that \"a circuit produced this markup\" is answerable from"
                + $" outside the process. Missing from: {string.Join(", ", offenders)}."));
    }

    [Fact]
    public void EveryInteractiveSurfaceWithALoadingState_PublishesTheLoadedBit()
    {
        string[] offenders = AllComponents
            .Where(component => component.IsInteractive && component.HasLoadingState)
            .Where(component => !component.Text.Contains(LoadedAttribute, StringComparison.Ordinal))
            .Select(component => component.RelativePath)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"§11.10 requires every interactive surface with a loading state to publish"
                + $" {LoadedAttribute} beside the live bit. Without it a reader waiting on data-live"
                + $" alone is steered towards the circuit's first render — the one instant when the"
                + $" surface has finished nothing — rather than past it (F-44, F-47). Missing from:"
                + $" {string.Join(", ", offenders)}."));
    }

    [Fact]
    public void TheTwoBitsAreAlwaysPublishedTogetherOnTheSameSurface()
    {
        // TableDisplay renders its surface element from two branches and so publishes each bit twice.
        // Any file where the counts disagree has an element carrying one bit and not the other, which
        // is the shape of the defect rather than a style difference: a selector demanding both would
        // never match that element, and a selector demanding one would match it too early.
        string[] offenders = AllComponents
            .Where(component => CountOf(component.Text, LiveAttribute) != CountOf(component.Text, LoadedAttribute))
            .Select(component => string.Create(
                CultureInfo.InvariantCulture,
                $"{component.RelativePath} ({CountOf(component.Text, LiveAttribute)} live,"
                + $" {CountOf(component.Text, LoadedAttribute)} loaded)"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"§11.10's two bits go on the same element, so a file publishes each the same number"
                + $" of times. Mismatched: {string.Join("; ", offenders)}."));
    }

    [Fact]
    public void EveryPublishedLiveBit_IsAnsweredByRendererInfoIsInteractive()
    {
        string[] offenders = AllComponents
            .Where(component => component.Text.Contains(LiveAttributeName, StringComparison.Ordinal))
            .Where(component => !component.Text.Contains(LiveExpression, StringComparison.Ordinal))
            .Select(component => component.RelativePath)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"A surface that publishes data-live must answer it with exactly `{LiveExpression}`."
                + $" Any other source for that bit — a field set in OnAfterRender, a parameter, a"
                + $" literal — answers a different question and still looks right in dev tools."
                + $" Offending: {string.Join(", ", offenders)}."));
    }

    [Fact]
    public void EveryPublishedLoadedBit_ComesFromAPropertyRatherThanAnInlineExpression()
    {
        string[] offenders = AllComponents
            .Where(component => component.Text.Contains(LoadedAttributeName, StringComparison.Ordinal))
            .Where(component => !component.Text.Contains(LoadedPropertyPrefix, StringComparison.Ordinal))
            .Select(component => component.RelativePath)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"A surface that publishes data-loaded must define `{LoadedPropertyPrefix} …` in its"
                + $" @code block. The predicate is deliberately each surface's own — §11.10's bit means"
                + $" \"has what it renders itself for\", which on TableDisplay is a join code and not a"
                + $" completed query — but it belongs in a named property with the reasoning attached,"
                + $" not inline in an attribute where nobody can say why it is what it is."
                + $" Offending: {string.Join(", ", offenders)}."));
    }

    [Fact]
    public void NothingButAnInteractiveSurfacePublishesEitherBit()
    {
        // The other direction, and it is not symmetry for its own sake. A statically rendered page
        // publishing data-live would publish "false" forever: an attribute that is a lie in the shape
        // of an assertion, and one a harness could wait on until it timed out.
        string[] offenders = AllComponents
            .Where(component => !component.IsInteractive && component.PublishesEitherBit)
            .Select(component => component.RelativePath)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"§11.10's bits belong to interactive surfaces only. On a statically rendered page"
                + $" data-live is 'false' on every render that will ever happen, which is an attribute"
                + $" shaped like an assertion and empty of one. Offending: {string.Join(", ", offenders)}."));
    }

    /// <summary>
    /// Reads every <c>.razor</c> file under the web application's <c>Components</c> directory and
    /// classifies it. Fails loudly rather than skipping if the tree cannot be found: a contract test
    /// that quietly declines to run is the thing this file exists to prevent.
    /// </summary>
    private static IReadOnlyList<RazorComponent> Discover()
    {
        DirectoryInfo repositoryRoot = FindRepositoryRoot();
        string componentsDirectory = Path.Combine(
            repositoryRoot.FullName,
            ComponentsRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(componentsDirectory))
        {
            throw new InvalidOperationException(
                $"'{componentsDirectory}' does not exist, so the §11.10 contract cannot be checked."
                + " The repository root was found but its layout is not the one §2 describes.");
        }

        string[] files = Directory
            .GetFiles(componentsDirectory, "*.razor", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, string> textByPath = files.ToDictionary(
            path => path,
            File.ReadAllText,
            StringComparer.Ordinal);

        // Islands are named by their host, so the whole tree has to be read before any one file can be
        // classified. This is the half a per-file rule would have got wrong: TableOrderSurface carries
        // no @page and no @rendermode of its own, and is interactive because /table/{id} says so.
        HashSet<string> islands = new(StringComparer.Ordinal);
        foreach (string text in textByPath.Values)
        {
            foreach (string name in InteractiveIslandNamesIn(text))
            {
                islands.Add(name);
            }
        }

        List<RazorComponent> components = [];
        foreach (string path in files)
        {
            string text = textByPath[path];
            string name = Path.GetFileNameWithoutExtension(path);
            string relativePath = Path
                .GetRelativePath(repositoryRoot.FullName, path)
                .Replace(Path.DirectorySeparatorChar, '/');

            bool routable = false;
            bool excluded = false;

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith(PageDirective, StringComparison.Ordinal))
                {
                    routable = true;
                }

                if (trimmed.StartsWith(ExclusionAttribute, StringComparison.Ordinal))
                {
                    excluded = true;
                }
            }

            components.Add(new RazorComponent(
                name,
                relativePath,
                text,
                routable,
                excluded,
                islands.Contains(name)));
        }

        return components;
    }

    /// <summary>
    /// The component names hosted with an explicit interactive render mode in one file's markup. Read
    /// by walking back from the marker to the tag that opened it rather than with a pattern, so the
    /// answer does not depend on where the attribute sits relative to its element's line breaks.
    /// </summary>
    private static IReadOnlyList<string> InteractiveIslandNamesIn(string text)
    {
        List<string> names = [];
        int cursor = 0;

        while (true)
        {
            int marker = text.IndexOf(InteractiveIslandMarker, cursor, StringComparison.Ordinal);
            if (marker < 0)
            {
                return names;
            }

            cursor = marker + InteractiveIslandMarker.Length;

            int openingBracket = text.LastIndexOf('<', marker);
            if (openingBracket < 0)
            {
                continue;
            }

            int start = openingBracket + 1;
            int end = start;
            while (end < marker && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }

            if (end > start)
            {
                names.Add(text[start..end]);
            }
        }
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        int cursor = 0;

        while (true)
        {
            int found = text.IndexOf(value, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            cursor = found + value.Length;
        }
    }

    /// <summary>
    /// Walks up from this test assembly's own output until it finds the solution file — the same
    /// discovery <c>WebApplicationLocator</c> uses in the end-to-end harness, and for the same reason:
    /// it keeps a Debug run and a Release run pointed at the tree they were built from without either
    /// being told where that is.
    /// </summary>
    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}, so the"
            + " §11.10 contract could not be checked against any source. This test asserts a property"
            + " of the Razor markup and has to read it; it fails rather than skips, because a contract"
            + " test that quietly declines to run is exactly the defect it was written for.");
    }
}
