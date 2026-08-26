using MyRestaurant.Domain.Menu;
using MyRestaurant.WebApplication.Menu;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

public sealed class MenuItemImageSurfaceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string ItemPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor";

    private const string GuestPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor";

    private const string ComponentsRelativePath = "src/MyRestaurant.WebApplication/Components";

    private const string CompositionRootRelativePath = "src/MyRestaurant.WebApplication/Program.cs";

    private const string AttachFormName = "menu-item-image-attach";

    private const string RemoveFormName = "menu-item-image-remove";

    private const string AltTextFormName = "menu-item-image-alt-text";

    private const string DownscalerRelativePath =
        "src/MyRestaurant.WebApplication/wwwroot/js/menu-picture.js";

    private const string RootComponentRelativePath =
        "src/MyRestaurant.WebApplication/Components/App.razor";

    private const string ImageMigrationRelativePath =
        "src/MyRestaurant.DataAccess/Migrations/0006_menu_item_images.sql";

    private const string SourceRelativePath = "src";

    private const string MigrationsDirectoryName = "Migrations";

    private const string ByteCapConstraintName = "menu_item_image_bytes_within_cap";

    [Fact]
    public void TheAdministrationItemPageCarriesBothPictureForms()
    {
        string page = ItemPage();

        Assert.True(page.Length > 1000, $"{ItemPageRelativePath} is too short to be the item page.");
        Assert.Contains($"@formname=\"{AttachFormName}\"", page, StringComparison.Ordinal);
        Assert.Contains($"@formname=\"{RemoveFormName}\"", page, StringComparison.Ordinal);
        Assert.Contains("<input type=\"file\"", page, StringComparison.Ordinal);
    }

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

        Assert.Contains("class=\"manage-inline-form\"", form, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttachFormDeclaresTheMultipartEncoding()
    {
        string page = ItemPage();
        string form = FormTagFor(page, AttachFormName);

        Assert.Contains("enctype=\"multipart/form-data\"", form, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", form, StringComparison.Ordinal);
    }

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

    [Fact]
    public void TheCompositionRootMapsTheRouteAndTheRouteIsKeyedOnTheImage()
    {
        string program = File.ReadAllText(PathUnder(CompositionRootRelativePath));

        Assert.Contains("app.MapRestaurantMenuImages();", program, StringComparison.Ordinal);

        Assert.Contains("{menuItemImageIdentifier:guid}", MenuImageRoutes.Pattern, StringComparison.Ordinal);
        Assert.DoesNotContain("{menuItemIdentifier", MenuImageRoutes.Pattern, StringComparison.Ordinal);
        Assert.Contains("immutable", MenuImageRoutes.ImmutableCacheControl, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuestMenuRendersThePictureAndReadsThemAllAtOnce()
    {
        string page = GuestPage();

        Assert.True(page.Length > 1000, $"{GuestPageRelativePath} is too short to be the guest surface.");

        Assert.Contains("order-menu-thumbnail", page, StringComparison.Ordinal);
        Assert.Contains("MenuImageRoutes.ForImage(", page, StringComparison.Ordinal);

        Assert.Contains("MenuItemPictures.ListAsync(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("FindForItemAsync", page, StringComparison.Ordinal);
    }

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

    [Fact]
    public void TheGuestCardTakesItsAltTextFromTheStoredColumn()
    {
        Assert.Contains("alt=\"@picture.AltText\"", GuestPage(), StringComparison.Ordinal);

        Assert.Contains("class=\"manage-picture-image\"", ItemPage(), StringComparison.Ordinal);
        Assert.Contains("AltTextInput.AltText", ItemPage(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUploadControlIsHandedTheCapAndAPlaceToReport()
    {
        string page = ItemPage();

        Assert.Contains("@attributes=\"PictureBudgetAttributes\"", page, StringComparison.Ordinal);
        Assert.Contains("MenuItemImageUpload.ByteBudgetAttributeName", page, StringComparison.Ordinal);
        Assert.Contains("MenuItemImageUpload.LongestEdgeAttributeName", page, StringComparison.Ordinal);

        Assert.Contains("ReadDeclaredByteCapAsync(", page, StringComparison.Ordinal);

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

        string script = File.ReadAllText(PathUnder(DownscalerRelativePath));

        Assert.Contains(MenuItemImageUpload.ByteBudgetAttributeName, script, StringComparison.Ordinal);
        Assert.Contains(MenuItemImageUpload.LongestEdgeAttributeName, script, StringComparison.Ordinal);
        Assert.Contains(
            "<script src=\"js/menu-picture.js\" defer></script>",
            File.ReadAllText(PathUnder(RootComponentRelativePath)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoFileUnderSourceRestatesTheStoredPictureCap()
    {
        string migration = File.ReadAllText(PathUnder(ImageMigrationRelativePath));

        int marker = migration.IndexOf(ByteCapConstraintName, StringComparison.Ordinal);
        Assert.True(marker >= 0, $"{ImageMigrationRelativePath} no longer declares {ByteCapConstraintName}.");

        int check = migration.IndexOf("CHECK", marker, StringComparison.Ordinal);
        Assert.True(check > marker, $"{ByteCapConstraintName} is declared without a CHECK.");

        int endOfLine = migration.IndexOf('\n', check);
        Assert.True(endOfLine > check, "the cap constraint's CHECK is unterminated.");

        string bound = new string(
            migration[check..endOfLine].Where(char.IsAsciiDigit).ToArray());

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
