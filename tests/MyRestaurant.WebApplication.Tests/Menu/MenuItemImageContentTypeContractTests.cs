using System.Text.RegularExpressions;
using MyRestaurant.Domain.Menu;
using MyRestaurant.WebApplication.Menu;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

public sealed class MenuItemImageContentTypeContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string SourceRelativePath = "src";

    private const string ItemPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor";

    private const string IdentifierName = "ImageFormat.IdentifyContentType";

    private const string AttachCall = "MenuChanges.AttachMenuItemImageAsync(";

    private static readonly Regex FormFileBinding =
        new(@"IFormFile(?![A-Za-z0-9_])\s*\??\s+([A-Za-z_][A-Za-z0-9_]*)");

    private static readonly Regex IdentifiedLocal =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*ImageFormat\.IdentifyContentType\(");

    [Fact]
    public void NoSurfaceReadsTheMediaTypeTheBrowserDeclared()
    {
        List<string> readers = [];
        List<string> bindingFiles = [];
        List<string> bindings = [];
        int filesScanned = 0;

        foreach (string file in SourceFiles())
        {
            filesScanned++;

            string source = WithoutComments(File.ReadAllText(file));

            string[] names = FormFileBinding.Matches(source)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (names.Length == 0)
            {
                continue;
            }

            bindingFiles.Add(Relative(file));
            bindings.AddRange(names);

            foreach (string name in names)
            {
                if (source.Contains($"{name}.ContentType", StringComparison.Ordinal)
                    || source.Contains($"{name}?.ContentType", StringComparison.Ordinal))
                {
                    readers.Add($"{Relative(file)} reads {name}.ContentType");
                }
            }
        }

        Assert.True(
            filesScanned >= 20,
            $"Only {filesScanned} file(s) under {SourceRelativePath}/ were scanned, so an emptiness"
                + " assertion over them is not about this tree (F-41).");

        Assert.True(
            bindingFiles.Count >= 1,
            $"No file under {SourceRelativePath}/ binds an IFormFile, so this gate judged nothing. The"
                + " picture upload is the one place in this application that reads a posted file; if it"
                + " has moved, move this subject with it rather than leaving a walk that passes by"
                + " finding nothing (F-41).");

        Assert.True(
            bindings.Count >= 1,
            $"{bindingFiles.Count} file(s) mention IFormFile and no binding could be named in any of"
                + " them, so the pattern below matched nothing to assert about.");

        Assert.True(
            readers.Count == 0,
            $"{readers.Count} upload surface(s) read the media type the browser declared:"
                + $" {string.Join("; ", readers)}. IFormFile.ContentType is not a fact about the file —"
                + " it is whatever the operating system's extension map produced for the chosen"
                + " filename, so a genuine PNG arrives as application/octet-stream from any system with"
                + " no mapping and is refused for a format that is not its own (F-109). Ask"
                + $" {IdentifierName} what the bytes are and pass that; the stored column may then be"
                + " served back as a response header, which is the whole reason §7 checks it at all.");
    }

    [Fact]
    public void TheUploadSurfacePassesWhatTheBytesWereIdentifiedAs()
    {
        string page = WithoutComments(File.ReadAllText(PathUnder(ItemPageRelativePath)));

        Match assignment = IdentifiedLocal.Match(page);

        Assert.True(
            assignment.Success,
            $"{ItemPageRelativePath} never calls {IdentifierName}, so nothing decides the media type"
                + " the picture is stored under from the picture itself (F-109).");

        string local = assignment.Groups[1].Value;

        int call = page.IndexOf(AttachCall, StringComparison.Ordinal);

        Assert.True(
            call >= 0,
            $"{ItemPageRelativePath} no longer calls {AttachCall} so the argument this fact is about"
                + " cannot be found. If the call was renamed, rename it here too rather than leaving a"
                + " gate that decides nothing.");

        int close = page.IndexOf(");", call, StringComparison.Ordinal);

        Assert.True(
            close > call,
            $"the call to {AttachCall} in {ItemPageRelativePath} is unterminated.");

        string[] arguments = page[(call + AttachCall.Length)..close]
            .Split(',')
            .Select(argument => argument.Trim())
            .ToArray();

        Assert.True(
            arguments.Contains(local, StringComparer.Ordinal),
            $"{ItemPageRelativePath} assigns {IdentifierName}'s answer to '{local}' and then does not"
                + $" pass it: the attach call's arguments are [{string.Join(" | ", arguments)}]. The"
                + " identified type is the whole of Stage 4f — a surface that computes it and hands the"
                + " write something else has the defect back with a dead line above it.");
    }

    [Fact]
    public void TheUploadSurfaceSpellsNoFormatNameAndRendersTheDerivedCensus()
    {
        string[] forbidden = ImageFormat.RecognisedContentTypes
            .Select(type => type[(type.IndexOf('/') + 1)..])
            .Where(word => word.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            forbidden.Length >= 3,
            $"Only {forbidden.Length} format name(s) could be derived from"
                + " ImageFormat.RecognisedContentTypes, so this scan is looking for almost nothing"
                + " (F-41).");

        string page = WithoutComments(File.ReadAllText(PathUnder(ItemPageRelativePath)));

        Assert.True(
            page.Length >= 1000,
            $"{ItemPageRelativePath} is {page.Length} characters once comments are stripped, which is"
                + " not a page — the stripper has eaten the file and every assertion below would pass"
                + " on nothing (F-41).");

        string[] spelled = forbidden
            .Where(word => page.Contains(word, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            spelled.Length == 0,
            $"{ItemPageRelativePath} spells {spelled.Length} picture format name(s) out:"
                + $" {string.Join(", ", spelled)}. That is the media-type vocabulary's fifth"
                + " declaration, after §8.2's CHECK, ImageFormat.RecognisedContentTypes and the derived"
                + " accept attribute — and it is the copy with no symptom at all when it drifts, because"
                + " prose that lists three formats on a menu that stores four is simply read and"
                + $" believed (F-80, F-110). Render {nameof(MenuItemImageUpload)}."
                + $"{nameof(MenuItemImageUpload.RecognisedTypesForOperators)} instead.");

        Assert.True(
            page.Contains(nameof(MenuItemImageUpload.RecognisedTypesForOperators), StringComparison.Ordinal),
            $"{ItemPageRelativePath} names no format and also never renders"
                + $" {nameof(MenuItemImageUpload.RecognisedTypesForOperators)}, so the operator is told"
                + " their file was refused and not told what would be accepted. The emptiness above is"
                + " satisfied by saying nothing, which is why this half is here.");
    }

    private static IEnumerable<string> SourceFiles()
        => Directory
            .EnumerateFiles(PathUnder(SourceRelativePath), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string WithoutComments(string source)
    {
        string withoutRazorComments = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);

        return Regex.Replace(withoutRazorComments, @"(?<!:)//[^\n]*", " ");
    }

    private static string Relative(string path)
        => Path.GetRelativePath(FindRepositoryRoot().FullName, path).Replace(Path.DirectorySeparatorChar, '/');

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
