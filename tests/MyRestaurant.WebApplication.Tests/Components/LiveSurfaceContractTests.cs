using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Components;

public sealed class LiveSurfaceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    private const string LiveAttribute = "data-live=\"@IsLiveAttributeValue\"";

    private const string LoadedAttribute = "data-loaded=\"@IsLoadedAttributeValue\"";

    private const string LiveAttributeName = "data-live=";
    private const string LoadedAttributeName = "data-loaded=";

    private const string LiveExpression =
        "private string IsLiveAttributeValue => RendererInfo.IsInteractive ? \"true\" : \"false\";";

    private const string LoadedPropertyPrefix = "private string IsLoadedAttributeValue =>";

    private const string LoadingStateField = "private bool _loaded;";

    private const string PageDirective = "@page ";
    private const string ExclusionAttribute = "@attribute [ExcludeFromInteractiveRouting]";
    private const string InteractiveIslandMarker = "@rendermode=\"InteractiveServer\"";

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

    private const int MinimumComponentsExpected = 30;

    private const int MinimumStaticPagesExpected = 20;

    private static readonly IReadOnlyList<RazorComponent> AllComponents = Discover();

    private sealed record RazorComponent(
        string Name,
        string RelativePath,
        string Text,
        bool IsRoutablePage,
        bool IsExcludedFromInteractiveRouting,
        bool IsHostedAsInteractiveIsland)
    {
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
