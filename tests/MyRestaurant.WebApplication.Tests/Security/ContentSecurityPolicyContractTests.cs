using System.Globalization;
using MyRestaurant.WebApplication.Security;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

public sealed class ContentSecurityPolicyContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";
    private const string StaticAssetsRelativePath = "src/MyRestaurant.WebApplication/wwwroot";
    private const string CompositionRootRelativePath = "src/MyRestaurant.WebApplication/Program.cs";

    private const string SampleHost = "orders.example.com";

    private static readonly string[] ResourceElements =
    [
        "<script",
        "<link",
        "<img",
        "<source",
        "<iframe",
        "<frame",
        "<object",
        "<embed",
        "<audio",
        "<video",
        "<track",
    ];

    private const string FaviconMarkup = "<link rel=\"icon\" href=\"data:,\" />";

    [Fact]
    public void TheScanReadsTheTreeAndClassifiesIt()
    {
        MarkupScan scan = ScanMarkup();

        Assert.True(scan.FileCount >= 40, $"only {scan.FileCount} .razor files were read.");

        AssertIsPopulated(scan.ExternalScriptSources.Count, "<script src>");
        AssertIsPopulated(scan.StylesheetHrefs.Count, "stylesheet <link>");
        AssertIsPopulated(scan.InlineStyleBlockCount, "inline <style> block");
        AssertIsPopulated(scan.ResourceReferenceCount, "resource-loading element");
    }

    private static void AssertIsPopulated(int count, string subject)
    {
        Assert.True(count >= 1, $"no {subject} was found, so nothing below is tested.");
    }

    [Fact]
    public void NoMarkupCarriesAnInlineScript()
    {
        MarkupScan scan = ScanMarkup();

        Assert.True(
            scan.InlineScripts.Count == 0,
            "the policy's script-src is 'self' with no hash and no nonce, so an inline <script> would be"
            + " refused by the browser and would fail nowhere else. Move it into wwwroot/js/ beside"
            + " passkey.js, display.js, kitchen.js and clock.js. Found in: "
            + string.Join(", ", scan.InlineScripts));
    }

    [Fact]
    public void NoMarkupCarriesAnInlineEventHandlerAttribute()
    {
        MarkupScan scan = ScanMarkup();

        Assert.True(
            scan.InlineEventHandlers.Count == 0,
            "an HTML on* attribute is inline script, and admitting one costs the policy 'unsafe-hashes'"
            + " plus a digest per handler. Blazor's @onclick is not this. Found in: "
            + string.Join(", ", scan.InlineEventHandlers));
    }

    [Fact]
    public void EveryScriptAndStylesheetIsLoadedFromThisOrigin()
    {
        MarkupScan scan = ScanMarkup();

        List<string> offOrigin =
        [
            .. scan.ExternalScriptSources.Where(IsOffOrigin),
            .. scan.StylesheetHrefs.Where(IsOffOrigin),
        ];

        Assert.True(
            offOrigin.Count == 0,
            "script-src and style-src are both 'self', so a reference to another origin would be"
            + " refused. Off-origin: " + string.Join(", ", offOrigin));
    }

    [Fact]
    public void NoResourceElementNamesAnAbsoluteUrl()
    {
        MarkupScan scan = ScanMarkup();

        Assert.True(
            scan.AbsoluteResourceReferences.Count == 0,
            "default-src is 'self' and no directive names a host, so a resource element pointing at"
            + " another origin would be refused. A link (<a href>) is a navigation and is not this."
            + " Found: " + string.Join(", ", scan.AbsoluteResourceReferences));
    }

    [Fact]
    public void TheStylesheetsFetchNothing()
    {
        IReadOnlyList<string> problems = ScanStylesheets();

        Assert.True(
            problems.Count == 0,
            "the policy names no font-src and img-src admits only 'self' and data:, which holds only"
            + " while the stylesheets load nothing. Found: " + string.Join(", ", problems));
    }

    [Fact]
    public void TheServedPictureBytesAreInsideThePolicyAlready()
    {
        MarkupScan scan = ScanMarkup();

        Assert.Contains("img-src 'self' data:", PolicyText(), StringComparison.Ordinal);

        Assert.True(
            scan.ImageSources.Count >= 1,
            "no <img src> was found, so nothing about img-src is being tested.");

        Assert.True(
            scan.ImageSources.All(source => !IsOffOrigin(source)),
            "img-src is 'self' and data:, so a picture loaded from another origin would be refused."
            + " Menu pictures are served from this application's own /menu/image route by design"
            + " (ADR-0015). Off-origin: "
            + string.Join(", ", scan.ImageSources.Where(IsOffOrigin)));
    }

    [Fact]
    public void TheOnlyDataUrlIsTheFaviconThatImgSrcAdmits()
    {
        MarkupScan scan = ScanMarkup();

        Assert.Equal(1, scan.DataUrlCount);
        Assert.True(scan.CarriesTheFavicon, $"{FaviconMarkup} was not found in the markup.");
        Assert.Contains("img-src 'self' data:", PolicyText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInlineStyleConcessionIsStillEarned()
    {
        MarkupScan scan = ScanMarkup();

        Assert.True(scan.InlineStyleBlockCount > 0);
        Assert.Contains("style-src 'self' 'unsafe-inline'", PolicyText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompositionRootDeliversThePolicyAndOnlyThePolicy()
    {
        string program = File.ReadAllText(PathUnder(CompositionRootRelativePath));

        int middleware = program.IndexOf("app.UseMiddleware<SecurityHeadersMiddleware>();", StringComparison.Ordinal);
        Assert.True(middleware >= 0, $"{CompositionRootRelativePath} does not install SecurityHeadersMiddleware.");

        Assert.Contains(
            "ContentSecurityFrameAncestorsPolicy = null",
            program,
            StringComparison.Ordinal);

        foreach (string later in (string[])["app.UseRateLimiter();", "app.UseStaticFiles();", "app.MapRazorComponents<App>()"])
        {
            int index = program.IndexOf(later, StringComparison.Ordinal);
            Assert.True(index >= 0, $"{CompositionRootRelativePath} no longer contains '{later}'.");
            Assert.True(
                middleware < index,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"SecurityHeadersMiddleware is registered after '{later}'. Everything that can"
                    + $" short-circuit has to be behind it, or the responses that skip the endpoint"
                    + $" pipeline entirely — static files, the rate limiter's 429, the obligations"
                    + $" redirect, a 404 — go out bare."));
        }
    }

    private static string PolicyText() => ResponseSecurityHeaders.ContentSecurityPolicyFor(SampleHost);

    private static bool IsOffOrigin(string reference)
        => reference.StartsWith("//", StringComparison.Ordinal)
            || reference.Contains("://", StringComparison.Ordinal);

    private static MarkupScan ScanMarkup()
    {
        string root = PathUnder(ComponentsRelativePath);
        MarkupScan scan = new();

        foreach (string file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            string text = File.ReadAllText(file);
            scan.FileCount++;

            if (text.Contains(FaviconMarkup, StringComparison.Ordinal))
            {
                scan.CarriesTheFavicon = true;
            }

            scan.InlineStyleBlockCount += Occurrences(text, "<style>");
            scan.DataUrlCount += Occurrences(text, "=\"data:");

            foreach ((string element, string attribute, string value) in ResourceReferences(text))
            {
                scan.ResourceReferenceCount++;

                if (element == "<script" && attribute == "src")
                {
                    scan.ExternalScriptSources.Add(value);
                }

                if (element == "<link" && attribute == "href" && value.EndsWith(".css", StringComparison.Ordinal))
                {
                    scan.StylesheetHrefs.Add(value);
                }

                if (element == "<img" && attribute == "src")
                {
                    scan.ImageSources.Add(value);
                }

                if (IsOffOrigin(value))
                {
                    scan.AbsoluteResourceReferences.Add($"{name}: {value}");
                }
            }

            foreach (int index in AllIndexesOf(text, "<script"))
            {
                int close = text.IndexOf('>', index);
                string tag = close < 0 ? text[index..] : text[index..close];
                if (!tag.Contains(" src=", StringComparison.Ordinal))
                {
                    scan.InlineScripts.Add(name);
                }
            }

            foreach (string handler in InlineEventHandlersIn(text))
            {
                scan.InlineEventHandlers.Add($"{name}: {handler}");
            }
        }

        return scan;
    }

    private static IEnumerable<(string Element, string Attribute, string Value)> ResourceReferences(string text)
    {
        foreach (string element in ResourceElements)
        {
            foreach (int index in AllIndexesOf(text, element))
            {
                int close = text.IndexOf('>', index);
                string tag = close < 0 ? text[index..] : text[index..close];

                foreach (string attribute in (string[])["src", "href"])
                {
                    string marker = attribute + "=\"";
                    int at = tag.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        continue;
                    }

                    int start = at + marker.Length;
                    int end = tag.IndexOf('"', start);
                    if (end > start)
                    {
                        yield return (element, attribute, tag[start..end]);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> InlineEventHandlersIn(string text)
    {
        for (int index = 0; index < text.Length - 4; index++)
        {
            if (!char.IsWhiteSpace(text[index]) || text[index + 1] != 'o' || text[index + 2] != 'n')
            {
                continue;
            }

            int end = index + 3;
            while (end < text.Length && char.IsAsciiLetterLower(text[end]))
            {
                end++;
            }

            if (end > index + 3 && end < text.Length - 1 && text[end] == '=' && text[end + 1] == '"')
            {
                yield return text[(index + 1)..end];
            }
        }
    }

    private static IReadOnlyList<string> ScanStylesheets()
    {
        List<string> problems = [];
        string root = PathUnder(StaticAssetsRelativePath);

        foreach (string file in Directory.EnumerateFiles(root, "*.css", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);

            if (text.Contains("url(", StringComparison.Ordinal))
            {
                problems.Add($"{name}: url(");
            }

            if (text.Contains("@import", StringComparison.Ordinal))
            {
                problems.Add($"{name}: @import");
            }
        }

        return problems;
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        foreach (int _ in AllIndexesOf(text, value))
        {
            count++;
        }

        return count;
    }

    private static IEnumerable<int> AllIndexesOf(string text, string value)
    {
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    private static string PathUnder(string relativePath)
        => Path.Combine(
            FindRepositoryRoot().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

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
            $"Walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}.");
    }

    private sealed class MarkupScan
    {
        public int FileCount { get; set; }

        public int InlineStyleBlockCount { get; set; }

        public int DataUrlCount { get; set; }

        public int ResourceReferenceCount { get; set; }

        public bool CarriesTheFavicon { get; set; }

        public List<string> ExternalScriptSources { get; } = [];

        public List<string> StylesheetHrefs { get; } = [];

        public List<string> ImageSources { get; } = [];

        public List<string> InlineScripts { get; } = [];

        public List<string> InlineEventHandlers { get; } = [];

        public List<string> AbsoluteResourceReferences { get; } = [];
    }
}
