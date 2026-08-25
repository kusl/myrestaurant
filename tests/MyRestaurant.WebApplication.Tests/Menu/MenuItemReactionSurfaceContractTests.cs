using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

/// <summary>
/// §11.1's like control, asserted against the markup that renders it and the two rulings that decided its
/// shape (TECHNICAL_SPECIFICATION §7, §9, §11.1, §16.4; Stage 5b of <c>docs/MENU_AND_HANDHELD_PLAN.md</c>).
///
/// <para><b>Why this exists rather than more facts on <c>MenuWiringTests</c>.</b> That file asserts what
/// happens to a call once a surface makes one. Every claim here is about whether the call can be made at
/// all, or about a ruling whose violation is <em>silent</em> — a nested button that a browser quietly
/// splits, a count that reaches the wrong audience, a write that starts announcing itself. None of them
/// fails loudly, and several cannot fail in any suite this repository has: §16.1 rules out bUnit, so
/// nothing renders a component, and the §16.3 barrier that visits the guest surface measures where
/// controls <em>are</em> rather than what they read.</para>
///
/// <para><b>The last two are Stage 5c's and they are about reach rather than placement.</b> §11.1 puts
/// the like in the detail panel and the panel opens only for a chosen item, so a dish whose card is
/// <c>disabled</c> had no panel and therefore no like at all — the consequence Stage 5b-i wrote down
/// instead of repairing. The repair is a second control beside the card, and both of its failure modes
/// are improvements: dropping the card's <c>disabled</c> so one control does both jobs, and disabling
/// Add to basket so a dish that is off cannot be chosen into the basket. Each renders perfectly, each
/// passes every other fact here, and each takes a sentence away from the guest.</para>
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

    private const string AdministrationIndexRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor";

    /// <summary>The marker class the control carries, which every fact below keys on.</summary>
    private const string LikeControlClass = "order-menu-like";

    /// <summary>The card's own class — a <c>&lt;button&gt;</c>, which is the whole of fact two.</summary>
    private const string CardChoiceClass = "order-menu-choice";

    /// <summary>
    /// Stage 5c's second control: the way into the detail panel for a dish §7 will not let the guest
    /// stage. It exists so that the like can be pressed on a dish that is off tonight.
    /// </summary>
    private const string InspectControlClass = "order-menu-inspect";

    /// <summary>
    /// What keeps §7's half of the rule after Stage 5c added a way past the card. The card is the
    /// STAGING control and stays refused; the sibling beside it only opens a panel.
    /// </summary>
    private const string CardDisabledAttribute = "disabled=\"@(!item.IsActive)\"";

    /// <summary>The verb both controls call, which is what makes them open the same panel.</summary>
    private const string ChooseHandler = "ChooseItem(item)";

    /// <summary>
    /// §11.1's staging control. Named by the words on it rather than by a class, because it has none of
    /// its own — it is a <c>.button-secondary</c>, like every other secondary action in the tree.
    /// </summary>
    private const string StagingControlLabel = ">Add to basket<";

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

    /// <summary>
    /// §11.4's index reads the whole-menu count and never one person's presses — the mirror of the fact
    /// above it, and the half that fails <em>plausibly</em>.
    ///
    /// <para><b>The two reads are one keystroke apart and only one of them is about the person
    /// reading.</b> An index calling <c>ListLikedByAsync</c> renders perfectly: every chip on the page
    /// says <c>1 like</c> or is absent, because it is showing the administrator their own opinion
    /// presented as the restaurant's. Nothing throws, no number is malformed, and the page answers a
    /// different question from the one §11.4 asks — <em>which of these is popular</em> against
    /// <em>which of these do I like</em>. Two reads over one fold is the whole design (Stage 5a), and
    /// the failure is that both call sites compile.</para>
    ///
    /// <para><b>Both halves are asserted, because either alone is satisfiable by an index that reads
    /// neither.</b> The prohibition is the interesting one and the requirement is its non-vacuity guard
    /// (F-41) — an index that had lost the read entirely would render a menu with no counts on it and
    /// pass a fact that only forbade.</para>
    ///
    /// <para>The keys carry an open parenthesis for the reason the directory fact's do: this page
    /// explains in a comment which read it must not make, and a gate keyed on the bare identifier would
    /// report a finding on the explanation (F-67).</para>
    /// </summary>
    [Fact]
    public void TheAdministrationIndex_ReadsTheCount_AndNeverOnePersonsPresses()
    {
        string index = File.ReadAllText(PathUnder(AdministrationIndexRelativePath));

        Assert.True(
            index.Contains(WholeMenuCountRead, StringComparison.Ordinal),
            $"§11.4's menu index does not call {WholeMenuCountRead}, so the prohibition below asserts"
            + " nothing. The count is this surface's question (Stage 5a); an index that stopped asking it"
            + " renders a menu with no counts on it and no test would otherwise notice.");

        Assert.False(
            index.Contains(PerPersonRead, StringComparison.Ordinal),
            $"§11.4's menu index calls {PerPersonRead}, which answers about the person reading the page."
            + " Every chip would then say '1 like' or be absent — this administrator's own opinion"
            + " rendered as the restaurant's, on a page that asks which dishes are popular. Nothing"
            + " throws and no number is malformed, which is why this is a test rather than a comment.");
    }

    /// <summary>
    /// A dish that cannot be staged still has a way into the detail panel, the card that refuses it is
    /// still refused, and the way in is beside the card rather than inside it (Stage 5c).
    ///
    /// <para><b>What this closes.</b> §11.1 puts the like in the detail panel, and the panel opens only
    /// for a chosen item — so an item whose card is <c>disabled</c> had no panel and therefore no like.
    /// Stage 5b-i recorded that consequence rather than repairing it, and named the repair: a second path
    /// for items that cannot be staged. <em>The salmon is off tonight and it is still the best thing
    /// here</em> is a real opinion, and this is the markup that lets somebody record it.</para>
    ///
    /// <para><b>Four claims, and the first is the one a later slice would undo without noticing.</b> The
    /// card must still carry <c>disabled</c> bound to <c>!item.IsActive</c>. The tidy repair — drop it,
    /// so one control does both jobs — renders perfectly and passes every other fact in this file: §7's
    /// "cannot be added to a send" would still hold, because <c>OrderStaging.Stage</c> refuses an
    /// inactive item by name and the send transaction re-reads under the lock. What it costs is that a
    /// guest is invited to press Add to basket for a dish the surface already knows is off, and is
    /// answered with a refusal instead of never being offered the choice.</para>
    ///
    /// <para><b>The second is the parser fact for the second time.</b> A <c>&lt;button&gt;</c> inside a
    /// <c>&lt;button&gt;</c> is markup a browser silently splits, so a control placed inside the card
    /// would take the half carrying the dish's name out of the staging path — with nothing thrown and
    /// the Razor compiling. The walk is the one the like control's fact uses, and the structural claim is
    /// stronger than "not inside": the control must sit between the card's <c>&lt;/button&gt;</c> and the
    /// <c>&lt;/li&gt;</c>, which is the only place a sibling can be.</para>
    ///
    /// <para><b>The third is the guard</b> — the region between the card's close and this control must
    /// test <c>!item.IsActive</c>, because a way in rendered beside every card is a second control on a
    /// menu of sixty. <b>And the fourth is that both controls call the same verb</b>, which is what makes
    /// the panel this one opens the panel the like lives in; a control wired to anything else would open
    /// something, and a reader would have to run it to find out what.</para>
    /// </summary>
    [Fact]
    public void AnUnavailableItem_HasAWayIntoTheDetailPanel_BesideItsRefusedCard()
    {
        string page = GuestPage();

        Assert.True(
            page.Contains(CardDisabledAttribute, StringComparison.Ordinal),
            $"§11.1's menu card no longer carries {CardDisabledAttribute}. §7 says a deactivated item"
            + " stays on the menu, marked, and cannot be added to a send — and the card is the staging"
            + " control, so that is where the refusal belongs. Dropping it so that one control both"
            + " stages and opens the panel renders perfectly and is still wrong: the send would be"
            + " refused by OrderStaging.Stage and by the transaction, so the only visible change is that"
            + " a guest is offered a dish the surface already knows is off.");

        int controls = Occurrences(page, $"class=\"{InspectControlClass}\"");

        Assert.True(
            controls == 1,
            $"§11.1 renders exactly one '{InspectControlClass}' control; the markup holds {controls}."
            + " Zero means a dish that is off tonight has no detail panel and therefore no like, which"
            + " is the gap Stage 5c closed; two is what a copy of it into the card would leave behind.");

        int cardOpen = page.IndexOf($"class=\"{CardChoiceClass}\"", StringComparison.Ordinal);
        Assert.True(cardOpen >= 0, $"the guest menu no longer renders a '{CardChoiceClass}' card.");

        int cardClose = page.IndexOf("</button>", cardOpen, StringComparison.Ordinal);
        Assert.True(cardClose > cardOpen, "the card's <button> is unterminated.");

        int listItemClose = page.IndexOf("</li>", cardClose, StringComparison.Ordinal);
        Assert.True(listItemClose > cardClose, "the card's <li> is unterminated.");

        int inspectAt = page.IndexOf($"class=\"{InspectControlClass}\"", StringComparison.Ordinal);

        Assert.True(
            inspectAt > cardClose && inspectAt < listItemClose,
            $"the '{InspectControlClass}' control is not a sibling of the card inside its <li>. Inside"
            + " the card it is a <button> within a <button>, which the HTML parser does not keep: it"
            + " closes the outer element when it meets the inner one, so the card splits in two and the"
            + " half carrying the dish's name stops staging anything. Nothing throws and the Razor"
            + " compiles, which is why this is asserted structurally rather than described.");

        string between = page[(cardClose + "</button>".Length)..inspectAt];

        Assert.True(
            between.Contains("!item.IsActive", StringComparison.Ordinal),
            $"the '{InspectControlClass}' control is not guarded by !item.IsActive, so it renders beside"
            + " every card on the menu. An available dish already has a way into its panel — its card —"
            + " and a second control on sixty cards is sixty controls nobody needed, read from a phone.");

        string control = page[inspectAt..listItemClose];

        Assert.True(
            control.Contains(ChooseHandler, StringComparison.Ordinal),
            $"the '{InspectControlClass}' control does not call {ChooseHandler}. Both controls have to"
            + " open the same panel, because that panel is where §11.1 puts the like — and this one"
            + " exists so a dish that cannot be staged can still be liked.");
    }

    /// <summary>
    /// §11.1's staging control is never disabled, because the refusal it would be hiding is a sentence
    /// somebody needs to read (Stage 5c).
    ///
    /// <para><b>Why this became worth asserting now.</b> Before Stage 5c the chosen item was always one
    /// the card had allowed, so the question never arose. Now a guest can open the panel for a dish that
    /// is off, and the obvious next tidy-up is to disable Add to basket while that item is chosen. It
    /// would look considerate and it costs two things. The guest gets a dead control and no reason —
    /// where <c>OrderStaging.Stage</c> answers "<em>Grilled salmon is currently unavailable</em>",
    /// naming the dish — and the component acquires a second opinion about availability alongside the
    /// staging area's, which is F-65's mechanism: one rule in two places, and the two drift.</para>
    ///
    /// <para><b>The Send button one region down legitimately does the opposite</b>, and the distinction
    /// is the reason this fact names its subject by the words on it. §11.1 requires Send to be disabled
    /// while the basket is empty — there is nothing to refuse and nothing to explain, so the control has
    /// no sentence to withhold. Add to basket always has one.</para>
    ///
    /// <para>Non-vacuity is the label itself (F-41): a fact about the attributes of a button that is not
    /// there passes beautifully.</para>
    /// </summary>
    [Fact]
    public void TheStagingControl_IsNeverDisabled_BecauseItsRefusalIsASentence()
    {
        string page = GuestPage();

        int label = page.IndexOf(StagingControlLabel, StringComparison.Ordinal);

        Assert.True(
            label >= 0,
            $"§11.1 no longer renders a control reading '{StagingControlLabel.Trim('>', '<')}', so the"
            + " claim below is asserting nothing. That button is how a chosen item reaches the basket.");

        int opening = page.LastIndexOf("<button", label, StringComparison.Ordinal);

        Assert.True(opening >= 0, "the staging control's label is not inside a <button>.");

        string tag = page[opening..label];

        Assert.False(
            tag.Contains("disabled", StringComparison.Ordinal),
            "§11.1's Add to basket control carries a disabled attribute. The refusal it would be hiding"
            + " is OrderStaging.Stage's, which names the dish and says why — and since Stage 5c a guest"
            + " can choose a dish that is off, so this is exactly the control somebody will reach for."
            + " Two costs: a dead button with no reason on it, and a second authority on availability"
            + " inside a component whose staging area already holds one (F-65). The Send button is"
            + " disabled while the basket is empty and that is not the same case — an empty basket has"
            + " no refusal to explain.");
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
