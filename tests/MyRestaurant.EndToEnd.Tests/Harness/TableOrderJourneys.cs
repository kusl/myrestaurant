using System.Globalization;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One line on the guest's committed order, as the surface renders it (§11.1): what it is, and which
/// of the two badges it is wearing. The badge is the whole of §16.3 scenario 6's assertion, so it is
/// modelled as a value rather than left as a class name for a scenario to match on.
///
/// <para>The same record carries a line belonging to <em>somebody else</em> at the table
/// (<see cref="PartyOrder.Lines"/>). That is reuse rather than laziness: §11.1 renders the two lists
/// from different sources — your own from the event fold, the rest of the table from
/// <c>order_current_line</c> — but the badge is published as the same pair of classes in both, and a
/// scenario asking "is their soup still with the kitchen?" is asking exactly the question it asks of
/// its own. The one difference is that <see cref="GuestLineBadge.Removed"/> cannot occur on a party
/// line at all: <c>order_current_line</c> filters removals out in SQL, so a line that was taken off
/// simply is not there.</para>
/// </summary>
internal sealed record GuestOrderLine(string Name, GuestLineBadge Badge);

/// <summary>§11.1's two states for a live line, named the way the surface says them.</summary>
internal enum GuestLineBadge
{
    /// <summary>Sent, not yet fulfilled — the surface says "With the kitchen".</summary>
    WithTheKitchen,

    /// <summary>The kitchen marked it done — the surface says "At your table".</summary>
    AtYourTable,

    /// <summary>Taken off the order (§11.1's struck-through line).</summary>
    Removed,
}

/// <summary>
/// One price adjustment as §11.1 shows it to the guest whose line it is: "price adjustments shown
/// old → new with reason" (§6.5.7, §11.3).
///
/// <para><b>Three fields rather than a parsed sentence.</b> The two amounts have elements of their own —
/// the old one struck through in an <c>&lt;s&gt;</c>, the new one in a <c>&lt;strong&gt;</c> — so they are
/// read as themselves rather than pulled back out of prose. §16.3 scenario 9's claim is precisely that
/// <em>both</em> are on screen and in the right places; a single string could satisfy "the new price
/// appears" while the old one had quietly vanished, which is the half of "old → new" that costs a guest
/// the ability to see what changed.</para>
///
/// <para><paramref name="Sentence"/> is the whole paragraph, and it carries the two things that have no
/// element of their own: the reason (§6.5.7 requires one, and the table's own CHECK enforces it) and the
/// actor's role. The role matters more than it looks — §6.2 binds a <c>price_adjustment</c> to counter or
/// administrator, and a surface that said "an administrator" for a counter's act would be misreporting
/// who to ask about a number on a bill.</para>
///
/// <para>Both amounts are kept as text for the same reason <see cref="PartyOrder.TotalText"/> is: they
/// are rendered through <c>MoneyText.Format(amount, CurrencyCode)</c>, and parsing them back into
/// decimals would mean reimplementing a currency formatter inside a test in order to compare against a
/// number the test already knew.</para>
/// </summary>
internal sealed record GuestPriceAdjustment(
    string PreviousPriceText,
    string NewPriceText,
    string Sentence);

/// <summary>
/// One of the guest's <em>own</em> committed lines with everything §11.1 renders under it — the badge, the
/// extended price, and any price adjustments in the order they were applied.
///
/// <para><b>A separate record from <see cref="GuestOrderLine"/>, on purpose.</b> That one is shared with
/// the party list, and the two extra fields here have no meaning there: §11.1 renders somebody else's
/// line as a name and a chip and nothing more. Widening the shared record would have meant two fields
/// that are always empty on half its uses, which is how a record stops describing anything.</para>
///
/// <para><paramref name="PriceText"/> is the <em>extended</em> price — quantity × current unit price —
/// because that is what §11.1 puts beside the line. It is the number that makes an adjustment's
/// arithmetic visible: a line at quantity two whose unit price moved by three has to move by six, and a
/// surface that changed the sentence without recomputing the money would pass every other assertion
/// here.</para>
/// </summary>
internal sealed record GuestOrderLineDetail(
    string Name,
    GuestLineBadge Badge,
    string PriceText,
    IReadOnlyList<GuestPriceAdjustment> PriceAdjustments);

/// <summary>
/// One person on the sitting's roster, as §11.1's "Who is here" list renders them (§5.2).
///
/// <para><paramref name="IsYou" /> is read from the "you" chip beside the name rather than from a
/// person identifier the harness would have to learn some other way. That chip is the only thing on
/// the surface that distinguishes the reader from everybody else, and §16.3 scenario 5 turns on
/// exactly that distinction: the first guest must see the second arrive <em>as somebody else</em>.</para>
/// </summary>
internal sealed record TableRosterMember(string Name, bool IsYou);

/// <summary>
/// One other person's order under §11.1's "The rest of the table": the name that goes on the bill, the
/// money beside it exactly as the surface formatted it, and their lines.
///
/// <para><b>The total is kept as text on purpose.</b> It is rendered through
/// <c>MoneyText.Format(amount, CurrencyCode)</c>, so parsing it back into a decimal here would mean
/// reimplementing a currency formatter inside a test in order to compare against a number the test
/// already knew. A scenario that cares asserts on containment of a formatted figure, or — better —
/// asserts on the lines, which are the thing §16.3 scenario 5 is actually about.</para>
///
/// <para>Only people who have <em>sent</em> something appear here. §6.1 creates the
/// <c>guest_order</c> row lazily inside the first send transaction and <c>sitting_bill</c> is built
/// from those rows, so a guest who has joined and ordered nothing is on the roster and not in the
/// party list. That asymmetry is the product's, not the harness's, and scenario 5 depends on it.</para>
/// </summary>
internal sealed record PartyOrder(string BillName, string TotalText, IReadOnlyList<GuestOrderLine> Lines);

/// <summary>
/// What the staging area is holding right now (§11.1): the adds waiting to go, the committed lines
/// ticked to be taken off in the same batch, and how many of those adds are wearing §7's "this became
/// unavailable while it was in your basket" mark.
///
/// <para>Three counts rather than three methods because §16.3 scenario 7 waits on combinations of
/// them, and a wait that read them one at a time would be sampling a surface that re-renders all three
/// together.</para>
/// </summary>
internal sealed record BasketContents(int StagedAdds, int TickedRemovals, int UnavailableMarks);

