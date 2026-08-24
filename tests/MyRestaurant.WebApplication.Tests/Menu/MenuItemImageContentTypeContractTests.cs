using System.Text.RegularExpressions;
using MyRestaurant.Domain.Menu;
using MyRestaurant.WebApplication.Menu;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

/// <summary>
/// What decides the media type a picture is stored under, and where the vocabulary of media types may be
/// written (TECHNICAL_SPECIFICATION §7, §11.4, §16.4; Stage 4f).
///
/// <para><b>Why this is its own class rather than three more facts on
/// <c>MenuItemImageSurfaceContractTests</c>.</b> That class is at twelve, and the tree has an opinion
/// about twelve: <c>TestingSectionContractTests.NumberWords</c> stops there, and says in its own summary
/// that a contract test with more than twelve assertions is a contract test that has become two. Adding
/// here rather than there is that rule obeyed rather than routed around — and the split is honest on its
/// own terms, because the two classes ask different questions. That one asks whether an upload can
/// <em>reach</em> the write service. This one asks what the write service is <em>told</em> when it does.
/// </para>
///
/// <para><b>The defect these exist for was open for six slices and was reported as a refusal nobody could
/// explain (F-109).</b> The multipart handler passed <c>IFormFile.ContentType</c> — which is not a fact
/// about the file at all. It is whatever the operating system's extension map produced for the chosen
/// filename, so a Linux desktop with no <c>shared-mime-info</c>, an Android browser handed a file from a
/// document provider, and any file saved without an extension all send <c>application/octet-stream</c>
/// for a genuine PNG. §8.2's census does not admit that string, so the write answered
/// <c>UnsupportedContentType</c> and the operator read a sentence blaming the format of a file whose
/// format was fine. **The fix was written down in Slice 52 and deferred four times**, on the objection
/// that a surface identifying the format itself would leave two of the write's outcomes unreachable from
/// the only form that can produce them. What settles it is that the surface passes the answer of
/// <em>the same pure function the write consults</em>: one decision procedure called twice, not two that
/// can disagree, and both outcomes still reachable at the write's own boundary where
/// <c>MenuItemImageTests</c> reaches them directly.
///
/// <para><b>The first fact computes its own subject (F-47/F-58).</b> It does not name
/// <c>ManageMenuItem.razor</c>: it finds every file under <c>src/</c> that binds an <c>IFormFile</c>,
/// captures what each binding is called, and requires that none of them has <c>.ContentType</c> read off
/// it. Today that is one file and one binding. The point is the day it is two — a second upload surface
/// acquires this rule by existing, rather than by somebody remembering to add it here.</para>
///
/// <para><b>The third is F-80's shape in a fifth place (F-110).</b> The media-type vocabulary is
/// declared in §8.2's CHECK, in <see cref="ImageFormat.RecognisedContentTypes"/> and — derived from that
/// — in <see cref="MenuItemImageUpload.AcceptAttribute"/>. The upload surface had two more copies, both
/// in English and neither reachable by anything: the explanatory paragraph above the form said
/// <em>"JPEG, PNG or WebP"</em>, and the refusal an operator reads said <em>"Choose a JPEG, a PNG or a
/// WebP"</em>. Both now render <see cref="MenuItemImageUpload.RecognisedTypesForOperators"/>, and this
/// fact keeps it that way with the forbidden words <b>derived from the census</b> rather than listed —
/// so a fourth format admitted by a future migration is a word this gate starts looking for on its
/// own.</para>
///
/// <para><b>Comments are stripped before every scan, on the standard every source walk in this suite
/// applies (F-67)</b> — and here it is load-bearing rather than tidy: the paragraph in
/// <c>ManageMenuItem.razor</c> that <em>explains</em> F-109 necessarily contains both
/// <c>IFormFile.ContentType</c> and the word PNG, so a gate that read comments would report a finding on
/// the very file it had just been satisfied by.</para>
/// </summary>
public sealed class MenuItemImageContentTypeContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string SourceRelativePath = "src";

    private const string ItemPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor";

    /// <summary>The identifier the write's answer must be handed through.</summary>
    private const string IdentifierName = "ImageFormat.IdentifyContentType";

    /// <summary>The write the surface calls, named so the argument walk has one call to find.</summary>
    private const string AttachCall = "MenuChanges.AttachMenuItemImageAsync(";

    /// <summary>
    /// A binding of an <c>IFormFile</c>, in either position it can occupy: a local declaration and a
    /// parameter. The negative lookahead is what keeps <c>IFormFileCollection</c> out — that type's
    /// bindings are collections and reading a content type off one is not possible, so a gate that
    /// matched it would be reaching past what it can decide (F-41).
    /// </summary>
    private static readonly Regex FormFileBinding =
        new(@"IFormFile(?![A-Za-z0-9_])\s*\??\s+([A-Za-z_][A-Za-z0-9_]*)");

    /// <summary>The local the identified media type is assigned to, captured rather than assumed.</summary>
    private static readonly Regex IdentifiedLocal =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*ImageFormat\.IdentifyContentType\(");

    /// <summary>
    /// <b>No surface reads the media type the browser declared.</b> Every file under <c>src/</c> that
    /// binds an <c>IFormFile</c> is found, every binding in it is named, and none may have
    /// <c>.ContentType</c> read off it.
    ///
    /// <para><b>Scoped to the binding rather than to the string, which is what makes it precise.</b> A
    /// blanket ban on <c>.ContentType</c> in these files would fail on a correct tree: §11.4's own panel
    /// renders <c>_picture.ContentType</c>, which is the <em>stored</em> column and is exactly right to
    /// render. What is wrong is reading the claim off the upload, so that is what is asserted —
    /// F-41's rule that a gate must not report a finding on a correct file.</para>
    ///
    /// <para>Three non-vacuity guards, and each covers a different way this could become an assertion
    /// about nothing: enough files scanned that <c>src/</c> was really walked, at least one file that
    /// binds an upload at all, and at least one binding named inside it. A tree where the upload form
    /// was deleted should fail here loudly rather than pass silently.</para>
    /// </summary>
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

    /// <summary>
    /// <b>The upload surface hands the write what the bytes were identified as.</b> The local is
    /// captured from the assignment rather than assumed, and then required to be one of the arguments
    /// the attach call is given.
    ///
    /// <para><b>The positive half of the fact above, and it is needed because emptiness is cheap.</b>
    /// Deleting the argument, passing a literal, or passing <c>string.Empty</c> unconditionally all
    /// satisfy "does not read the browser's declaration" while making every upload fail — which is a
    /// worse product than the defect this stage repaired. So the two facts are a pair: one says what
    /// must not be passed, this one says what must.</para>
    /// </summary>
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

    /// <summary>
    /// <b>The upload surface names no picture format, anywhere a person can read.</b> The forbidden
    /// words are derived from <see cref="ImageFormat.RecognisedContentTypes"/> — the segment after the
    /// slash — so the set this gate looks for widens with the census rather than with somebody's memory
    /// of it.
    ///
    /// <para><b>Whole file rather than the refusal alone, and that is deliberate.</b> Two copies of the
    /// vocabulary were on this page and only one of them was a refusal: the paragraph above the form
    /// said <em>"JPEG, PNG or WebP"</em>, which is prose an operator reads and which no gate had ever
    /// looked at. Scoping to the <c>switch</c> would have caught the message and left the paragraph,
    /// which is F-46's mechanism — a rule enforced against the example that prompted it.</para>
    ///
    /// <para><b>The anti-evasion half matters more than the emptiness half here</b>, because the
    /// cheapest way to stop naming formats is to stop telling the operator which ones are accepted, and
    /// a refusal that does not say what would have worked is a refusal that sends somebody back to the
    /// same file. So the refusal arm is additionally required to render the derived list.</para>
    /// </summary>
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

    /// <summary>
    /// Every authored file under <c>src/</c>, build output excluded. Enumerated rather than listed, for
    /// the reason the first fact exists at all.
    /// </summary>
    private static IEnumerable<string> SourceFiles()
        => Directory
            .EnumerateFiles(PathUnder(SourceRelativePath), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Razor comments and C# line comments removed, which covers documentation comments too since
    /// <c>///</c> begins with <c>//</c>.
    ///
    /// <para>Line comments are cut only where the two slashes are not preceded by a colon, so a URL in a
    /// string literal survives. No file in this subject contains one today; the guard is here because
    /// the failure it prevents is silent — an over-eager stripper removes the rest of a line and the
    /// scan stops being able to see anything on it.</para>
    /// </summary>
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
