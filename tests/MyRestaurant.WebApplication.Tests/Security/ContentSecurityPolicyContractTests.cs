using System.Globalization;
using MyRestaurant.WebApplication.Security;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Security;

/// <summary>
/// The §11.11 policy against the tree it protects (TECHNICAL_SPECIFICATION §11.11, §16.4, F-49).
///
/// <para><b>Why this exists, and why it is not more assertions in
/// <see cref="ResponseSecurityHeadersTests"/>.</b> That file asserts what the header <em>says</em>.
/// This one asserts that what the header says is still true of the markup — that every script is still
/// same-origin, that nothing inline has appeared, that the two concessions the policy makes are still
/// earned by facts in the tree. A Content Security Policy is the only kind of configuration in this
/// project that becomes <em>wrong</em> by somebody editing a file it does not mention: add one
/// <c>&lt;script&gt;</c> block to a Razor page and the page silently stops working in production while
/// every test stays green. The policy and its subject are two things, so they are checked as two
/// things.</para>
///
/// <para><b>It computes the category rather than reading a list</b> (F-47's habit, sixth application).
/// The question "what does this application load, and from where" is answered by scanning the markup
/// and the static assets, not by anybody's memory of them — the same reason
/// <c>LiveSurfaceContractTests</c> derives its subject from the routing rule. Every count it produces
/// is asserted non-zero first, because a scan that found nothing passes every assertion after it
/// (F-41).</para>
///
/// <para><b>The concessions are asserted in both directions.</b> <c>style-src 'unsafe-inline'</c> is
/// there because nineteen components carry a scoped <c>&lt;style&gt;</c> block; <c>img-src data:</c>
/// is there because of one empty favicon. If either fact ever stops being true, this test fails and
/// says to tighten the policy — which is the only mechanism that ever removes a concession, since
/// nothing else about a working application changes when one is dropped.</para>
///
/// <para>Pure: reads files off the disk it was built from. No server, no container, no browser.</para>
/// </summary>
public sealed class ContentSecurityPolicyContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";
    private const string StaticAssetsRelativePath = "src/MyRestaurant.WebApplication/wwwroot";
    private const string CompositionRootRelativePath = "src/MyRestaurant.WebApplication/Program.cs";

    /// <summary>A representative host, so the policy under test is the one a real request produces.</summary>
    private const string SampleHost = "orders.example.com";

    /// <summary>
    /// Elements whose <c>src</c> or <c>href</c> is a <em>fetch</em> and therefore governed by a fetch
    /// directive. <c>&lt;a href&gt;</c> is deliberately absent: a link is a navigation, and this
    /// application links out to the operator's forge from <c>/source</c> on purpose (§11.9).
    /// </summary>
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

    /// <summary>
    /// The one <c>data:</c> URL this tree is allowed to contain, and the reason <c>img-src</c> admits
    /// the scheme at all: an empty icon, so that a browser does not request <c>/favicon.ico</c> on
    /// every page of a restaurant's phone traffic. A <c>&lt;link rel="icon"&gt;</c> is an image fetch as
    /// far as CSP is concerned, so without the scheme every page load logs a violation.
    /// </summary>
    private const string FaviconMarkup = "<link rel=\"icon\" href=\"data:,\" />";

    [Fact]
    public void TheScanReadsTheTreeAndClassifiesIt()
    {
        MarkupScan scan = ScanMarkup();

        // The file-walk floor stays separate, with its reason stated
        Assert.True(scan.FileCount >= 40, $"only {scan.FileCount} .razor files were read.");

        // The non-vacuity guards passing through a single helper
        AssertIsPopulated(scan.ExternalScriptSources.Count, "<script src>");
        AssertIsPopulated(scan.StylesheetHrefs.Count, "stylesheet <link>");
        AssertIsPopulated(scan.InlineStyleBlockCount, "inline <style> block");
        AssertIsPopulated(scan.ResourceReferenceCount, "resource-loading element");
    }

    private static void AssertIsPopulated(int count, string subject)
    {
        Assert.True(count >= 1, $"no {subject} was found, so nothing below is tested.");
    }

    /// <summary>
    /// The assertion that keeps <c>script-src 'self'</c> honest. There is no <c>'unsafe-inline'</c>, no
    /// hash and no nonce in the policy, so an inline script added to any component would simply not run
    /// — in production, on a phone, with nothing red anywhere in this suite. This is the test that goes
    /// red instead.
    /// </summary>
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

    /// <summary>
    /// An HTML event-handler attribute is inline script by another name and needs <c>'unsafe-hashes'</c>
    /// plus a per-handler digest — which is what Microsoft's starter policy for a Blazor Web App carries
    /// and this one does not, because this tree has none. Blazor's own <c>@onclick</c> is a directive
    /// attribute compiled into a delegate and is not affected.
    /// </summary>
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

    /// <summary>
    /// <c>default-src 'self'</c> is the fallback for every directive this policy does not name — fonts,
    /// media, manifests, workers. It is only a sufficient answer for as long as the markup asks for
    /// nothing off-origin at all, so that is what is asserted rather than the directive list being
    /// exhaustive.
    /// </summary>
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

    /// <summary>
    /// The stylesheets fetch nothing, which is why the policy needs no <c>font-src</c> and why
    /// <c>img-src</c> can be as narrow as it is. A single <c>url()</c> for a web font would make both
    /// statements false without touching any markup.
    /// </summary>
    [Fact]
    public void TheStylesheetsFetchNothing()
    {
        IReadOnlyList<string> problems = ScanStylesheets();

        Assert.True(
            problems.Count == 0,
            "the policy names no font-src and img-src admits only 'self' and data:, which holds only"
            + " while the stylesheets load nothing. Found: " + string.Join(", ", problems));
    }

    /// <summary>
    /// The bytes this application now serves itself are inside the policy it already publishes — asserted
    /// rather than assumed, which is the whole of F-49's lesson and was carried by name from the slice
    /// that created the schema to this one, which added the route.
    ///
    /// <para><b>Nothing about §11.11 changed and that is the point.</b> <c>img-src 'self' data:</c>
    /// admits a same-origin fetch, and <c>/menu/image/{id}</c> is same-origin by construction because the
    /// bytes come out of this application's own database rather than out of a bucket somebody configured.
    /// A Content Security Policy is the one configuration here that becomes wrong by editing a file it
    /// does not mention, so "no change needed" is a claim, and a claim with no assertion behind it is
    /// how a policy comes to be correct by accident and then stops being.</para>
    ///
    /// <para><b>Both halves are asserted, and the second is the one that would actually fail.</b> That
    /// the directive admits <c>'self'</c> is stable; that the page's <c>&lt;img&gt;</c> is still
    /// same-origin is not, because the shape that would break it — a CDN, an object store, a thumbnail
    /// service — is exactly the shape somebody reaches for when a menu grows pictures.
    /// <see cref="NoResourceElementNamesAnAbsoluteUrl"/> already forbids it tree-wide; this names the
    /// element that made the question live, so a failure says which feature it is about.</para>
    /// </summary>
    [Fact]
    public void TheServedPictureBytesAreInsideThePolicyAlready()
    {
        MarkupScan scan = ScanMarkup();

        Assert.Contains("img-src 'self' data:", PolicyText(), StringComparison.Ordinal);

        // The tree has an <img> at all — without one the assertion below passes vacuously (F-41), and
        // this is the slice that introduced the first one.
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

    /// <summary>The first concession, tied to the one fact that earns it.</summary>
    [Fact]
    public void TheOnlyDataUrlIsTheFaviconThatImgSrcAdmits()
    {
        MarkupScan scan = ScanMarkup();

        Assert.Equal(1, scan.DataUrlCount);
        Assert.True(scan.CarriesTheFavicon, $"{FaviconMarkup} was not found in the markup.");
        Assert.Contains("img-src 'self' data:", PolicyText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The second concession, tied to the facts that earn it — and it is the only mechanism that would
    /// ever cause it to be dropped. Nineteen components carry a scoped <c>&lt;style&gt;</c> block, and
    /// Blazor's reconnection overlay builds one at runtime with <c>innerHTML</c>, so removing the
    /// components' blocks alone would not be enough; the second half is recorded here because it is
    /// invisible in this tree and would otherwise be rediscovered by somebody watching a guest's screen
    /// lose its dialog styling at the moment a circuit drops.
    /// </summary>
    [Fact]
    public void TheInlineStyleConcessionIsStillEarned()
    {
        MarkupScan scan = ScanMarkup();

        Assert.True(scan.InlineStyleBlockCount > 0);
        Assert.Contains("style-src 'self' 'unsafe-inline'", PolicyText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The wiring, asserted rather than described. Three things have to be true at once and none of them
    /// is visible from the middleware's own file: that the composition root installs it at all; that it
    /// installs it before anything that can answer a request, since a header written on the way out of a
    /// pipeline that short-circuited is a header nobody sent; and that the framework's own partial
    /// policy is turned off, because <c>AddInteractiveServerRenderMode</c> <em>appends</em> to this
    /// header rather than setting it, and two policies on one response are enforced as an intersection
    /// that nobody reading the response can decipher.
    /// </summary>
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

    /// <summary>Off-origin means it names a scheme or begins protocol-relative. Everything else is a path.</summary>
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

    /// <summary>
    /// Every <c>src=</c> / <c>href=</c> on a resource-loading element, as (element, attribute, value).
    /// Deliberately a scan over literal markup rather than a parse: these files are Razor, not HTML, and
    /// a parser would have to be taught about <c>@</c> expressions to say anything a substring cannot.
    /// </summary>
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

    /// <summary>
    /// HTML event-handler attributes: a space, then <c>on</c>, then lowercase letters, then <c>="</c>.
    /// Razor's <c>@onclick</c> does not match, because the character before <c>on</c> is <c>@</c> rather
    /// than whitespace — which is the whole distinction, and the reason this looks for the space.
    /// </summary>
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other contract tests use, failing rather than
    /// skipping for the same reason: a check that quietly declines to run is worse than none.
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