/// <summary>
/// §11.1's two figures at the foot of the surface: what this guest owes and what the table owes.
///
/// <para>Both are text for the reason every money field in this harness is — see
/// <see cref="CounterBillLine"/> — and both matter to §16.3 scenario 10 because they are computed on the
/// <em>guest's</em> circuit. <c>MyTotal</c> and <c>TableTotal</c> are C# sums over <c>sitting_bill</c>
/// rows read by this component; the till's header is a SQL sum over the same view read by another
/// process. A scenario that compares them is comparing two independent opinions about one number, which
/// is what "totals match" has to mean if it is to mean anything.</para>
/// </summary>
internal sealed record GuestTotals(string YourTotalText, string TableTotalText);

/// <summary>
/// The guest's surface after §11.1's flip: "On <c>SittingClosed</c>, the surface flips to a read-only
/// settled-bill view."
///
/// <para><b>The flip is mostly an absence, which is why the heading is read too.</b> No picker, no Send,
/// no removal ticks — and a surface that has not finished loading has none of those either. §11.1's
/// settled heading is the one positive marker that the flip happened, and it is why
/// <c>TableOrderSurface.razor</c> gained <c>.order-settled-heading</c> in M6 Slice 13.</para>
///
/// <para><paramref name="Lines"/> is carried because the settled view is not an empty page: §11.1 keeps
/// the guest's own lines with their badges, which is the record of what was charged. A line still saying
/// "With the kitchen" on a settled bill is not a bug — it is §5.3's "knowingly charge" written down, and
/// a surface that quietly re-badged it at close would be hiding the one fact the guest might want to
/// argue about.</para>
/// </summary>
internal sealed record GuestSettledView(
    bool SaysSettled,
    bool OffersPicker,
    bool OffersSend,
    int RemovalCheckboxes,
    GuestTotals Totals,
    IReadOnlyList<GuestOrderLineDetail> Lines);

/// <summary>
/// What one press of Send left on the surface: the basket it emptied or did not, and the sentence it
/// wrote (TECHNICAL_SPECIFICATION §6.5.9, §11.1).
///
/// <para>Not returned to scenarios — <see cref="TableOrderJourneys.SendAsync"/> and
/// <see cref="TableOrderJourneys.SendExpectingRefusalAsync"/> each turn it into the one thing their
/// caller asked for, and turn the other outcome into a failure that says what happened instead. It
/// exists because both of those need to watch for <em>both</em> outcomes at once, and a single poll
/// that can end two ways is easier to get right than two waits racing each other.</para>
/// </summary>
/// <param name="BasketIsEmpty">
/// True once the staging area holds neither an add nor a ticked removal. This is how the harness knows
/// a send committed. §11.1 clears the basket only on an accepted event and a refusal leaves it exactly
/// as it was, so the basket is the surface's own record of which happened — and unlike the confirmation
/// sentence it cannot be confused with the identical sentence a previous send left behind.
/// </param>
/// <param name="Confirmation">The §11.1 confirmation, or <c>null</c> when none is on screen.</param>
/// <param name="RefusalReasons">§6.5.9's per-operation reasons, empty when the panel is absent.</param>
internal sealed record SendOutcome(
    bool BasketIsEmpty,
    string? Confirmation,
    IReadOnlyList<string> RefusalReasons);

/// <summary>
/// The guest ordering journeys the §16.3 scenarios walk: staging items into the basket, ticking a
/// committed line for removal, sending them, and reading what came back — plus who else is at the
/// table and what they have ordered (TECHNICAL_SPECIFICATION §6.5, §9, §11.1).
///
/// <para><b>Everything here is scoped to <c>#table-order-surface</c>, and that is not decoration.</b>
/// <c>/table/{id}</c> is a static-SSR page hosting this island, and the page itself renders a
/// <c>p.status-success</c> of its own — "You have joined Table Four" — the moment a join redirects back
/// to it. An unscoped wait for a success message would match that one every time and report a send as
/// having been accepted before the button was even pressed. The island's wrapper is the boundary
/// between what the circuit owns and what the HTTP response owns, so every selector below starts at
/// it.</para>
///
/// <para><b>Why the liveness wait exists.</b> Prerendering draws the whole surface — picker, basket,
/// totals — and every control on it is an <c>@onclick</c>. On an island that never became interactive
/// the taps below land on nothing at all, and the first thing a scenario would learn is that the basket
/// stayed empty thirty seconds later. <see cref="WaitForLiveSurfaceAsync"/> turns that into one sentence
/// about the circuit, at the moment the circuit was needed — the same lesson
/// <see cref="DisplayJourneys.WaitForLiveSurfaceAsync"/> was written for.</para>
///
/// <para><b>Why the roster and the party list have waits of their own.</b> Both change because of a §9
/// broadcast started by <em>another</em> browser — a second guest pressing Join, a first guest pressing
/// Send — and there is no click on this page to await. A scenario that read them once would be sampling
/// a race it cannot see; <see cref="WaitForRosterAsync"/> and <see cref="WaitForPartyAsync"/> re-read
/// until the predicate holds and then say, in one sentence, what was on screen when it never did.</para>
///
/// <para><b>Why a send is judged by the basket rather than by its confirmation.</b> §11.1 clears the
/// staging area only on an accepted event, so an empty basket is the surface saying the transaction
/// committed. The confirmation sentence cannot carry that weight on its own: it stays on screen until
/// something clears it, and two sends of the same shape produce the same words — so "wait for
/// <c>p.status-success</c>" is satisfied by the <em>previous</em> send in any scenario that sends
/// twice. The same poll watches for §6.5.9's rejection panel, which is why a refused send now says
/// which operation was refused and why, at the moment it happens, instead of thirty seconds later as a
/// timeout.</para>
/// </summary>
internal static class TableOrderJourneys
{
    /// <summary>The ordering island, whatever state it is in.</summary>
    private const string SurfaceSelector = "#table-order-surface";

    /// <summary>
    /// The island as rendered by a live circuit. <c>TableOrderSurface.razor</c> sets <c>data-live</c>
    /// from <c>RendererInfo.IsInteractive</c>, so this matches only markup an interactive renderer
    /// produced — never the prerendered pass, which is identical in every other respect.
    /// </summary>
    private const string LiveSurfaceSelector = "#table-order-surface[data-live='true']";

    /// <summary>Staged adds only. <c>.is-removal</c> rows are ticked removals and are counted separately.</summary>
    private const string BasketLineSelector =
        "#table-order-surface ul.order-basket li.order-basket-line:not(.is-removal)";

