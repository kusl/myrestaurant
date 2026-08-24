using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

/// <summary>
/// §11.1's like control, asserted against the markup that renders it and the two rulings that decided its
/// shape (TECHNICAL_SPECIFICATION §7, §9, §11.1, §16.4; Stage 5b of <c>docs/MENU_AND_HANDHELD_PLAN.md</c>).
///
/// <para><b>Why this exists rather than more facts on <c>MenuWiringTests</c>.</b> That file asserts what
/// happens to a call once a surface makes one. Every claim here is about whether the call can be made at
/// all, or about a ruling whose violation is <em>silent</em> — a nested button that a browser quietly
/// splits, a count that reaches the wrong audience, a write that starts announcing itself. None of the
/// four fails loudly, and two of them cannot fail in any suite this repository has: §16.1 rules out
/// bUnit, so nothing renders a component, and the §16.3 barrier that visits the guest surface measures
/// where controls <em>are</em> rather than what they read.</para>
///
/// <para><b>Two of the four hold a ruling rather than a mechanism, and that is deliberate.</b> Stage 5a
/// decided that the like <em>count</em> is staff-facing and that a reaction publishes nothing, and wrote
/// both down in §7 — where a later slice can improve them away in one line each, by adding a span to a
/// card or a forwarding verb to a workflow. A sentence in a specification is not a thing that refuses; a
/// test is. <b>F-38's lesson is the standard being applied</b>: a ruling worth making is a ruling worth
/// making executable.</para>
///
/// <para><b>Each fact computes its own subject where it can</b> (F-47, F-58). The count fact walks
/// <c>Components/Pages/Table/</c> rather than naming one file, because the ruling is about the guest's
/// half of the application and not about one component that happens to hold the menu today.</para>
///
/// <para><b>It reads source text rather than rendering anything</b>, for
/// <c>MenuItemImageSurfaceContractTests</c>' reason exactly: the properties under test are properties of
/// the markup and of one file's imports, and a renderer would need a container and a database to assert a
/// string.</para>
///
/// <para>Pure: reads files off the disk it was built from. No server, no container, no browser.</para>
/// </summary>
public sealed class MenuItemReactionSurfaceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string GuestPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor";

    private const string GuestPagesRelativeDirectory =
        "src/MyRestaurant.WebApplication/Components/Pages/Table";

    private const string WorkflowRelativePath =
        "src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs";

    /// <summary>The marker class the control carries, which every fact below keys on.</summary>
    private const string LikeControlClass = "order-menu-like";

    /// <summary>The card's own class — a <c>&lt;button&gt;</c>, which is the whole of fact two.</summary>
    private const string CardChoiceClass = "order-menu-choice";

    /// <summary>The read §11.1 is entitled to: one person's own presses.</summary>
    private const string PerPersonRead = "ListLikedByAsync(";

    /// <summary>The read §11.4 owns and §11.1 must never make (Stage 5a).</summary>
    private const string WholeMenuCountRead = "ListLikeCountsAsync(";

    /// <summary>
    /// The guest surface resolves both halves of the feature and renders exactly one like control.
    ///
    /// <para><b>This is the non-vacuity guard for the three facts below it</b> (F-41). Each of those is
    /// an <em>absence</em> assertion of some kind — the control is not inside the card, the count is not
    /// read, the workflow does not reach the write — and every one of them passes trivially against a
    /// surface that has no like control at all. So the presence is established first, and it is
    /// established as an exact count rather than as a containment: two controls would satisfy a
    /// <c>Contains</c> and would mean the panel had grown a second heart, which is the shape a
    /// copy-paste into the card would leave behind.</para>
    ///
    /// <para>The two services are asserted by their <c>@inject</c> lines rather than by the registration,
    /// which <c>MenuWiringTests</c> owns: an unresolved injection on an interactive island is not a
    /// compile error, it is an exception when the circuit starts.</para>
    /// </summary>
    [Fact]
    public void TheGuestSurface_TakesBothReactionServices_AndRendersExactlyOneLikeControl()
    {
        string page = GuestPage();

        Assert.Contains("@inject IMenuItemReactionDirectory", page, StringComparison.Ordinal);
        Assert.Contains("@inject IMenuItemReactions ", page, StringComparison.Ordinal);

        int controls = Occurrences(page, $"class=\"{LikeControlClass} ");

        Assert.True(
            controls == 1,
            $"§11.1 renders exactly one like control; the markup holds {controls}. A second one is what a"
            + " copy of this element into the card would leave behind, and the card is the one place it"
            + " must not be (see the nesting fact).");

        // The state is carried by aria-pressed rather than by the label, so that a screen reader
        // announces it once. A control that lost this attribute would look identical on every screen.
        Assert.Contains("aria-pressed=", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The like control is not inside the card's <c>&lt;button&gt;</c>, and this is the only fact in the
    /// file about a defect a browser would <em>hide</em>.
    ///
    /// <para><b>The mechanism, because it is not guessable from the source.</b> §11.1's card is itself a
    /// <c>&lt;button&gt;</c> — it is what stages a dish. The HTML parser does not permit a button inside
    /// a button and does not report the attempt: it closes the outer element when it meets the inner one,
    /// so the card silently becomes two elements, and the half holding the dish's name and price — the
    /// half a guest taps — stops being a control at all. Nothing throws, no C# is wrong, and the Razor
    /// compiles. The plan ruled the control into the detail panel before the markup was written; this is
    /// that ruling made refusable.</para>
    ///
    /// <para>The walk takes the card's opening tag to the first <c>&lt;/button&gt;</c> after it, which is
    /// its closing tag: the card holds spans and an <c>&lt;img&gt;</c> and no nested control, and if it
    /// ever held one this fact is the thing that should fail.</para>
    /// </summary>
    [Fact]
    public void TheLikeControl_IsNotNestedInsideTheCardButton()
    {
        string page = GuestPage();

        int cardOpen = page.IndexOf($"class=\"{CardChoiceClass}\"", StringComparison.Ordinal);
        Assert.True(cardOpen >= 0, $"the guest menu no longer renders a '{CardChoiceClass}' card.");

        int cardClose = page.IndexOf("</button>", cardOpen, StringComparison.Ordinal);
        Assert.True(cardClose > cardOpen, "the card's <button> is unterminated.");

        string card = page[cardOpen..cardClose];

        Assert.False(
            card.Contains(LikeControlClass, StringComparison.Ordinal),
            "the like control is inside the card's <button>. A browser will not keep that markup: the"
            + " parser closes the outer button when it meets the inner one, so the card splits in two and"
            + " the half carrying the dish's name stops staging anything. The control belongs in the"
            + " detail panel (§11.1).");
    }

    /// <summary>
    /// No surface a guest can open reads the whole-menu like count, and at least one of them reads their
    /// own presses.
    ///
    /// <para><b>Stage 5a's first ruling, made executable.</b> The count is staff-facing: a 3 beside a
    /// dish on a menu of sixty is noise that makes a restaurant look empty, and the number's honest
    /// audience is the person deciding what to stock. So <c>ListLikeCountsAsync</c> is §11.4's read and
    /// <c>ListLikedByAsync</c> is §11.1's — two reads over one fold, rather than one read handing every
    /// guest the restaurant's opinion. Nothing but this fact stands between that ruling and a slice that
    /// renders the number because it was there.</para>
    ///
    /// <para><b>The subject is the directory, not a file</b> (F-47). The ruling is about the guest's half
    /// of the application; a fact naming <c>TableOrderSurface.razor</c> would say nothing about the
    /// history page beside it, or about whatever surface holds the menu after the next re-layout.</para>
    ///
    /// <para><b>The positive half is the non-vacuity guard</b> (F-41): a directory in which neither read
    /// appears would satisfy the prohibition while proving that the walk found nothing worth walking.</para>
    ///
    /// <para><b>Both keys carry an open parenthesis, and that is the difference between a use and a
    /// mention</b> (F-67's shape). The rule is that a guest surface must not <em>call</em> the count read;
    /// the file that must not call it is also the natural place to write down <em>why</em>, and this class
    /// would otherwise report a finding on a component whose only offence was explaining the ruling it
    /// obeys. A call cannot omit its parentheses, so the narrower key is the accurate one rather than the
    /// lenient one.</para>
    /// </summary>
    [Fact]
    public void TheGuestSurfaces_ReadTheirOwnPresses_AndNeverTheCount()
    {
        string[] pages = Directory
            .GetFiles(PathUnder(GuestPagesRelativeDirectory), "*.razor", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            pages.Length > 0,
            $"no Razor components under {GuestPagesRelativeDirectory}; the walk has lost its subject.");

        List<string> readTheCount = [];
        bool anyReadsTheirOwn = false;

        foreach (string path in pages)
        {
            string text = File.ReadAllText(path);

            if (text.Contains(WholeMenuCountRead, StringComparison.Ordinal))
            {
                readTheCount.Add(Path.GetFileName(path));
            }

            anyReadsTheirOwn |= text.Contains(PerPersonRead, StringComparison.Ordinal);
        }

        Assert.True(
            anyReadsTheirOwn,
            $"no surface under {GuestPagesRelativeDirectory} calls {PerPersonRead}, so the prohibition"
            + " below is asserting nothing. §11.1 renders a guest's own presses; a walk that found"
            + " neither read is a walk over the wrong directory.");

        Assert.True(
            readTheCount.Count == 0,
            $"{string.Join(", ", readTheCount)} calls {WholeMenuCountRead}. That read is §11.4's: Stage 5a"
            + " ruled the like count staff-facing, because a count of three on a menu of sixty makes a"
            + " restaurant look empty and the number's audience is whoever decides what to stock. A guest"
            + " gets their own press back and nobody else's.");
    }

    /// <summary>
    /// The workflow never reaches the reaction write, which is Stage 5a's second ruling and the one with
    /// the most expensive violation.
    ///
    /// <para><b>What a forwarding verb would cost.</b> <c>MenuWorkflow</c> exists so that a change to the menu is announced (§9), and <c>MenuChanged</c> means
    /// <em>re-read the menu</em> and nothing else. A like moves no name, no price, no heading, no
    /// position, no availability flag and no photograph — and it is the one write in this application
    /// that can fire many times a minute at one table. A verb added here would make one thumb re-read the
    /// entire menu on every phone, every kitchen board and every display in the building, and the symptom
    /// would be load rather than an error: nothing fails, nothing logs, and the tree stays green.</para>
    ///
    /// <para><b>The obvious tidy-up is exactly the defect.</b> Every other menu write is behind that
    /// interface, so a reader meeting this one outside it will read the asymmetry as an omission. The
    /// asymmetry is the design, §7 says so in a paragraph, and this is the thing that refuses.</para>
    ///
    /// <para>Non-vacuity is the workflow's own vocabulary (F-41): a fact asserting that a file does not
    /// mention something passes beautifully against a file that has been renamed away.</para>
    /// </summary>
    [Fact]
    public void TheMenuWorkflow_NeverReachesTheReactionWrite()
    {
        string workflow = File.ReadAllText(PathUnder(WorkflowRelativePath));

        Assert.Contains("IMenuWorkflow", workflow, StringComparison.Ordinal);
        Assert.Contains("MenuChanged", workflow, StringComparison.Ordinal);

        foreach (string forbidden in new[] { "IMenuItemReactions", "SetLikedAsync" })
        {
            Assert.False(
                workflow.Contains(forbidden, StringComparison.Ordinal),
                $"MenuWorkflow mentions {forbidden}. A reaction publishes nothing (§7, §9): it changes"
                + " nothing a picker renders, and it is the one write here that can fire many times a"
                + " minute at one table — so a verb behind the workflow would make a heart-tap re-read"
                + " the whole menu on every phone in the building. The symptom is load, not an error.");
        }
    }

    private static string GuestPage() => File.ReadAllText(PathUnder(GuestPageRelativePath));

    /// <summary>
    /// How many times one string occurs, without overlaps. <c>Contains</c> is not enough for the count
    /// fact above: the claim is that there is exactly one control, and a second is what the defect this
    /// class exists to refuse would leave behind.
    /// </summary>
    private static int Occurrences(string text, string value)
    {
        int found = 0;

        for (int at = text.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
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
