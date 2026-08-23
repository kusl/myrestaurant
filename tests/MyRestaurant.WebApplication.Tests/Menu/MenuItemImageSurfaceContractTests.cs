using MyRestaurant.Domain.Menu;
using MyRestaurant.WebApplication.Menu;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

/// <summary>
/// The picture markup at both ends of the feature — the administrator's forms, the route that serves what
/// they store, and the guest's card that renders it — asserted against the markup and the composition root
/// (TECHNICAL_SPECIFICATION §7, §11.1, §11.4, §16.4).
///
/// <para><b>Both surfaces are in one class rather than two, and the route helper is why.</b> Neither page
/// may build a picture's address by hand: §7's route is keyed on the <em>image</em> so that an immutable
/// cache header is a true statement, and a path written out in either file goes wrong when the route moves.
/// That is one claim over two files, so a second class would either assert it twice or leave one surface
/// unguarded — and the §16.4 census counts classes, so splitting would also make the count move for a
/// filing decision rather than for a fact.</para>
///
/// <para><b>Why this exists rather than more facts on <c>MenuWiringTests</c>.</b> That file asserts what
/// the workflow <em>does</em> with an upload once it has one. Every claim here is about whether an upload
/// ever reaches it, and each one fails in the same silent way: the form still renders, the button still
/// submits, the page still redirects, and the file is simply not there. Nothing throws, nothing logs, no
/// other test goes red, and the operator's only symptom is that every picture they choose comes back
/// reported as empty.</para>
///
/// <para><b>Stage 4e adds the two facts about the browser-side downscaler, and they are the opposite
/// shape from the six above them.</b> Those six assert that a string is written in one place; these two
/// assert that a <em>number</em> is written in one place and that the one place is not this side of the
/// wire at all. §8.2's cap is declared in <c>0006</c> and nowhere else — the write service reports a
/// violation by reading the constraint's NAME so that no C# file has an opinion about it — and Stage 4e
/// is the first thing in this feature that needs the number rather than the outcome, because a
/// downscaler with no budget cannot decide when to stop. It is read out of <c>pg_constraint</c> at render
/// time and handed to the browser in an attribute, so the second fact is the one that keeps that true:
/// it takes the bound out of the migration and requires that it appear nowhere under <c>src/</c>
/// besides.</para>
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

    private const string GuestPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor";

    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    private const string CompositionRootRelativePath = "src/MyRestaurant.WebApplication/Program.cs";

    /// <summary>The <c>@formname</c> the attach form posts under, and the remove form's.</summary>
    private const string AttachFormName = "menu-item-image-attach";

    private const string RemoveFormName = "menu-item-image-remove";

    /// <summary>The caption editor's own <c>@formname</c> (Stage 4c).</summary>
    private const string AltTextFormName = "menu-item-image-alt-text";

    private const string DownscalerRelativePath =
        "src/MyRestaurant.WebApplication/wwwroot/js/menu-picture.js";

    private const string RootComponentRelativePath =
        "src/MyRestaurant.WebApplication/Components/App.razor";

    private const string ImageMigrationRelativePath =
        "src/MyRestaurant.DataAccess/Migrations/0006_menu_item_images.sql";

    private const string SourceRelativePath = "src";

    private const string MigrationsDirectoryName = "Migrations";

    /// <summary>
    /// §8.2's named cap, by name. The <em>name</em> is written here and the <em>number</em> is not, which
    /// is the same asymmetry <c>DapperMenuItemImageAdministration</c> carries and for the same reason.
    /// </summary>
    private const string ByteCapConstraintName = "menu_item_image_bytes_within_cap";

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
    /// The caption is edited by a form of its own, and that form does <b>not</b> carry a file input.
    ///
    /// <para><b>The negative half is the claim.</b> A caption folded into the upload form would compile,
    /// render and work — and would make correcting a typo cost a re-upload: a new
    /// <c>menu_item_image_identifier</c>, every cached copy of an unchanged photograph invalidated across
    /// the building for a year, and a <c>replaced</c> event recording a replacement that replaced nothing.
    /// Nothing else in the suite can see that, because the outcome of the wrong design is a page that
    /// works.</para>
    /// </summary>
    [Fact]
    public void TheCaptionIsEditedByItsOwnFormWithNoFileInput()
    {
        string page = ItemPage();

        Assert.Contains($"FormName=\"{AltTextFormName}\"", page, StringComparison.Ordinal);

        int marker = page.IndexOf($"FormName=\"{AltTextFormName}\"", StringComparison.Ordinal);
        int open = page.LastIndexOf("<EditForm", marker, StringComparison.Ordinal);
        Assert.True(open >= 0, "the caption form's marker is not inside an <EditForm> tag.");

        int close = page.IndexOf("</EditForm>", marker, StringComparison.Ordinal);
        Assert.True(close > marker, "the caption form is unterminated.");

        string form = page[open..close];

        Assert.DoesNotContain("type=\"file\"", form, StringComparison.Ordinal);
        Assert.DoesNotContain(
            MenuItemImageUpload.FileFieldName + "\"", form, StringComparison.Ordinal);

        // F-93's rule: a surface acquiring a control acquires the selector §16.3 scenario 16 reaches. The
        // barrier reaches `.manage-inline-form button` and does not reach `.form-actions button`.
        Assert.Contains("class=\"manage-inline-form\"", form, StringComparison.Ordinal);
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

    /// <summary>
    /// §11.1's card renders the picture, builds its address through the route helper, and reads the whole
    /// menu's pictures in <b>one</b> query.
    ///
    /// <para><b>The single-read half is the one with no visible symptom.</b>
    /// <see cref="IMenuItemImageDirectory.ListAsync"/> exists so that a surface decorating a list of cards
    /// asks once; <c>FindForItemAsync</c> is the item page's, and a call to it from inside this component's
    /// render loop would turn a sixty-dish menu into sixty queries per notification — a page that looks
    /// exactly right and gets slower as the restaurant's menu grows, which is the failure this assertion
    /// exists to catch while the menu is still small enough that nobody would notice.</para>
    /// </summary>
    [Fact]
    public void TheGuestMenuRendersThePictureAndReadsThemAllAtOnce()
    {
        string page = GuestPage();

        Assert.True(page.Length > 1000, $"{GuestPageRelativePath} is too short to be the guest surface.");

        Assert.Contains("order-menu-thumbnail", page, StringComparison.Ordinal);
        Assert.Contains("MenuImageRoutes.ForImage(", page, StringComparison.Ordinal);

        // One read, and it is the list read. The negative is the substantive half.
        Assert.Contains("MenuItemPictures.ListAsync(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("FindForItemAsync", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every <c>&lt;img&gt;</c> in the component tree carries an <c>alt</c> attribute</b>, which is
    /// F-103 turned into something a build can refuse.
    ///
    /// <para><b>The distinction this gate protects is not cosmetic, and the plan got it backwards.</b>
    /// <c>docs/MENU_AND_HANDHELD_PLAN.md</c> justified the <c>alt_text</c> column by saying that an
    /// <c>&lt;img&gt;</c> with no alternative text on a menu is a card a screen reader renders as nothing.
    /// That conflates two different things. A <b>missing</b> <c>alt</c> attribute makes a screen reader fall
    /// back to announcing the URL — here a bare UUID, which is worse than silence. <c>alt=""</c> makes it
    /// <em>skip</em> an image whose surroundings already say what it is, and on §11.1's card they do, because
    /// the card is a button holding the dish's name and its price as text. So <c>""</c> is the correct value
    /// for most pictures on this menu and the column earns its place only for the ones that say something a
    /// name does not.</para>
    ///
    /// <para><b>Which is exactly why this needs a gate rather than a sentence.</b> The right value is often
    /// the empty one, so the wrong markup — no attribute at all — is invisible on any screen and produces a
    /// page that looks perfect. The one thing that is never correct is omitting the attribute, and that is
    /// what is asserted. Its own non-vacuity guard is the count, because a scan that found no
    /// <c>&lt;img&gt;</c> at all would pass by having nothing to judge (F-41).</para>
    /// </summary>
    [Fact]
    public void EveryImageInTheTreeCarriesAnAltAttribute()
    {
        List<string> missing = [];
        int images = 0;

        foreach (string file in Directory
            .EnumerateFiles(PathUnder(ComponentsRelativePath), "*.razor", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);

            for (int index = text.IndexOf("<img", StringComparison.Ordinal);
                 index >= 0;
                 index = text.IndexOf("<img", index + 4, StringComparison.Ordinal))
            {
                int close = text.IndexOf('>', index);

                // An unterminated tag is a defect of its own and is reported as one rather than skipped:
                // a scan that quietly ignored what it could not parse is how a gate stops reaching things.
                Assert.True(close > index, $"{name} has an unterminated <img> tag.");

                string tag = text[index..close];
                images++;

                if (!tag.Contains(" alt=", StringComparison.Ordinal))
                {
                    missing.Add($"{name}: {tag.Trim()}");
                }
            }
        }

        Assert.True(images >= 2, $"only {images} <img> tag(s) were found, so nothing is being tested.");

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} <img> tag(s) carry no alt attribute: {string.Join("; ", missing)}. An"
                + " omitted alt makes a screen reader announce the URL — for a menu picture that is a bare"
                + " UUID. alt=\"\" is the correct value for a picture whose surroundings already name it,"
                + " and it is a DIFFERENT thing: write the attribute and leave it empty.");
    }

    /// <summary>
    /// The guest's card takes its <c>alt</c> from the stored column, and §11.4's own thumbnail does not.
    ///
    /// <para><b>The two surfaces want different things from one column and the difference is the
    /// assertion.</b> On the administrator's item page the picture sits under the dish's name in the page's
    /// <c>&lt;h1&gt;</c>, so a caption there would make a screen reader read the dish twice; on a guest's
    /// card among sixty others it is the only thing that can say what the photograph shows. A page that
    /// hard-coded <c>alt=""</c> on the guest's card would render identically, pass the gate above, and make
    /// the whole column unreachable from the only surface it was added for.</para>
    /// </summary>
    [Fact]
    public void TheGuestCardTakesItsAltTextFromTheStoredColumn()
    {
        Assert.Contains("alt=\"@picture.AltText\"", GuestPage(), StringComparison.Ordinal);

        // The administrator's thumbnail keeps the empty one, deliberately, and the constant that carries
        // the caption is still the record's own member rather than a second spelling of the column.
        Assert.Contains("class=\"manage-picture-image\"", ItemPage(), StringComparison.Ordinal);
        Assert.Contains("AltTextInput.AltText", ItemPage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The upload control is handed §8.2's cap and a place to report into, and every one of the four
    /// strings that carries them is read from the constant both sides share (Stage 4e).
    ///
    /// <para><b>The failure mode is the one every fact in this class is about: nothing breaks.</b> A
    /// budget attribute that stopped being rendered leaves a file input that works exactly as it did
    /// before the downscaler existed — the operator chooses a four-megabyte photograph, the form posts
    /// it, and the server refuses it with a sentence about size. No exception, no log, nothing red, and
    /// the only symptom is a feature that quietly stopped being usable. The <c>aria-describedby</c> half
    /// fails even more quietly: the script resolves the element it reports into from that attribute, so a
    /// broken pairing is a downscaler that resizes correctly and says nothing about it.</para>
    ///
    /// <para><b>What is deliberately not asserted is the number.</b> This fact requires the attribute to
    /// be present and to be rendered from a splatted dictionary rather than written out; whether the
    /// value in it is right is <see cref="NoFileUnderSourceRestatesTheStoredPictureCap"/>'s question and
    /// ultimately PostgreSQL's answer.</para>
    /// </summary>
    [Fact]
    public void TheUploadControlIsHandedTheCapAndAPlaceToReport()
    {
        string page = ItemPage();

        // The two attribute names are constants, so the markup must reference the splat rather than
        // spell either of them — a name written in the markup and renamed in the script produces no
        // compiler error on either side.
        Assert.Contains("@attributes=\"PictureBudgetAttributes\"", page, StringComparison.Ordinal);
        Assert.Contains("MenuItemImageUpload.ByteBudgetAttributeName", page, StringComparison.Ordinal);
        Assert.Contains("MenuItemImageUpload.LongestEdgeAttributeName", page, StringComparison.Ordinal);

        // The cap is asked of the database on this page rather than carried in from anywhere.
        Assert.Contains("ReadDeclaredByteCapAsync(", page, StringComparison.Ordinal);

        // The status element and the description that points at it are one constant, used twice.
        Assert.Contains(
            "aria-describedby=\"@MenuItemImageUpload.StatusElementId\"",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"@MenuItemImageUpload.StatusElementId\"",
            page,
            StringComparison.Ordinal);

        Assert.False(
            string.IsNullOrWhiteSpace(MenuItemImageUpload.StatusElementId),
            "the status element id is blank, so aria-describedby would point at nothing.");

        Assert.True(
            MenuItemImageUpload.LongestEdgePixels > 0,
            "a non-positive longest edge would scale every picture to nothing.");

        // The script exists, is loaded from this origin, and reads the same two attributes. Asserted
        // here rather than in the CSP class because the claim is about this feature: that class already
        // forbids an off-origin or inline script tree-wide and has no opinion about which files exist.
        string script = File.ReadAllText(PathUnder(DownscalerRelativePath));

        Assert.Contains(MenuItemImageUpload.ByteBudgetAttributeName, script, StringComparison.Ordinal);
        Assert.Contains(MenuItemImageUpload.LongestEdgeAttributeName, script, StringComparison.Ordinal);
        Assert.Contains(
            "<script src=\"js/menu-picture.js\" defer></script>",
            File.ReadAllText(PathUnder(RootComponentRelativePath)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// §8.2's cap on a stored picture appears in the migration that declares it and <b>nowhere else under
    /// <c>src/</c></b> (Stage 4e, <b>F-65</b>'s mechanism guarded rather than described).
    ///
    /// <para><b>The subject is computed, not listed (F-47).</b> The bound is read out of
    /// <c>0006_menu_item_images.sql</c> and then searched for; this file does not contain it and cannot,
    /// which is what makes the assertion something other than a comparison of one constant against
    /// itself. A migration that changed the cap would change what is searched for in the same commit.</para>
    ///
    /// <para><b>Why this needed a gate on this slice specifically.</b> Four slices of this feature were
    /// built around the rule that the number lives in one place — the write service reports
    /// <see cref="AttachMenuItemImageOutcome.BytesOverCap"/> by reading a constraint's name precisely so
    /// that no C# file has to know it. Stage 4e is the first thing that needs the value, and the obvious
    /// implementation of a browser-side downscaler is a constant in a script. That constant would work,
    /// would look careful, and would be wrong on the day somebody edits the migration — which is F-65's
    /// shape exactly, and F-64 and F-69 are the same mechanism found twice more.</para>
    ///
    /// <para><b>Scoped to <c>src/</c>, and the exclusions are reasons rather than convenience.</b>
    /// <c>Migrations/</c> is the declaration site. <c>tests/</c> legitimately quotes the rendered CHECK in
    /// a doc comment while reading the bound from <c>pg_constraint</c> at run time, so a finding there
    /// would be a finding on a correct file. <c>docs/</c> quotes the DDL, which is the whole job of a
    /// specification, and F-46's lesson is that a rule enforced against the wrong subject is worse than
    /// one enforced against none.</para>
    /// </summary>
    [Fact]
    public void NoFileUnderSourceRestatesTheStoredPictureCap()
    {
        string migration = File.ReadAllText(PathUnder(ImageMigrationRelativePath));

        int marker = migration.IndexOf(ByteCapConstraintName, StringComparison.Ordinal);
        Assert.True(marker >= 0, $"{ImageMigrationRelativePath} no longer declares {ByteCapConstraintName}.");

        // The CHECK that follows the name, read to the end of its own line. Anything wider would sweep
        // up the digits in `octet_length` had it one, and anything narrower would depend on where the
        // migration happens to break its lines.
        int check = migration.IndexOf("CHECK", marker, StringComparison.Ordinal);
        Assert.True(check > marker, $"{ByteCapConstraintName} is declared without a CHECK.");

        int endOfLine = migration.IndexOf('\n', check);
        Assert.True(endOfLine > check, "the cap constraint's CHECK is unterminated.");

        string bound = new string(
            migration[check..endOfLine].Where(char.IsAsciiDigit).ToArray());

        // Non-vacuity, and it is the whole guard: a search for the empty string finds it everywhere, and
        // a search for a bound that failed to parse finds it nowhere. Either way the assertion below
        // would say nothing about the tree.
        Assert.True(
            bound.Length >= 3,
            $"the cap parsed out of {ByteCapConstraintName} as '{bound}', which is not a byte count.");

        List<string> restatements = [];
        int filesScanned = 0;

        foreach (string file in Directory
            .EnumerateFiles(PathUnder(SourceRelativePath), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(MigrationsDirectoryName, StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            filesScanned++;

            if (File.ReadAllText(file).Contains(bound, StringComparison.Ordinal))
            {
                restatements.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            filesScanned >= 20,
            $"Only {filesScanned} file(s) under {SourceRelativePath}/ were scanned, so an emptiness"
                + " assertion over them is not about this tree (F-41).");

        Assert.True(
            restatements.Count == 0,
            $"{restatements.Count} file(s) under {SourceRelativePath}/ restate §8.2's picture cap"
                + $" ({bound}): {string.Join(", ", restatements)}. It is declared once, in"
                + $" {MigrationsDirectoryName}/, and everything that needs it asks: the write service"
                + " reports a violation by the constraint's NAME, and the upload surface reads the value"
                + " through IMenuItemImageDirectory.ReadDeclaredByteCapAsync. A second copy is one fact"
                + " in two places where one edit can make them disagree (F-65).");
    }

    private static string ItemPage() => File.ReadAllText(PathUnder(ItemPageRelativePath));

    private static string GuestPage() => File.ReadAllText(PathUnder(GuestPageRelativePath));

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