    /// <summary>Ticked removals — the other half of what one Send carries (§6.3's N added + M removed).</summary>
    private const string BasketRemovalSelector =
        "#table-order-surface ul.order-basket li.order-basket-line.is-removal";

    /// <summary>The committed lines — what has actually reached the kitchen (§11.1's "Sent to the kitchen").</summary>
    private const string CommittedLineSelector = "#table-order-surface ul.order-lines li.order-line";

    /// <summary>
    /// §11.1's "old → new with reason" sentence, relative to a committed line. A class of its own as of
    /// M6 Slice 12: the removal sentence directly above it carries the identical
    /// <c>.order-line-detail</c>, so "the detail paragraph under this line" was never a way to name this
    /// one — and on a line that had been both adjusted and removed, it would have named both.
    /// </summary>
    private const string PriceAdjustmentSelector = "p.order-line-adjustment";

    /// <summary>§11.1's "Who is here" list — every member of the sitting, in join order (§5.2).</summary>
    private const string RosterMemberSelector = "#table-order-surface ul.table-roster > li";

    /// <summary>§11.1's "The rest of the table" — one entry per <em>other</em> person who has ordered.</summary>
    private const string PartyOrderSelector = "#table-order-surface ul.order-party > li.order-party-order";

    /// <summary>§6.5.9's refusal panel, one list item per refused operation with its own reason.</summary>
    private const string RefusalReasonSelector = "#table-order-surface ul.order-reject-list li";

    /// <summary>
    /// §11.1's unticking notice — the sentence the surface writes when it drops a removal mark that has
    /// gone stale underneath the guest. It has a class of its own precisely so this can name it: three
    /// other <c>p.status-error</c> elements live inside the island.
    /// </summary>
    private const string PruneNoticeSelector = "#table-order-surface p.order-prune-notice";

    /// <summary>The confirmation of an accepted send (§11.1), scoped to the island — see the type remarks.</summary>
    private const string ConfirmationSelector = "#table-order-surface p.status-success";

    /// <summary>
    /// §11.1's settled heading, named as of M6 Slice 13. The one positive marker that the surface has
    /// flipped — everything else about the settled state is the absence of something.
    /// </summary>
    private const string SettledHeadingSelector = "#table-order-surface h2.order-settled-heading";

    /// <summary>The picker and the Send row, both rendered only while the sitting is open (§11.1).</summary>
    private const string PickerSelector = "#table-order-surface div.order-picker";
    private const string SendRowSelector = "#table-order-surface .order-send";

    /// <summary>§11.1's per-line "take this off my order" tick, offered only on an open sitting.</summary>
    private const string RemovalCheckboxSelector = "#table-order-surface label.order-line-remove";

    /// <summary>
    /// §11.1's totals list. A <c>&lt;dl&gt;</c> of <c>&lt;div&gt;</c> groupings, each holding one
    /// <c>&lt;dt&gt;</c> label and its <c>&lt;dd&gt;</c> amount — so a figure is found by the term that
    /// names it rather than by its position, which is how anything reading the document finds it and is
    /// what keeps a third total added between them from silently shifting the answer.
    /// </summary>
    private const string TotalsGroupSelector = "#table-order-surface dl.order-totals > div";

