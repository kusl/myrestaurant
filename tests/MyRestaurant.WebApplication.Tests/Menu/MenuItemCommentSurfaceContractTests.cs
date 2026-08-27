using System.Text.RegularExpressions;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Menu;

public sealed class MenuItemCommentSurfaceContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";

    private const string GuestPageRelativePath =
        "src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor";

    private const string WorkflowRelativePath =
        "src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs";

    private const string MigrationRelativePath =
        "src/MyRestaurant.DataAccess/Migrations/0009_menu_item_comments.sql";

    private const string CapConstraintName = "menu_item_comment_event_body_within_cap";

    private const string CapRead = "ReadDeclaredBodyCapAsync(";

    private const string PerPersonRead = "ListForPersonAsync(";

    private const string WholeMenuRead = "ListAsync(";

    private const string BoxClass = "order-menu-comment-body";

    private const string SaveControlClass = "order-menu-comment-save";

    private const string WithdrawControlClass = "order-menu-comment-withdraw";

    private const string PanelClass = "order-menu-detail";

    private const string CardChoiceClass = "order-menu-choice";

    private const string BlankOutcome = "SubmitMenuItemCommentOutcome.BodyBlank";

    private const string WithdrawCall = "WithdrawAsync(";

    private const string SaveHandlerSignature = "private async Task SaveCommentAsync(";

    private static readonly Regex InjectedCommentDirectory =
        new(@"@inject\s+IMenuItemCommentDirectory\s+(\w+)");

    private static readonly Regex DeclaredCap =
        new($@"{CapConstraintName}[\s\S]*?<=\s*([0-9]+)", RegexOptions.Singleline);

    private static readonly Regex CommentBoxTag =
        new($@"<textarea[^>]*{BoxClass}[^>]*>", RegexOptions.Singleline);

    private static readonly Regex MaximumLengthAttribute = new(@"maxlength=""([^""]*)""");

    [Fact]
    public void TheGuestSurface_TakesBothCommentServices_AndRendersExactlyOneCommentBox()
    {
        string page = GuestPage();

        Assert.Contains("@inject IMenuItemCommentDirectory", page, StringComparison.Ordinal);
        Assert.Contains("@inject IMenuItemComments ", page, StringComparison.Ordinal);

        int boxes = Occurrences(page, $"class=\"{BoxClass}\"");

        Assert.True(
            boxes == 1,
            $"§11.1 renders exactly one comment box; the markup holds {boxes}. Two is what a copy of"
            + " the block onto the card leaves behind, and the card is the one place it must not be"
            + " (see the nesting fact). Zero is Stage 6d unbuilt, and every prohibition below would"
            + " then be asserting nothing.");

        int save = Occurrences(page, $"class=\"{SaveControlClass}\"");
        int withdraw = Occurrences(page, $"class=\"{WithdrawControlClass}\"");

        Assert.True(
            save == 1 && withdraw == 1,
            $"§11.1 renders one save control and one withdraw control; the markup holds {save} and"
            + $" {withdraw}. They are separate verbs because §7 makes them separate events, and a"
            + " surface with one control cannot express both.");
    }

    [Fact]
    public void TheCommentBox_SitsInsideTheDetailPanel_AndNotInsideTheCardButton()
    {
        string page = GuestPage();

        int cardOpen = page.IndexOf($"class=\"{CardChoiceClass}\"", StringComparison.Ordinal);
        Assert.True(cardOpen >= 0, $"the guest menu no longer renders a '{CardChoiceClass}' card.");

        int cardClose = page.IndexOf("</button>", cardOpen, StringComparison.Ordinal);
        Assert.True(cardClose > cardOpen, "the card's <button> is unterminated.");

        string card = page[cardOpen..cardClose];

        Assert.False(
            card.Contains(BoxClass, StringComparison.Ordinal),
            "the comment box is inside the card's <button>. A browser will not keep that markup — a"
            + " <textarea> is interactive content and a button may hold none, so the parser lifts it"
            + " out and the card splits — and even where it rendered, a box on sixty cards is sixty"
            + " boxes read from a phone. §11.1 puts it in the detail panel, beside the like, on the"
            + " reasoning that put the like there.");

        int panel = page.IndexOf($"class=\"{PanelClass}\"", StringComparison.Ordinal);
        Assert.True(panel >= 0, $"the guest menu no longer renders a '{PanelClass}' panel.");

        int box = page.IndexOf($"class=\"{BoxClass}\"", StringComparison.Ordinal);

        Assert.True(
            box > panel,
            "the comment box is rendered before §11.1's detail panel opens, so it is not inside it."
            + " The panel is the only part of this surface that exists per chosen dish; a box outside"
            + " it belongs to no dish and would save the same words against whichever one was picked"
            + " last.");
    }

    [Fact]
    public void TheGuestSurface_ReadsItsOwnComments_AndNeverTheStaffFacingRead()
    {
        string page = GuestPage();

        Match injected = InjectedCommentDirectory.Match(page);

        Assert.True(
            injected.Success,
            $"§11.1's surface does not inject IMenuItemCommentDirectory, so the two markers below"
            + " have no receiver to qualify and the prohibition cannot be written. This fact keys on"
            + " the identifier the page gave the service rather than on a bare method name, and the"
            + " reason is that ListAsync( is declared by three of the menu directories, two of which"
            + " this component reads — a marker without the receiver would forbid the menu read and"
            + " the picture read that §11.1 requires.");

        string receiver = injected.Groups[1].Value;

        Assert.True(
            page.Contains($"{receiver}.{PerPersonRead}", StringComparison.Ordinal),
            $"§11.1's surface never calls {receiver}.{PerPersonRead}. §7 renders a guest their own"
            + " standing comment and nobody else's, so this read is the whole of the guest's half of"
            + " Stage 6d — and without it the prohibition below is asserting nothing.");

        Assert.False(
            page.Contains($"{receiver}.{WholeMenuRead}", StringComparison.Ordinal),
            $"§11.1's surface calls {receiver}.{WholeMenuRead}, which answers with every comment"
            + " every guest has left. §7 rules a comment staff-facing and that ruling is what makes"
            + " the moderation question not arise: nothing a guest writes is rendered to another"
            + " guest, so there is nobody to moderate on behalf of. This read would render one"
            + " guest's words to another, and the plan's fourth Stage 6 row is what has to be"
            + " reopened before any surface does that.");
    }

    [Fact]
    public void TheBodyCapIsTheSchemasAndTheSurfaceAsksTheDirectoryForIt()
    {
        string page = GuestPage();

        Match declared = DeclaredCap.Match(Migration());

        Assert.True(
            declared.Success,
            $"{MigrationRelativePath} no longer declares a bound in {CapConstraintName}, so there is"
            + " no number for this fact to look for. §7 says the cap is the schema's and is stated"
            + " once; the constraint is that one place.");

        Assert.True(
            page.Contains(CapRead, StringComparison.Ordinal),
            $"§11.1's surface never calls {CapRead}, so whatever `maxlength` it renders is a second"
            + " copy of §8.2's bound rather than the bound itself (F-107).");

        Match box = CommentBoxTag.Match(page);

        Assert.True(box.Success, $"the comment box is no longer a <textarea> carrying '{BoxClass}'.");

        Match attribute = MaximumLengthAttribute.Match(box.Value);

        Assert.True(
            attribute.Success && attribute.Groups[1].Value.StartsWith('@'),
            "the comment box's maxlength is not an expression. The client's cap is an optimisation"
            + " and the refusal is the server's, exactly as §7 already rules for a picture's bytes —"
            + " so the attribute states what the schema declared, and where the read cannot answer,"
            + " no attribute is rendered at all rather than a number nobody checked.");

        string capValue = declared.Groups[1].Value;

        Assert.False(
            page.Contains(capValue, StringComparison.Ordinal),
            $"§11.1's surface writes the number {capValue}, which is the bound"
            + $" {CapConstraintName} declares. §7 says the cap travels rather than being copied — the"
            + " read above asks `pg_get_constraintdef` for it and the write service recognises the"
            + " refusal by the constraint's name (F-107). This claim is over this one file rather"
            + " than over the tree, and the reason is recorded as a residual: 1000 is not a"
            + " distinctive number and two files in `wwwroot/js` write it as a millisecond count, so"
            + " the picture cap's tree-wide twin cannot be written here without reporting them.");
    }

    [Fact]
    public void ABlankSaveIsRefused_AndIsNeverTreatedAsAWithdrawal()
    {
        string page = GuestPage();

        Assert.True(
            page.Contains(BlankOutcome, StringComparison.Ordinal),
            $"§11.1's save path does not name {BlankOutcome}, so an empty box falls to whatever the"
            + " catch-all arm says — which on this surface is a sentence about the dish having come"
            + " off the menu. The write service decided this outcome in Stage 6c and the surface owes"
            + " it a sentence of its own.");

        Assert.True(
            page.Contains(WithdrawCall, StringComparison.Ordinal),
            $"§11.1's surface never calls {WithdrawCall}, so the prohibition below is asserting"
            + " nothing and §7's withdrawal verb has no control.");

        string save = BodyOf(page, SaveHandlerSignature);

        Assert.False(
            save.Contains(WithdrawCall, StringComparison.Ordinal),
            "§11.1's save path reaches the withdrawal verb. Clearing the box and pressing Save is"
            + " what somebody will do, and treating it as a withdrawal makes the surface a second"
            + " authority on what withdrawal means — the write service already refuses a blank body"
            + " by name. One verb per intent: the refusal names the control that does withdraw, and"
            + " nothing is written until it is pressed.");
    }

    [Fact]
    public void TheMenuWorkflow_NeverReachesTheCommentWrite()
    {
        string workflow = File.ReadAllText(PathUnder(WorkflowRelativePath));

        Assert.Contains("IMenuWorkflow", workflow, StringComparison.Ordinal);
        Assert.Contains("MenuChanged", workflow, StringComparison.Ordinal);

        foreach (string forbidden in new[] { "IMenuItemComments", "SubmitAsync" })
        {
            Assert.False(
                workflow.Contains(forbidden, StringComparison.Ordinal),
                $"MenuWorkflow mentions {forbidden}. A comment publishes nothing (§7, §9), for the"
                + " reaction's reason: §9's MenuChanged means re-read the menu, and an opinion has"
                + " not changed the menu. A verb behind the workflow would make one guest's sentence"
                + " re-read the whole menu on every phone in the building. The symptom is load, not"
                + " an error, which is why this is a test rather than a comment.");
        }
    }

    private static string GuestPage() => File.ReadAllText(PathUnder(GuestPageRelativePath));

    private static string Migration() => File.ReadAllText(PathUnder(MigrationRelativePath));

    private static string BodyOf(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);

        Assert.True(at >= 0, $"'{signature}' is not in the source this fact was written about.");

        int open = source.IndexOf('{', at);

        Assert.True(open > at, $"'{signature}' has no body.");

        int depth = 0;

        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return source[open..index];
                }
            }
        }

        throw new InvalidOperationException($"'{signature}' has an unterminated body.");
    }

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
