using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

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

    private const string LikeControlClass = "order-menu-like";

    private const string CardChoiceClass = "order-menu-choice";

    private const string InspectControlClass = "order-menu-inspect";

    private const string CardDisabledAttribute = "disabled=\"@(!item.IsActive)\"";

    private const string ChooseHandler = "ChooseItem(item)";

    private const string StagingControlLabel = ">Add to basket<";

    private const string PerPersonRead = "ListLikedByAsync(";

    private const string WholeMenuCountRead = "ListLikeCountsAsync(";

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

        Assert.Contains("aria-pressed=", page, StringComparison.Ordinal);
    }

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