    /// <summary>The two <c>&lt;dt&gt;</c> terms §11.1 writes in that list.</summary>
    private const string YourTotalTerm = "Your total";
    private const string TableTotalTerm = "Table total";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long one press of Send has to produce an answer. A send is one transaction against a local
    /// PostgreSQL under an advisory lock nothing else is holding, so the honest expectation is
    /// milliseconds; thirty seconds is the same patience every other page operation in this harness
    /// gets. Either outcome ends the wait, so this length is only ever reached when the click did not
    /// dispatch at all.
    /// </summary>
    private static readonly TimeSpan SendPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits until the ordering island on screen was rendered by a live circuit rather than by
    /// prerendering. Every other method here assumes it; a scenario calls it once after joining.
    /// </summary>
    internal static async Task WaitForLiveSurfaceAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.Locator(LiveSurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            // Read the page BEFORE composing the message: an await inside an interpolated string that
            // binds to a handler is CS4007, and the diagnosis is worth more than the one-liner.
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The ordering surface never became interactive within"
                    + $" {timeout.TotalSeconds:F0}s; it is still the prerendered markup ({surface})."
                    + $" Nothing on this page will respond — Add to basket, Send and every quantity box"
                    + $" are @onclick handlers with no circuit behind them, and the kitchen will never"
                    + $" hear anything. Check that /_framework/blazor.web.js is served"
                    + $" (RestaurantInstance probes it at startup) and that the browser reached"
                    + $" /_blazor."),
                exception);
        }
    }

    /// <summary>
    /// Stages one item into the basket (§11.1) and returns once the basket has grown by one.
    ///
    /// <para>The item is chosen by <em>value</em> rather than by label. The picker's label is the name,
    /// the formatted price, and — for a deactivated item — §7's "(currently unavailable)"; matching on
    /// it would make this fail for a currency setting. The value is the bare identifier, which is what
    /// <see cref="AdministrationJourneys.CreateMenuItemAsync"/> hands back.</para>
    ///
    /// <para>Nothing reaches the kitchen here. §11.1 is explicit that the basket is local until Send,
    /// and the surface says so in as many words; <see cref="SendAsync"/> is the part that writes.</para>
    /// </summary>
    internal static async Task StageAsync(
        IPage page,
        MenuItemOnTheMenu item,
        int quantity,
        string? customizationNote = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(item);

        int before = await page.Locator(BasketLineSelector).CountAsync();

        await page.SelectOptionAsync(
            "#order-picker-item",
            new SelectOptionValue { Value = item.Identifier.ToString("D", CultureInfo.InvariantCulture) });

        await page.FillAsync(
            "#order-picker-quantity",
            quantity.ToString(CultureInfo.InvariantCulture));

        // Filled unconditionally, including with the empty string: the picker keeps whatever was typed
        // for the previous item until StageItem clears it, so skipping this would silently attach the
        // last note to the next thing staged.
        await page.FillAsync("#order-picker-note", customizationNote ?? string.Empty);

        // Focus the button before pressing it, and the order of the three fills above is not arbitrary
        // either. All three controls are @bind, which is @bind:event="onchange" by default, and Playwright
        // types into a text or number input rather than assigning to it — so the value is in the DOM but
        // the change event has not fired yet, and the component has not heard about it. Moving focus is
        // what fires it. Each fill blurs the previous field, and this focus blurs the last one, so all
        // three bindings have been dispatched by the time the click arrives. Relying on the click's own
        // implicit blur would work too, right up until the day the events raced.
        string addToBasket = $"{SurfaceSelector} .order-picker button:has-text('Add to basket')";

        await page.FocusAsync(addToBasket);
        await page.ClickAsync(addToBasket);

        if (await WaitForCountAsync(page, BasketLineSelector, before + 1, TimeSpan.FromSeconds(15)))
        {
            return;
        }

        // Both reads happen before the message is composed: an await inside an interpolated string that
        // binds to a handler is CS4007.
        int after = await page.Locator(BasketLineSelector).CountAsync();
        string refusal = await DescribeStagingRefusalAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Staging {quantity} × '{item.Name}' did not put a line in the basket:"
                + $" it holds {after} line(s) rather than {before + 1}. {refusal}"));
    }

    /// <summary>
    /// Takes a staged item back out of the basket — §11.1's "Take out" — and returns once it is gone.
    ///
    /// <para>Matched on the basket row's own name rather than on the menu item's identifier, because a
    /// staged row carries no identifier in the markup: <c>StagedOrderLine.StagingIdentifier</c> is a
    /// client-side key and never reaches the DOM. The name is what the guest sees and what they would
    /// tap on, which makes it the right thing for a scenario to name too.</para>
    /// </summary>
    internal static async Task UnstageAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator staged = page.Locator(BasketLineSelector);
        int before = await staged.CountAsync();

        for (int index = 0; index < before; index++)
        {
            ILocator line = staged.Nth(index);
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            if (!name.Contains(menuItemName, StringComparison.Ordinal))
            {
                continue;
            }

            await line.Locator("button:has-text('Take out')").First.ClickAsync();

            if (await WaitForCountAsync(page, BasketLineSelector, before - 1, TimeSpan.FromSeconds(15)))
            {
                return;
            }

            // Read before composing: an await inside an interpolated string that binds to a handler is
            // CS4007, because DefaultInterpolatedStringHandler is a ref struct.
            int after = await page.Locator(BasketLineSelector).CountAsync();

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Taking '{menuItemName}' out of the basket left it holding {after} line(s)"
                    + $" rather than {before - 1}."));
        }

        string basket = await DescribeBasketAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no staged '{menuItemName}' in the basket to take out."
                + $" What is staged: {basket}."));
    }

    /// <summary>
    /// Ticks one committed line for removal in the next send (§11.1's
    /// "mark-my-pending-line-for-removal") and returns once the basket shows the ticked removal.
    ///
    /// <para><b>A missing tick box is a diagnosis, not a timeout.</b> §11.1 renders the control only
    /// where <c>NarratedOrderLine.GuestMayRemove</c> holds — the line is pending, it was added by a
    /// guest submission, and that guest is this one (§6.5.3) — so a line that offers none is a line the
    /// transaction would have refused anyway. Saying which of those it is at the moment the scenario
    /// reaches for it is worth far more than the click that would otherwise fail somewhere else.</para>
    /// </summary>
    internal static async Task MarkForRemovalAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        int before = await page.Locator(BasketRemovalSelector).CountAsync();
        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            if (!name.Contains(menuItemName, StringComparison.Ordinal))
            {
                continue;
            }

            ILocator tick = line.Locator("label.order-line-remove input[type='checkbox']");

            if (await tick.CountAsync() == 0)
            {
                // Read before composing: an await inside an interpolated string that binds to a handler
                // is CS4007, because DefaultInterpolatedStringHandler is a ref struct.
                GuestLineBadge badge = await ReadBadgeAsync(line);

                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The line '{name}' offers no way to take it off: §11.1 renders the tick box"
                        + $" only while GuestMayRemove holds (pending, added by this guest's own"
                        + $" submission — §6.5.3), so the surface has already decided this one is not"
                        + $" the guest's to remove. The line is badged {badge}."));
            }

            await tick.First.CheckAsync();

            if (await WaitForCountAsync(page, BasketRemovalSelector, before + 1, TimeSpan.FromSeconds(15)))
            {
                return;
            }

            int after = await page.Locator(BasketRemovalSelector).CountAsync();

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Ticking '{name}' for removal did not reach the basket: it holds {after}"
                    + $" removal(s) rather than {before + 1}."));
        }

        string committed = Describe(await ReadCommittedLinesAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no committed line for '{menuItemName}' to take off."
                + $" The order holds: {committed}."));
    }

    /// <summary>
    /// Whether §11.1 is currently offering a removal control on the named committed line. False is a
    /// fact worth asserting rather than an absence to work around: it is how the surface says a line has
    /// stopped being the guest's to take off (§6.5.3).
    /// </summary>
    internal static async Task<bool> LineOffersRemovalAsync(IPage page, string menuItemName)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            if (name.Contains(menuItemName, StringComparison.Ordinal))
            {
                return await line.Locator("label.order-line-remove").CountAsync() > 0;
            }
        }

        // Read before composing: an await inside an interpolated string that binds to a handler is
        // CS4007, because DefaultInterpolatedStringHandler is a ref struct and cannot be held across
        // the suspension point. Four other failure paths in this file already hoist the read into a
        // local for exactly this reason; this one was written inline and did not compile.
        string committed = Describe(await ReadCommittedLinesAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no committed line for '{menuItemName}' on this order."
                + $" It holds: {committed}."));
    }

    /// <summary>
    /// Presses Send (§11.1) and returns the confirmation the surface rendered — the sentence that names
    /// how many lines went, which is worth returning rather than merely waiting for because a send of
    /// two adds and a send of one read identically to a selector.
    ///
    /// <para>Acceptance is judged by the <em>basket</em>, not by the sentence: §11.1 empties the staging
    /// area only on an accepted event, and the sentence from a previous send is still on screen until
    /// something clears it. A refusal ends the wait immediately with §6.5.9's per-operation reasons,
    /// because "Send did not confirm" without them is a mystery and with them is usually the answer.</para>
    /// </summary>
    internal static async Task<string> SendAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        SendOutcome outcome = await PressSendAsync(page);

        if (outcome.RefusalReasons.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The send was refused, so nothing was written and the basket is untouched (§6.5.9)."
                    + $" The surface says: {string.Join(" | ", outcome.RefusalReasons)}"));
        }

        return outcome.Confirmation ?? string.Empty;
    }

    /// <summary>
    /// Presses Send expecting §6.5.9 to refuse it, and returns the per-operation reasons in the order
    /// the panel lists them — which is the order of the operations that were sent, adds before removals
    /// (<c>OrderStaging.Build</c>).
    ///
    /// <para>The mirror of <see cref="SendAsync"/>, and it exists for §16.3 scenario 7: a refusal is
    /// that scenario's subject rather than its accident, and a scenario that expressed it as a caught
    /// exception would be asserting on prose. An <em>accepted</em> send here is the failure, and it is
    /// reported as one — because a batch that went through when it should have been refused is the
    /// interesting bug, not a missing panel.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<string>> SendExpectingRefusalAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        SendOutcome outcome = await PressSendAsync(page);

        if (outcome.RefusalReasons.Count > 0)
        {
            return outcome.RefusalReasons;
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The send was accepted when §6.5.9 should have refused the whole batch. The surface"
                + $" says: '{outcome.Confirmation ?? "(nothing)"}', and the staging area"
                + $" {(outcome.BasketIsEmpty ? "has been cleared, so the event was written" : "is still full, so this is neither outcome")}."));
    }

    /// <summary>How many staged adds are sitting in the basket right now.</summary>
    internal static Task<int> BasketLineCountAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Locator(BasketLineSelector).CountAsync();
    }

    /// <summary>How many committed lines are ticked to be taken off in the next send.</summary>
    internal static Task<int> BasketRemovalCountAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Locator(BasketRemovalSelector).CountAsync();
    }

    /// <summary>
    /// Everything the staging area is holding, read in one pass.
    ///
    /// <para><see cref="BasketContents.UnavailableMarks"/> is the surface's half of §7 — "guest staging
    /// areas mark newly-inactive staged items and the send re-validates server-side regardless" — and
    /// it is the observable proof that <c>MenuChanged</c> reached this circuit at all.</para>
    /// </summary>
    internal static async Task<BasketContents> ReadBasketAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new BasketContents(
            await page.Locator(BasketLineSelector).CountAsync(),
            await page.Locator(BasketRemovalSelector).CountAsync(),
            await page.Locator($"{SurfaceSelector} p.order-line-warning").CountAsync());
    }

    /// <summary>
    /// §11.1's unticking notice, or <c>null</c> when the surface is not showing one. The sentence the
    /// surface writes when it drops a removal mark that stopped being valid underneath the guest — the
    /// kitchen fulfilled the line, or staff took it off (§6.5.3).
    /// </summary>
    internal static async Task<string?> ReadPruneNoticeAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator notice = page.Locator(PruneNoticeSelector);

        return await notice.CountAsync() == 0
            ? null
            : (await notice.First.InnerTextAsync()).Trim();
    }

    /// <summary>
    /// The guest's committed lines, in the order the surface lists them (§11.1). Each carries the badge
    /// it is wearing, read from the chip's class rather than from its words: "At your table" is copy,
    /// <c>chip-ok</c> is the state.
    /// </summary>
    internal static async Task<IReadOnlyList<GuestOrderLine>> ReadCommittedLinesAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        IReadOnlyList<GuestOrderLineDetail> detailed = await ReadOwnLinesAsync(page);

        return detailed
            .Select(line => new GuestOrderLine(line.Name, line.Badge))
            .ToArray();
    }

    /// <summary>
    /// The guest's own committed lines with everything §11.1 renders under each — the badge, the extended
    /// price, and every price adjustment in the order they were applied.
    ///
    /// <para>The one DOM walk over <c>ul.order-lines</c>, which
    /// <see cref="ReadCommittedLinesAsync"/> now projects from rather than duplicating. The extra cost is
    /// two locator round trips per line plus three per adjustment, which against a local browser is
    /// microseconds beside the 250 ms poll interval every wait here uses — and a second walk that drifted
    /// out of step with this one would be a worse price to pay.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<GuestOrderLineDetail>> ReadOwnLinesAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        List<GuestOrderLineDetail> committed = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);

            // "2 × Soup of the day" — the quantity and the name are one span, deliberately, because
            // that is how §11.1 words it. The scenarios match on containment, so it is kept whole.
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();
            string price = (await line.Locator("span.order-line-price").First.InnerTextAsync()).Trim();

            committed.Add(new GuestOrderLineDetail(
                name,
                await ReadBadgeAsync(line),
                price,
                await ReadPriceAdjustmentsAsync(line)));
        }

        return committed;
    }

    /// <summary>
    /// Waits until the guest's own line for <paramref name="menuItemName"/> satisfies
    /// <paramref name="expectation"/>, and returns the reading that did.
    ///
    /// <para>This is the wait §16.3 scenario 9 is built on, and it is the same shape as
    /// <see cref="WaitForRosterAsync"/>'s: nobody touches the guest's phone. A counter presses Adjust in
    /// another browser, <c>IOrderWorkflow</c> publishes <c>OrderLinesChanged</c> after the transaction
    /// commits (§9: "fired on any order event commit"), this surface re-reads, and a sentence appears
    /// under a line. There is no click here to await, so the only honest thing to do is re-read — and to
    /// say what the line was showing if it never arrived, because "the adjustment did not appear" and
    /// "the broadcast never left the other circuit" look identical from a timeout.</para>
    ///
    /// <para>The line is matched by containment rather than equality, because §11.1 renders the quantity
    /// and the name as one span — "2 × Steak pie" — and a scenario knows only the name.</para>
    /// </summary>
    internal static async Task<GuestOrderLineDetail> WaitForOwnLineAsync(
        IPage page,
        string menuItemName,
        Func<GuestOrderLineDetail, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<GuestOrderLineDetail> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadOwnLinesAsync(page);

            GuestOrderLineDetail? named = observed.FirstOrDefault(
                line => line.Name.Contains(menuItemName, StringComparison.Ordinal));

            if (named is not null && expectation(named))
            {
                return named;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The guest's line for '{menuItemName}' never showed {whatIsExpected} within"
                + $" {timeout.TotalSeconds:F0}s. What the order shows: {DescribeOwn(observed)}."));
    }

    /// <summary>
    /// §11.1's two totals, each found by the <c>&lt;dt&gt;</c> that names it.
    ///
    /// <para>A missing term is a failure rather than a blank, and it says which terms the list does hold.
    /// A surface that had stopped rendering "Table total" would otherwise compare equal to one showing
    /// the wrong table total, because both would produce an empty string.</para>
    /// </summary>
    internal static async Task<GuestTotals> ReadTotalsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator groups = page.Locator(TotalsGroupSelector);
        int count = await groups.CountAsync();

        Dictionary<string, string> byTerm = new(StringComparer.Ordinal);

        for (int index = 0; index < count; index++)
        {
            ILocator group = groups.Nth(index);

            string term = (await group.Locator("dt").First.InnerTextAsync()).Trim();
            string amount = (await group.Locator("dd").First.InnerTextAsync()).Trim();

            byTerm[term] = amount;
        }

        if (!byTerm.TryGetValue(YourTotalTerm, out string? yourTotal)
            || !byTerm.TryGetValue(TableTotalTerm, out string? tableTotal))
        {
            string terms = byTerm.Count == 0
                ? "nothing at all"
                : string.Join(", ", byTerm.Keys.Select(term => $"'{term}'"));

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§11.1's totals list does not carry both '{YourTotalTerm}' and '{TableTotalTerm}'."
                    + $" What it names: {terms}. The browser is at '{page.Url}'."));
        }

        return new GuestTotals(yourTotal, tableTotal);
    }

    /// <summary>The surface as it stands right now, in the shape §11.1's settled view is judged by.</summary>
    internal static async Task<GuestSettledView> ReadSettledViewAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new GuestSettledView(
            await page.Locator(SettledHeadingSelector).CountAsync() > 0,
            await page.Locator(PickerSelector).CountAsync() > 0,
            await page.Locator(SendRowSelector).CountAsync() > 0,
            await page.Locator(RemovalCheckboxSelector).CountAsync(),
            await ReadTotalsAsync(page),
            await ReadOwnLinesAsync(page));
    }

    /// <summary>
    /// Waits until §11.1's flip has happened on this surface, and returns it.
    ///
    /// <para><b>Nobody touches this page.</b> A counter presses Yes in another browser,
    /// <c>ISittingWorkflow</c> publishes <c>SittingClosed</c> after the transaction commits (§9), this
    /// component re-reads, <c>GetOpenSittingForMemberAsync</c> now answers <c>null</c> because
    /// <c>closed_at</c> is set, and the picker, the Send row and every removal tick stop being rendered.
    /// There is no click here to await, so the only honest thing to do is re-read.</para>
    ///
    /// <para>The wait keys on the settled heading arriving rather than on the picker leaving, and the
    /// difference is the whole reason <c>.order-settled-heading</c> exists: a surface whose circuit died
    /// mid-scenario also has no picker, and would satisfy a wait written the other way round while
    /// proving nothing at all.</para>
    /// </summary>
    internal static async Task<GuestSettledView> WaitForSettledViewAsync(
        IPage page,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(SettledHeadingSelector).CountAsync() > 0)
            {
                return await ReadSettledViewAsync(page);
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        string surface = await DescribeSurfaceAsync(page);
        IReadOnlyList<GuestOrderLineDetail> lines = await ReadOwnLinesAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The guest's surface never flipped to §11.1's settled view within"
                + $" {timeout.TotalSeconds:F0}s. §9 publishes SittingClosed after the close commits and"
                + $" this surface subscribes to it, so either the broadcast never left the till's circuit"
                + $" or this one is not listening ({surface}). The order still shows:"
                + $" {DescribeOwn(lines)}."));
    }

    /// <summary>A short, quotable rendering of the settled view, for a failure message.</summary>
    internal static string DescribeSettledView(GuestSettledView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        string offered = view.OffersPicker || view.OffersSend || view.RemovalCheckboxes > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{(view.OffersPicker ? "the picker" : "no picker")},"
                + $" {(view.OffersSend ? "a Send row" : "no Send row")},"
                + $" {view.RemovalCheckboxes} removal tick(s)")
            : "no picker, no Send row and no removal ticks";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"settled heading {(view.SaysSettled ? "present" : "absent")}; {offered};"
            + $" yours {view.Totals.YourTotalText}, table {view.Totals.TableTotalText};"
            + $" lines: {DescribeOwn(view.Lines)}");
    }

    /// <summary>A short, quotable rendering of the guest's own lines with their adjustments.</summary>
    internal static string DescribeOwn(IReadOnlyList<GuestOrderLineDetail> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Count == 0
            ? "nothing at all"
            : string.Join(
                "; ",
                lines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{line.Name}' {line.PriceText} [{line.Badge}]{DescribeAdjustments(line)}")));
    }

    /// <summary>
    /// Waits until the guest's committed lines satisfy <paramref name="expectation"/>, re-reading them
    /// as §9's broadcasts land. The predicate is the assertion — scenario 6 waits for a specific line to
    /// be wearing a specific badge, not merely for the list to have changed — and
    /// <paramref name="whatIsExpected"/> is what the failure says was wanted, beside what was on screen.
    /// </summary>
    internal static async Task<IReadOnlyList<GuestOrderLine>> WaitForCommittedLinesAsync(
        IPage page,
        Func<IReadOnlyList<GuestOrderLine>, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<GuestOrderLine> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadCommittedLinesAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The guest's order never showed {whatIsExpected} within"
                + $" {timeout.TotalSeconds:F0}s. What it shows instead: {Describe(observed)}."));
    }

    /// <summary>
    /// Waits until the staging area satisfies <paramref name="expectation"/>, which is given the count
    /// of staged adds and the count of ticked removals.
    ///
    /// <para>This is a wait rather than a read for §16.3 scenario 7's sake: a mark can be dropped by
    /// something happening in <em>another</em> browser. The kitchen fulfills a line, §9 sends
    /// <c>LineFulfillmentChanged</c>, this surface re-reads and <c>OrderStaging.PruneRemovals</c> unticks
    /// what is no longer the guest's to remove — with nobody touching this page. There is no click here
    /// to await, so the only honest thing to do is re-read.</para>
    /// </summary>
    internal static async Task<BasketContents> WaitForBasketAsync(
        IPage page,
        Func<BasketContents, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        BasketContents observed = new(0, 0, 0);

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadBasketAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The basket never showed {whatIsExpected} within {timeout.TotalSeconds:F0}s."
                + $" It holds {observed.StagedAdds} staged add(s), {observed.TickedRemovals} ticked"
                + $" removal(s) and {observed.UnavailableMarks} unavailable mark(s)."));
    }

    // --- who is at the table (§5.2, §11.1) ---------------------------------------------------------

    /// <summary>
    /// §11.1's "Who is here" list, in the order the surface renders it — which is join order, because
    /// <c>ISittingDirectory.ListMembersAsync</c> orders by <c>joined_at</c>.
    /// </summary>
    internal static async Task<IReadOnlyList<TableRosterMember>> ReadRosterAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator members = page.Locator(RosterMemberSelector);
        int count = await members.CountAsync();

        List<TableRosterMember> roster = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator member = members.Nth(index);

            string name = (await member.Locator("span.table-roster-name").First.InnerTextAsync()).Trim();

            // The "you" chip is the only chip a roster row ever carries, so its presence is the whole
            // test. Matching on the word would be matching on copy.
            bool isYou = await member.Locator("span.chip").CountAsync() > 0;

            roster.Add(new TableRosterMember(name, isYou));
        }

        return roster;
    }

    /// <summary>
    /// Waits until the roster satisfies <paramref name="expectation"/>.
    ///
    /// <para>This is the wait §16.3 scenario 5 is built on. Nobody touches the first guest's phone: a
    /// second guest presses Join in another browser, <c>TableJoin.razor</c> publishes
    /// <c>SittingMemberJoined</c> after the membership row commits (§9: "fired on: membership insert"),
    /// and this surface re-reads. There is no click here to await and no navigation to settle, so the
    /// only honest thing to do is re-read until it arrives — and to say what was on screen if it never
    /// did, because "the roster did not grow" and "the broadcast never left the other circuit" look
    /// identical from a timeout.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<TableRosterMember>> WaitForRosterAsync(
        IPage page,
        Func<IReadOnlyList<TableRosterMember>, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<TableRosterMember> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadRosterAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The table roster never showed {whatIsExpected} within {timeout.TotalSeconds:F0}s."
                + $" Who it says is here: {DescribeRoster(observed)}."));
    }

    /// <summary>
    /// §11.1's "The rest of the table": everybody <em>except</em> the reader who has sent something,
    /// with their lines underneath.
    ///
    /// <para>An entry with no lines is normal rather than broken — <c>sitting_bill</c> is grouped from
    /// <c>guest_order</c> and keeps a person whose every line has since been removed, and the surface
    /// says "Nothing on their order right now" for exactly that case.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<PartyOrder>> ReadPartyAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator orders = page.Locator(PartyOrderSelector);
        int count = await orders.CountAsync();

        List<PartyOrder> party = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator order = orders.Nth(index);

            // Scoped to the entry's own header rather than to the entry: .order-line-name is the
            // person's bill name here, and nothing inside .order-party-lines carries that class — but
            // pinning it to the header row says so rather than relying on it.
            ILocator header = order.Locator("div.order-line-main").First;

            string billName = (await header.Locator("span.order-line-name").First.InnerTextAsync()).Trim();
            string total = (await header.Locator("span.order-line-price").First.InnerTextAsync()).Trim();

            ILocator lines = order.Locator("ul.order-party-lines > li");
            int lineCount = await lines.CountAsync();

            List<GuestOrderLine> theirLines = new(lineCount);

            for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                ILocator line = lines.Nth(lineIndex);

                string name = (await line.Locator("span.order-party-line-name").First.InnerTextAsync()).Trim();

                theirLines.Add(new GuestOrderLine(name, await ReadBadgeAsync(line)));
            }

            party.Add(new PartyOrder(billName, total, theirLines));
        }

        return party;
    }

    /// <summary>
    /// Waits until the rest of the table satisfies <paramref name="expectation"/>. The other half of
    /// §16.3 scenario 5: the second guest's screen must show the first guest's order change while
    /// nobody touches it, which is <c>OrderLinesChanged</c> crossing from one circuit to another.
    /// </summary>
    internal static async Task<IReadOnlyList<PartyOrder>> WaitForPartyAsync(
        IPage page,
        Func<IReadOnlyList<PartyOrder>, bool> expectation,
        TimeSpan timeout,
        string whatIsExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expectation);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        IReadOnlyList<PartyOrder> observed = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            observed = await ReadPartyAsync(page);

            if (expectation(observed))
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The rest of the table never showed {whatIsExpected} within"
                + $" {timeout.TotalSeconds:F0}s. What it shows instead: {DescribeParty(observed)}."));
    }

    // --- failure prose -----------------------------------------------------------------------------

    /// <summary>A short, quotable rendering of a set of lines, for a failure message.</summary>
    internal static string Describe(IReadOnlyList<GuestOrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Count == 0
            ? "nothing at all"
            : string.Join(
                "; ",
                lines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{line.Name}' [{line.Badge}]")));
    }

    /// <summary>A short, quotable rendering of the roster, for a failure message.</summary>
    internal static string DescribeRoster(IReadOnlyList<TableRosterMember> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        return roster.Count == 0
            ? "nobody at all"
            : string.Join(
                "; ",
                roster.Select(member => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{member.Name}'{(member.IsYou ? " (you)" : string.Empty)}")));
    }

    /// <summary>A short, quotable rendering of the rest of the table, for a failure message.</summary>
    internal static string DescribeParty(IReadOnlyList<PartyOrder> party)
    {
        ArgumentNullException.ThrowIfNull(party);

        return party.Count == 0
            ? "nobody else has ordered"
            : string.Join(
                " | ",
                party.Select(entry => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{entry.BillName}' {entry.TotalText}: {Describe(entry.Lines)}")));
    }

    // --- internals ---------------------------------------------------------------------------------

    /// <summary>
    /// Presses Send and waits for the surface to answer one way or the other (§6.5.9, §11.1).
    ///
    /// <para>Two things are watched at once and either ends the wait: the basket emptying, which is how
    /// §11.1 reports an accepted event, and the rejection panel appearing, which is how it reports a
    /// refused one. Watching only for the confirmation sentence would be wrong in both directions — it
    /// survives from a previous send, so an accepted second send is indistinguishable from no send at
    /// all; and a refusal never writes one, so a refused send could only ever be discovered as a
    /// timeout.</para>
    /// </summary>
    private static async Task<SendOutcome> PressSendAsync(IPage page)
    {
        int stagedBefore = await BasketLineCountAsync(page);
        int removalsBefore = await BasketRemovalCountAsync(page);

        if (stagedBefore + removalsBefore == 0)
        {
            throw new InvalidOperationException(
                "Send was pressed on an empty basket. §11.1 disables the button while the staging area"
                + " is empty, so this would have hung on an element that is never enabled — stage"
                + " something, or tick a line for removal, first.");
        }

        // A refusal panel still on screen from an earlier send would be read as this send's answer on
        // the very first poll. §11.1 clears it whenever the guest edits the basket — StageItem,
        // Unstage and ToggleRemoval all do — and the basket must have been edited for the button to be
        // enabled at all, so one being here means the surface stopped doing that rather than that the
        // scenario was careless. Said plainly, once, instead of mis-reported for the rest of the run.
        IReadOnlyList<string> stale = await ReadRefusalReasonsAsync(page);

        if (stale.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§6.5.9's refusal panel from an earlier send is still on screen at the moment Send"
                    + $" was pressed, so this send's outcome cannot be told apart from the last one's."
                    + $" It says: {string.Join(" | ", stale)}"));
        }

        await page.ClickAsync($"{SurfaceSelector} .order-send button");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + SendPatience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<string> refusals = await ReadRefusalReasonsAsync(page);

            if (refusals.Count > 0)
            {
                return new SendOutcome(false, await ReadConfirmationAsync(page), refusals);
            }

            if (await BasketLineCountAsync(page) == 0 && await BasketRemovalCountAsync(page) == 0)
            {
                return new SendOutcome(true, await ReadConfirmationAsync(page), []);
            }

            await Task.Delay(PollInterval);
        }

        string surface = await DescribeSurfaceAsync(page);
        string basket = await DescribeBasketAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Send neither committed nor refused within {SendPatience.TotalSeconds:F0}s: the basket"
                + $" still holds {basket} and there is no rejection panel, so the click may never have"
                + $" been dispatched at all ({surface}); the browser is at '{page.Url}'."));
    }

    private static async Task<IReadOnlyList<string>> ReadRefusalReasonsAsync(IPage page)
    {
        ILocator reasons = page.Locator(RefusalReasonSelector);

        if (await reasons.CountAsync() == 0)
        {
            return [];
        }

        IReadOnlyList<string> all = await reasons.AllInnerTextsAsync();

        return all.Select(text => text.Trim()).ToArray();
    }

    private static async Task<string?> ReadConfirmationAsync(IPage page)
    {
        ILocator confirmation = page.Locator(ConfirmationSelector);

        return await confirmation.CountAsync() == 0
            ? null
            : (await confirmation.First.InnerTextAsync()).Trim();
    }

    /// <summary>
    /// Which badge a line is wearing, from the chip's class rather than from its words. The two lists
    /// word it differently — "At your table" on your own order, "At the table" on somebody else's — and
    /// both publish <c>chip-ok</c>, which is the state rather than the copy.
    /// </summary>
    /// <summary>
    /// Every price adjustment §11.1 has written under one committed line, oldest first — which is the
    /// order <c>OrderNarrative</c> folds them in, and therefore the order they happened.
    ///
    /// <para>The two amounts are read from the elements that carry them rather than from the sentence:
    /// the previous price is the struck-through <c>&lt;s&gt;</c> and the new one is the <c>&lt;strong&gt;</c>.
    /// A missing element is a real failure rather than a blank — a surface that stopped rendering the old
    /// price would still read perfectly as prose — so this says which half went, and on which line.</para>
    /// </summary>
    private static async Task<IReadOnlyList<GuestPriceAdjustment>> ReadPriceAdjustmentsAsync(ILocator line)
    {
        ILocator paragraphs = line.Locator(PriceAdjustmentSelector);
        int count = await paragraphs.CountAsync();

        if (count == 0)
        {
            return [];
        }

        List<GuestPriceAdjustment> adjustments = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator paragraph = paragraphs.Nth(index);

            string sentence = (await paragraph.InnerTextAsync()).Trim();

            ILocator previous = paragraph.Locator("s");
            ILocator current = paragraph.Locator("strong");

            // Both counts are read into locals first. An await inside an interpolation hole of a string
            // that binds to DefaultInterpolatedStringHandler is CS4007 — the handler is a ref struct and
            // cannot be held across a suspension point — and the message below needs to say which half
            // is missing, so the branch has to be over values rather than over awaits.
            int previousCount = await previous.CountAsync();
            int currentCount = await current.CountAsync();

            if (previousCount == 0 || currentCount == 0)
            {
                string missing = previousCount == 0
                    ? "the struck-through old price"
                    : "the new price";

                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"§11.1 requires a price adjustment to be shown old → new, and this one is"
                        + $" missing {missing}. The sentence on screen reads: '{sentence}'."));
            }

            adjustments.Add(new GuestPriceAdjustment(
                (await previous.First.InnerTextAsync()).Trim(),
                (await current.First.InnerTextAsync()).Trim(),
                sentence));
        }

        return adjustments;
    }

    /// <summary>The adjustments on one line, as a tail for a failure message, or nothing when there are none.</summary>
    private static string DescribeAdjustments(GuestOrderLineDetail line)
    {
        if (line.PriceAdjustments.Count == 0)
        {
            return string.Empty;
        }

        string adjustments = string.Join(
            ", ",
            line.PriceAdjustments.Select(adjustment => string.Create(
                CultureInfo.InvariantCulture,
                $"{adjustment.PreviousPriceText} → {adjustment.NewPriceText}")));

        return string.Create(CultureInfo.InvariantCulture, $" adjusted {adjustments}");
    }

    private static async Task<GuestLineBadge> ReadBadgeAsync(ILocator line)
    {
        if (await line.Locator("span.chip-warn").CountAsync() > 0)
        {
            return GuestLineBadge.Removed;
        }

        return await line.Locator("span.chip-ok").CountAsync() > 0
            ? GuestLineBadge.AtYourTable
            : GuestLineBadge.WithTheKitchen;
    }

    private static async Task<bool> WaitForCountAsync(
        IPage page,
        string selector,
        int expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(selector).CountAsync() == expected)
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    private static async Task<string> DescribeBasketAsync(IPage page)
    {
        BasketContents basket = await ReadBasketAsync(page);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{basket.StagedAdds} staged add(s) and {basket.TickedRemovals} ticked removal(s)");
    }

    /// <summary>
    /// Whatever the picker has to say about why it would not stage the item — §11.1's staging notice,
    /// which is the surface refusing locally (an unavailable item, a quantity outside 1–100) rather than
    /// anything the server has been asked about yet.
    /// </summary>
    private static async Task<string> DescribeStagingRefusalAsync(IPage page)
    {
        ILocator notice = page.Locator($"{SurfaceSelector} .order-picker p.status-error");

        if (await notice.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The picker reports no refusal; the browser is at '{page.Url}'.");
        }

        string message = (await notice.First.InnerTextAsync()).Trim();

        return string.Create(CultureInfo.InvariantCulture, $"The picker says: {message}");
    }

    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        ILocator surface = page.Locator(SurfaceSelector);

        if (await surface.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"there is no ordering surface on the page at all; the browser is at '{page.Url}'");
        }

        string? live = await surface.First.GetAttributeAsync("data-live");

        return string.Create(CultureInfo.InvariantCulture, $"data-live='{live ?? "absent"}'");
    }
}
