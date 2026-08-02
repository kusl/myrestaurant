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
/// The guest ordering journeys the §16.3 scenarios walk: staging items into the basket, sending them,
/// and reading what came back — plus who else is at the table and what they have ordered
/// (TECHNICAL_SPECIFICATION §6.5, §9, §11.1).
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

    /// <summary>The committed lines — what has actually reached the kitchen (§11.1's "Sent to the kitchen").</summary>
    private const string CommittedLineSelector = "#table-order-surface ul.order-lines li.order-line";

    /// <summary>§11.1's "Who is here" list — every member of the sitting, in join order (§5.2).</summary>
    private const string RosterMemberSelector = "#table-order-surface ul.table-roster > li";

    /// <summary>§11.1's "The rest of the table" — one entry per <em>other</em> person who has ordered.</summary>
    private const string PartyOrderSelector = "#table-order-surface ul.order-party > li.order-party-order";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

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

        if (await WaitForBasketCountAsync(page, before + 1, TimeSpan.FromSeconds(15)))
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
    /// Presses Send (§11.1) and returns the confirmation the surface rendered — the sentence that names
    /// how many lines went, which is worth returning rather than merely waiting for because a send of
    /// two adds and a send of one read identically to a selector.
    ///
    /// <para>A refusal is §6.5.9's all-or-nothing panel: nothing was written, the basket is untouched,
    /// and each refused operation carries its own reason. Those reasons are the failure message here,
    /// because "Send did not confirm" without them is a mystery and with them is usually the answer.</para>
    /// </summary>
    internal static async Task<string> SendAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.ClickAsync($"{SurfaceSelector} .order-send button");

        // Scoped to the island: the parent page's own "You have joined …" success line is also a
        // p.status-success and is still on screen from the join that got us here.
        ILocator confirmation = page.Locator($"{SurfaceSelector} p.status-success").First;

        try
        {
            await confirmation.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeSendRefusalAsync(page);

            throw new InvalidOperationException(
                $"The send was not confirmed. {refusal}",
                exception);
        }

        return (await confirmation.InnerTextAsync()).Trim();
    }

    /// <summary>How many staged adds are sitting in the basket right now.</summary>
    internal static Task<int> BasketLineCountAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Locator(BasketLineSelector).CountAsync();
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

    private static async Task<bool> WaitForBasketCountAsync(IPage page, int expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(BasketLineSelector).CountAsync() == expected)
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
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

    /// <summary>
    /// §6.5.9's refusal panel, flattened into one sentence: the headline plus every per-operation reason.
    /// A send is accepted or refused as a whole, so all of them are the diagnosis, not just the first.
    /// </summary>
    private static async Task<string> DescribeSendRefusalAsync(IPage page)
    {
        ILocator reasons = page.Locator($"{SurfaceSelector} ul.order-reject-list li");
        int count = await reasons.CountAsync();

        if (count == 0)
        {
            string surface = await DescribeSurfaceAsync(page);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"The surface shows no refusal either ({surface}), so the send may simply never have"
                + $" been dispatched; the browser is at '{page.Url}'.");
        }

        IReadOnlyList<string> all = await reasons.AllInnerTextsAsync();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Nothing was written and the surface refused it: {string.Join(" | ", all.Select(text => text.Trim()))}");
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
