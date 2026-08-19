using MyRestaurant.Domain.Menu;
using MyRestaurant.WebApplication.Menu;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

/// <summary>
/// The picture form and the route that serves what it stores, asserted against the markup and the
/// composition root (TECHNICAL_SPECIFICATION §7, §11.4, §16.4).
///
/// <para><b>Why this exists rather than more facts on <c>MenuWiringTests</c>.</b> That file asserts what
/// the workflow <em>does</em> with an upload once it has one. Every claim here is about whether an upload
/// ever reaches it, and each one fails in the same silent way: the form still renders, the button still
/// submits, the page still redirects, and the file is simply not there. Nothing throws, nothing logs, no
/// other test goes red, and the operator's only symptom is that every picture they choose comes back
/// reported as empty.</para>
///
/// <para><b>Three of the five substantive facts are about one string being written in one place.</b> The
/// file input's <c>name</c> is what <c>Request.Form.Files</c> looks the part up by; the <c>accept</c>
/// list is the media-type vocabulary's third declaration after §8.2's CHECK and
/// <see cref="ImageFormat.RecognisedContentTypes"/>, which is <b>F-80's</b> shape; and the thumbnail's
/// address is §7's route, whose key is the <em>image</em> rather than the item and is the whole reason
/// an immutable cache header is a true statement. In all three the markup is asserted to reference the
/// constant rather than to contain the value, because the value is what drifts and the reference is what
/// makes drifting impossible.</para>
///
/// <para><b>It reads source text rather than rendering anything</b>, for <c>LiveSurfaceContractTests</c>'
/// reason: this repository has no bUnit (§16.1), the property under test is a property of the markup, and
/// a renderer would need a container and a database to assert a string. The §16.3 barrier visits this page
/// already and would notice a control that moved; what it cannot notice is an attribute that stopped being
/// written.</para>
///
/// <para>Pure: reads files off the disk it was built from. No server, no container, no browser.</para>
/// </summary>
public sealed class MenuItemImageSurfaceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string ItemPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor";

    private const string CompositionRootRelativePath = "src/MyRestaurant.WebApplication/Program.cs";

    /// <summary>The <c>@formname</c> the attach form posts under, and the remove form's.</summary>
    private const string AttachFormName = "menu-item-image-attach";

    private const string RemoveFormName = "menu-item-image-remove";

    /// <summary>
    /// The scan is real and the page is the one this file thinks it is (F-41). Every assertion below
    /// searches the same text for a substring, and a search of the wrong file — or of a file that lost
    /// the panel entirely — succeeds at finding nothing.
    /// </summary>
    [Fact]
    public void TheAdministrationItemPageCarriesBothPictureForms()
    {
        string page = ItemPage();

        Assert.True(page.Length > 1000, $"{ItemPageRelativePath} is too short to be the item page.");
        Assert.Contains($"@formname=\"{AttachFormName}\"", page, StringComparison.Ordinal);
        Assert.Contains($"@formname=\"{RemoveFormName}\"", page, StringComparison.Ordinal);
        Assert.Contains("<input type=\"file\"", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one attribute the whole transport rests on. Without <c>multipart/form-data</c> a browser posts
    /// the file's <em>name</em> as an ordinary text field and sends none of its bytes — so the form
    /// submits, the handler finds no part, and the upload is reported as empty for every file anybody
    /// ever chooses. There is no other symptom.
    /// </summary>
    [Fact]
    public void TheAttachFormDeclaresTheMultipartEncoding()
    {
        string page = ItemPage();
        string form = FormTagFor(page, AttachFormName);

        Assert.Contains("enctype=\"multipart/form-data\"", form, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", form, StringComparison.Ordinal);
    }

    /// <summary>
    /// The name in the markup and the name the handler indexes by are one constant. A rename applied to
    /// one side produces no compiler error, because one side is an attribute value and the other is a
    /// dictionary key.
    /// </summary>
    [Fact]
    public void TheFileInputIsNamedFromTheConstantTheHandlerReads()
    {
        string page = ItemPage();

        Assert.Contains(
            "name=\"@MenuItemImageUpload.FileFieldName\"",
            page,
            StringComparison.Ordinal);

        Assert.False(
            string.IsNullOrWhiteSpace(MenuItemImageUpload.FileFieldName),
            "the file field name is blank, so Request.Form.Files would be indexed by nothing.");
    }

    /// <summary>
    /// F-80's shape, third declaration. The markup must reference the derived attribute rather than spell
    /// a list out, and the derived attribute must be exactly the domain's own census — an <c>accept</c>
    /// list that has drifted refuses nothing at all, it just hides the file somebody wants from the
    /// picker, which is a defect with no server-side symptom whatsoever.
    /// </summary>
    [Fact]
    public void TheAcceptListIsDerivedFromTheDomainVocabulary()
    {
        string page = ItemPage();

        Assert.Contains(
            "accept=\"@MenuItemImageUpload.AcceptAttribute\"",
            page,
            StringComparison.Ordinal);

        Assert.NotEmpty(ImageFormat.RecognisedContentTypes);
        Assert.Equal(
            string.Join(",", ImageFormat.RecognisedContentTypes),
            MenuItemImageUpload.AcceptAttribute);
    }

    /// <summary>
    /// The thumbnail's address is built by the route helper, and the route helper agrees with the pattern
    /// the endpoint is mapped on. Two halves, because they fail differently: a hand-written path in the
    /// markup goes wrong when the route moves, and a helper that disagreed with its own pattern would
    /// produce a 404 for every picture in the menu at once.
    /// </summary>
    [Fact]
    public void TheThumbnailAddressIsBuiltByTheRouteHelper()
    {
        string page = ItemPage();

        Assert.Contains("MenuImageRoutes.ForImage(", page, StringComparison.Ordinal);

        Guid identifier = Guid.Parse("0192f000-0000-7000-8000-0000000000a1");
        string url = MenuImageRoutes.ForImage(identifier);

        Assert.StartsWith(MenuImageRoutes.Prefix + "/", url, StringComparison.Ordinal);
        Assert.EndsWith(identifier.ToString("D"), url, StringComparison.Ordinal);
        Assert.StartsWith(MenuImageRoutes.Prefix + "/", MenuImageRoutes.Pattern, StringComparison.Ordinal);
    }

    /// <summary>
    /// The route is mapped, and it is keyed on the <em>image</em> rather than on the item.
    ///
    /// <para>The second half is ADR-0015's second decision and is the reason the cache header may say
    /// <c>immutable</c>: a replace mints a new identifier and deletes the old row, so the address changes
    /// exactly when the bytes do. A pattern re-keyed on <c>menuItemIdentifier</c> would still route, still
    /// return a picture, and would then serve last week's photograph out of every browser cache in the
    /// building for a year — which is the longest-lived bug this feature can have and the one nothing
    /// else in the suite could see.</para>
    /// </summary>
    [Fact]
    public void TheCompositionRootMapsTheRouteAndTheRouteIsKeyedOnTheImage()
    {
        string program = File.ReadAllText(PathUnder(CompositionRootRelativePath));

        Assert.Contains("app.MapRestaurantMenuImages();", program, StringComparison.Ordinal);

        Assert.Contains("{menuItemImageIdentifier:guid}", MenuImageRoutes.Pattern, StringComparison.Ordinal);
        Assert.DoesNotContain("{menuItemIdentifier", MenuImageRoutes.Pattern, StringComparison.Ordinal);
        Assert.Contains("immutable", MenuImageRoutes.ImmutableCacheControl, StringComparison.Ordinal);
    }

    private static string ItemPage() => File.ReadAllText(PathUnder(ItemPageRelativePath));

    /// <summary>
    /// The opening tag of the form posting under <paramref name="formName"/>, so an attribute asserted
    /// about one form cannot be satisfied by a different form on the same page. Deliberately a substring
    /// walk rather than a parse: these files are Razor, and a parser would have to be taught about
    /// <c>@</c> expressions to say anything a substring cannot.
    /// </summary>
    private static string FormTagFor(string page, string formName)
    {
        int marker = page.IndexOf($"@formname=\"{formName}\"", StringComparison.Ordinal);
        Assert.True(marker >= 0, $"no form posts under '{formName}'.");

        int open = page.LastIndexOf("<form", marker, StringComparison.Ordinal);
        Assert.True(open >= 0, $"the '{formName}' marker is not inside a <form> tag.");

        int close = page.IndexOf('>', marker);
        Assert.True(close > open, $"the '{formName}' form tag is unterminated.");

        return page[open..close];
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
}
