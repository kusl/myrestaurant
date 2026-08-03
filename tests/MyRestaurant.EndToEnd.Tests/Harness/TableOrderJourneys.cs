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

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no committed line for '{menuItemName}' on this order."
                + $" It holds: {Describe(await ReadCommittedLinesAsync(page))}."));
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

        ILocator lines = page.Locator(CommittedLineSelector);
        int count = await lines.CountAsync();

        List<GuestOrderLine> committed = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);

            // "2 × Soup of the day" — the quantity and the name are one span, deliberately, because
            // that is how §11.1 words it. The scenarios match on containment, so it is kept whole.
            string name = (await line.Locator("span.order-line-name").First.InnerTextAsync()).Trim();

            committed.Add(new GuestOrderLine(name, await ReadBadgeAsync(line)));
        }

        return committed;
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
