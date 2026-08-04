using System.Globalization;
using Microsoft.Playwright;
using MyRestaurant.WebApplication.Orders;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// One line on a bill at the till, as §11.3 renders it.
///
/// <para><b>Both money fields are text, deliberately.</b> They are rendered through
/// <c>MoneyText.Format(amount, CurrencyCode)</c>, and parsing them back into decimals here would mean
/// reimplementing a currency formatter inside a test in order to compare against a number the test
/// already knew. A scenario formats its expectation the same way the surface did — see
/// <see cref="RestaurantInstance.CurrencyCode"/> — and compares strings, which is a stricter assertion
/// than comparing decimals because it catches a formatter that has started dropping a symbol.</para>
///
/// <para><paramref name="UnitPriceText"/> is the one §16.3 scenario 9 turns on. A price adjustment
/// (§6.5.7) changes the <em>unit</em> price, and the extension is recomputed from it — so a line at
/// quantity two is the only shape in which "the adjustment landed" and "the bill was recalculated" are
/// separable claims.</para>
/// </summary>
internal sealed record CounterBillLine(
    int Quantity,
    string Name,
    string LineTotalText,
    string UnitPriceText,
    string? Note,
    bool IsDelivered);

/// <summary>One person's part of a bill (§8.3's <c>sitting_bill</c> grouping) with their lines under it.</summary>
internal sealed record CounterBillEntry(
    string BillName,
    string PersonTotalText,
    IReadOnlyList<CounterBillLine> Lines);

/// <summary>
/// A whole bill at the till, as one instant of §11.3.
///
/// <para><paramref name="RunningTotalText"/> is the header figure — <c>CounterSittingSummary.AmountToShow</c>,
/// which for an open sitting is the running total. It is read here rather than the settle panel's
/// "Table total" because the two are computed by different code on different sides of the screen: the
/// header comes straight from the <c>sitting_bill</c> view, in SQL, while the settle panel sums the
/// per-person entries in C#. The SQL one is the genuinely independent opinion when the thing being
/// checked against it is a guest's own event fold.</para>
/// </summary>
internal sealed record CounterBill(
    string TableLabel,
    string RunningTotalText,
    IReadOnlyList<CounterBillEntry> People);

/// <summary>
/// §5.3's pre-close warning, as §11.3 renders it: "the counter UI must surface still-pending lines
/// prominently <b>before</b> offering Close (remove with reason, or knowingly charge)".
///
/// <para><paramref name="LineCount"/> is parsed off the front of the sentence rather than counted from
/// the bill, and that is the point of reading it at all — the bill and the warning are two different
/// numbers until something proves they are the same one. §11.3 renders the count from
/// <c>CounterSittingSummary.PendingLineCount</c>, which is a <c>NOT is_fulfilled</c> count in SQL; a
/// scenario counting undelivered chips on screen is counting the C# projection. A warning naming the
/// wrong number is a warning that gets a table charged for food it did get, or discharged for food it
/// did not.</para>
/// </summary>
internal sealed record CounterPendingWarning(int LineCount, string Sentence);

/// <summary>
/// §11.3's confirmation prompt, between pressing Close &amp; settle and meaning it.
///
/// <para><paramref name="AmountText"/> is the <c>&lt;strong&gt;</c> the prompt quotes, and it is worth a
/// scenario's attention because it is a <em>third</em> reading of the total: the header shows
/// <c>AmountToShow</c>, the settle panel sums the per-person entries in C#, and this quotes
/// <c>CurrentTotalAmount</c> directly. A prompt that asked somebody to confirm a different number from
/// the one about to be stamped would be the worst possible place for the three to disagree — it is the
/// last thing a person reads before an irreversible write.</para>
/// </summary>
internal sealed record CloseConfirmation(string AmountText, string Sentence);

/// <summary>
/// A settled sitting as the till renders it — §11.3's "closed-sitting lookup (read-only)", which is the
/// same page rather than a second one.
///
/// <para><b>Most of these fields are absences, and they are counted rather than asserted one at a
/// time.</b> §6.5.8 admits nothing but an administrator's corrective events after a close, so every
/// control §11.3 offers on an open sitting has to be gone: the per-line Adjust and Remove, the staff-add
/// panel, and Close itself. A settled sitting still showing an Adjust button would be a door that only
/// ever answers no, and the harness reports how many are left rather than which, because the answer that
/// matters is zero.</para>
///
/// <para><paramref name="TotalLabel"/> is read alongside <paramref name="TotalText"/> deliberately.
/// <c>CounterSittingSummary.AmountToShow</c> feeds one element in both states — the running total while
/// open, the stamped total once closed — so the amount alone cannot say which it is, and a close that
/// stamped nothing would leave a screen that looks entirely correct.</para>
///
/// <para><paramref name="ShowsCorrection"/> is expected <em>false</em> by any scenario that has not made
/// a §6.7 correction. §5.3 shows both numbers only "when corrective events exist"; a settled total
/// carrying a "corrected to" figure minutes after a close would mean the stamped value and the live one
/// had already diverged, which is the one thing §5.3 promises cannot happen on its own.</para>
/// </summary>
internal sealed record SettledTill(
    string TotalLabel,
    string TotalText,
    string TableTotalText,
    string HeaderMeta,
    string? Notice,
    bool SaysReadOnly,
    bool ShowsCorrection,
    int LineControlCount,
    bool OffersClose,
    bool OffersStaffAdd);

/// <summary>One row of §11.3's "Settled today" list on the counter board.</summary>
internal sealed record SettledTableRow(string TableLabel, string AmountText, string SettledBy);

/// <summary>
/// The counter board at one instant: which tables are open, and which have been settled recently.
///
/// <para>Both halves together, because "the table flipped to settled" is a claim about the pair. A table
/// appearing under Settled today while still on the floor would mean two rows for one sitting; a table
/// gone from the floor and absent from both lists would mean it had vanished. Reading one list would
/// pass for either.</para>
/// </summary>
internal sealed record CounterFloor(
    IReadOnlyList<string> OpenTableLabels,
    IReadOnlyList<SettledTableRow> Settled);

/// <summary>
/// The journeys a counter walks at the till: finding an open table on the board, opening its bill, and
/// adjusting a price with a reason (TECHNICAL_SPECIFICATION §5.3, §6.5.7, §11.3).
///
/// <para><b>Every surface here needs a circuit, and none of them says so on its own.</b>
/// <c>/counter</c> and <c>/counter/sittings/{id}</c> are interactive-server pages rather than static
/// SSR, and every control on the second one — Adjust price, Remove, Add to the bill, Close &amp; settle
/// — is an <c>@onclick</c>. A prerendered till is the dangerous kind of broken because it is the kind
/// that looks right: the bill is correct as of the request, every total adds up, and pressing anything
/// does nothing at all. So <see cref="OpenSittingAsync"/> waits on <c>data-live</c>, published by
/// <c>CounterSitting.razor</c> as of M6 Slice 12 for exactly this reason.</para>
///
/// <para><b>Why the board's link is followed rather than typed.</b> A scenario knows the sitting
/// identifier only if it reads the database for it, and §16.3's "counter adjusts a price" means the
/// counter found the table — the board, the open-sittings query, and the link. Following it also means
/// the scenario can cross-check the identifier it landed on against the row, which is how "opened the
/// right sitting" is told apart from "opened a sitting". The click goes through
/// <see cref="EnhancedNavigation"/> because <c>#counter-sitting-surface</c> is genuinely absent from
/// the board, which makes it an exact barrier rather than a delay.</para>
///
/// <para><b>The close is two calls, and that is not an accident of style.</b> §11.3 puts a confirmation
/// between the button and the write, and the prompt quotes the amount about to be stamped — the last
/// number a person reads before something §5.3 says cannot be undone. A single method would settle the
/// table before a scenario could read it, and a settled sitting offers no prompt to go back for. So
/// <see cref="BeginCloseAsync"/> returns the prompt and <see cref="ConfirmCloseAsync"/> accepts it, on
/// the same reasoning that keeps <c>SignInWithPasswordAsync</c> and
/// <c>CompleteForcedPasswordChangeAsync</c> apart.</para>
///
/// <para><b>Why an adjustment is judged by the unit price rather than by the confirmation.</b> §11.3
/// writes a flash sentence naming the new price, and that sentence survives until something clears it —
/// so a second adjustment of the same shape is satisfied by the first one's words. The unit price on
/// the line is the state the transaction actually wrote, re-read from <c>order_current_line</c>, and it
/// cannot be left over from anything. A refusal ends the wait immediately with the surface's own reason,
/// because every button here goes through <c>IOrderWorkflow</c> and can be refused under the §6.6 lock
/// — a guest sending, the kitchen fulfilling, somebody closing a second earlier — and the board renders
/// that refusal rather than throwing.</para>
/// </summary>
internal static class CounterJourneys
{
    /// <summary>The board's route. <c>CounterBoard.razor</c> is <c>@page "/counter"</c>.</summary>
    internal const string BoardPath = "/counter";

    private const string BoardSurfaceSelector = "section.counter-board";

    /// <summary>One open table on the board (§11.3). Settled ones are rows in a list, not articles.</summary>
    private const string OpenSittingSelector = "section.counter-board article.counter-sitting";

    private const string SittingSurfaceSelector = "#counter-sitting-surface";

    /// <summary>
    /// The bill as rendered by a live circuit. <c>CounterSitting.razor</c> sets <c>data-live</c> from
    /// <c>RendererInfo.IsInteractive</c>, so this matches only markup an interactive renderer produced —
    /// never the prerendered pass, which is identical in every other respect.
    /// </summary>
    private const string LiveSittingSurfaceSelector = "#counter-sitting-surface[data-live='true']";

    private const string BillEntrySelector = "#counter-sitting-surface article.counter-person";
    private const string BillLineSelector = "li.counter-line";

    /// <summary>The two ids <c>CounterSitting.razor</c>'s price editor carries (M6 Slice 12).</summary>
    private const string AdjustPriceFieldSelector = "#counter-adjust-price";
    private const string AdjustReasonFieldSelector = "#counter-adjust-reason";

    /// <summary>
    /// The two ids <c>CounterSitting.razor</c>'s close buttons carry (M6 Slice 13). They live in
    /// exclusive branches — <c>_confirmingClose</c> chooses — so at most one is ever in the document, and
    /// which one is on screen <em>is</em> the step the till is on.
    /// </summary>
    private const string CloseButtonSelector = "#counter-close";
    private const string ConfirmCloseButtonSelector = "#counter-close-confirm";

    private const string PendingWarningSelector = "#counter-sitting-surface p.counter-pending-warning";
    private const string CloseConfirmSelector = "#counter-sitting-surface p.counter-settle-confirm";

    /// <summary>
    /// §11.3's read-only note, rendered from <c>!_sitting.IsOpen</c>. The state rather than the copy: it
    /// exists if and only if <c>closed_at</c> is set, which makes its arrival the barrier a close is
    /// waited on.
    /// </summary>
    private const string ReadOnlyNoteSelector = "#counter-sitting-surface p.counter-readonly";

    private const string TotalLabelSelector = "#counter-sitting-surface span.counter-detail-total-label";
    private const string TotalAmountSelector = "#counter-sitting-surface span.counter-detail-total-amount";
    private const string CorrectedTotalSelector = "#counter-sitting-surface span.counter-detail-corrected";
    private const string SettlePanelTotalSelector = "#counter-sitting-surface .counter-settle-total strong";
    private const string HeaderMetaSelector = "#counter-sitting-surface p.counter-detail-meta";
    private const string NoticeSelector = "#counter-sitting-surface p.status-success";
    private const string LineActionsSelector = "#counter-sitting-surface div.counter-line-actions";
    private const string StaffAddSelector = "#counter-sitting-surface section.counter-add";

    private const string SettledRowSelector = "section.counter-board li.counter-settled-row";

    /// <summary>The path prefix a sitting's own URL starts with, for recovering the identifier.</summary>
    private const string SittingPathPrefix = "/counter/sittings/";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long one press of Adjust has to produce an answer. One transaction against a local PostgreSQL
    /// under an advisory lock nothing else is holding, so the honest expectation is milliseconds; thirty
    /// seconds is the same patience every other page operation in this harness gets. Either outcome ends
    /// the wait, so this length is only ever reached when the click did not dispatch at all.
    /// </summary>
    private static readonly TimeSpan AdjustmentPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long §5.3's close has to produce an answer. The same thirty seconds every other page operation
    /// here gets, and for once the number is worth a sentence: this transaction takes <c>FOR UPDATE</c>
    /// on the sitting row, and §6.6 has every order writer hold <c>FOR SHARE</c> on the same row — so a
    /// close genuinely can wait on a send that is mid-flight. Nothing else in a scenario that has just
    /// read a settled bill is writing, so in practice this is milliseconds; the length is here so that a
    /// timeout means the click never dispatched rather than that the lock was contended.
    /// </summary>
    private static readonly TimeSpan ClosePatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Opens the bill for the table labelled <paramref name="tableLabel"/> from the counter board and
    /// returns the sitting identifier the URL landed on, once a circuit is behind the page.
    ///
    /// <para>The table is found by the heading on its card rather than by putting the label into a CSS
    /// selector: labels are free text, an apostrophe in "Chef's table" would break a
    /// <c>:text-is('…')</c> selector, and a scenario is not the place to learn about selector
    /// escaping.</para>
    /// </summary>
    internal static async Task<Guid> OpenSittingAsync(IPage page, string tableLabel, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        await page.GotoAsync(BoardPath);
        await WaitForBoardAsync(page, timeout);

        ILocator cards = page.Locator(OpenSittingSelector);
        int count = await cards.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator card = cards.Nth(index);
            string label = (await card.Locator("h2").First.InnerTextAsync()).Trim();

            if (!string.Equals(label, tableLabel, StringComparison.Ordinal))
            {
                continue;
            }

            await EnhancedNavigation.FollowAsync(
                page,
                card.Locator("a:has-text('Bill')").First,
                SittingSurfaceSelector,
                "the sitting's bill at the till",
                timeout);

            await WaitForLiveSittingAsync(page, timeout);

            return SittingIdentifierFrom(page.Url);
        }

        // Read before composing: an await inside an interpolation hole of a string that binds to
        // DefaultInterpolatedStringHandler is CS4007, because the handler is a ref struct.
        string board = await DescribeBoardAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The counter board has no open table labelled '{tableLabel}'. §5.1 opens a sitting on"
                + $" the first join and §11.3 lists every open one, so either nobody has joined that"
                + $" table or it has already been settled. What the board shows: {board}."));
    }

    /// <summary>
    /// Waits until the counter board on screen was rendered by a live circuit. A board that never
    /// became interactive lists the floor as it stood at the moment of the request and then never
    /// changes — which for a screen whose whole job is to show a total moving is the failure that looks
    /// most like success.
    /// </summary>
    internal static async Task WaitForBoardAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.Locator(BoardSurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The counter board never rendered within {timeout.TotalSeconds:F0}s. §3.7 admits"
                    + $" counter and administrator to /counter, so a principal that failed the policy"
                    + $" would be looking at the access-denied panel instead; the browser is at"
                    + $" '{page.Url}'."),
                exception);
        }
    }

    /// <summary>
    /// Waits until the bill on screen was rendered by a live circuit rather than by prerendering. Every
    /// other method here assumes it.
    /// </summary>
    internal static async Task WaitForLiveSittingAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.Locator(LiveSittingSurfaceSelector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = (float)timeout.TotalMilliseconds,
            });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSurfaceAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The till never became interactive within {timeout.TotalSeconds:F0}s; it is still"
                    + $" the prerendered markup ({surface}). The bill will read correctly and every"
                    + $" control on it will do nothing — Adjust price, Remove, Add to the bill and"
                    + $" Close & settle are all @onclick handlers with no circuit behind them, and the"
                    + $" screen will not hear §9 either. Check that /_framework/blazor.web.js is served"
                    + $" (RestaurantInstance probes it at startup) and that the browser reached"
                    + $" /_blazor."),
                exception);
        }
    }

    /// <summary>
    /// Adjusts one line's unit price with a reason — §11.3's "price adjustment dialog (new price +
    /// required reason)" — and returns once the bill itself shows the new unit price.
    ///
    /// <para>The price is typed invariantly, because <c>CounterSitting.razor</c> parses it invariantly:
    /// the amount is a <c>numeric(10,2)</c>, and which separator the container's locale happens to use is
    /// not a decision anybody made about this restaurant.</para>
    ///
    /// <para>Both fields are <c>@bind:event="oninput"</c>, so no blur is needed to dispatch them — unlike
    /// the guest's picker, where the default <c>onchange</c> is why
    /// <see cref="TableOrderJourneys.StageAsync"/> has to move focus before clicking.</para>
    /// </summary>
    internal static async Task AdjustPriceAsync(
        IPage page,
        string menuItemName,
        decimal newUnitPrice,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator line = await LocateLineAsync(page, menuItemName, "adjust");

        await line.Locator("button:has-text('Adjust price')").First.ClickAsync();

        ILocator priceField = page.Locator(AdjustPriceFieldSelector);

        try
        {
            await priceField.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string refusal = await DescribeRefusalAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pressing Adjust price on '{menuItemName}' did not open the editor. §11.3 renders"
                    + $" the line's controls only while the sitting is open, so a settled sitting offers"
                    + $" none of them (§6.5.8 admits nothing but an administrator's corrective events"
                    + $" after a close). {refusal}"),
                exception);
        }

        string expectedUnitPrice = Money(newUnitPrice);

        await priceField.FillAsync(newUnitPrice.ToString("0.00", CultureInfo.InvariantCulture));
        await page.FillAsync(AdjustReasonFieldSelector, reason);

        await page.ClickAsync($"{SittingSurfaceSelector} .counter-editor button:has-text('Adjust')");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + AdjustmentPatience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            // The refusal is looked for first. §11.3 shows both a notice and a problem through the same
            // re-read, and an adjustment that was refused under the §6.6 lock leaves the unit price
            // exactly as it was — so a poll that only watched the price would spend the whole patience
            // failing to notice that the answer had already arrived.
            IReadOnlyList<string> refusals = await ReadRefusalReasonsAsync(page);

            if (refusals.Count > 0)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Adjusting '{menuItemName}' to {expectedUnitPrice} was refused, so nothing was"
                        + $" written (§6.5.9 is all-or-nothing at the granularity of the event). The till"
                        + $" says: {string.Join(" | ", refusals)}"));
            }

            CounterBillLine? current = await FindLineAsync(page, menuItemName);

            if (current is not null
                && string.Equals(current.UnitPriceText, expectedUnitPrice, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        string bill = Describe(await ReadBillAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Adjusting '{menuItemName}' to {expectedUnitPrice} each neither took effect nor was"
                + $" refused within {AdjustmentPatience.TotalSeconds:F0}s, so the click may never have"
                + $" been dispatched at all. The bill holds: {bill}."));
    }

    /// <summary>
    /// §5.3's still-pending-lines warning, or <c>null</c> when the till is not showing one.
    ///
    /// <para><b>Null is a real answer and callers should expect to assert on it.</b> §11.3 renders this
    /// only while <c>HasPendingLines</c> holds, so its absence on a fully delivered table is correct and
    /// its absence on a table with food still on the pass is the §5.3 defect — a counter settling a bill
    /// without being told what has not arrived. Returning <c>null</c> rather than throwing is what lets a
    /// scenario say which of those it is looking at.</para>
    ///
    /// <para>The count is read out of the leading <c>&lt;strong&gt;</c>, which §11.3 words as
    /// "N lines are still with the kitchen." — the number first, then a verb agreeing with it. Taking the
    /// leading integer is the same treatment <see cref="ReadLineAsync"/> gives the quantity's "2×": the
    /// digits are the data and the words around them are markup.</para>
    /// </summary>
    internal static async Task<CounterPendingWarning?> ReadPendingWarningAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator warning = page.Locator(PendingWarningSelector);

        if (await warning.CountAsync() == 0)
        {
            return null;
        }

        string sentence = (await warning.First.InnerTextAsync()).Trim();
        string headline = (await warning.First.Locator("strong").First.InnerTextAsync()).Trim();

        return new CounterPendingWarning(LeadingCount(headline), sentence);
    }

    /// <summary>
    /// Presses Close &amp; settle and returns §11.3's confirmation prompt — <em>without</em> settling
    /// anything.
    ///
    /// <para><b>Two methods rather than one, because the prompt is an assertion.</b> The amount it quotes
    /// is the last number a person reads before a write that §5.3 says cannot be undone, and it is
    /// computed by different code from the header above it. A composite
    /// <c>CloseAndSettleAsync</c> would settle the table before a scenario could look at it — and there
    /// is no second chance to read a confirmation prompt for a sitting that is already closed. The same
    /// reasoning kept <c>SignInWithPasswordAsync</c> and <c>CompleteForcedPasswordChangeAsync</c> apart in
    /// Slice 12.</para>
    ///
    /// <para>A refusal cannot arrive here: <c>BeginClose</c> is a field assignment and one render, with no
    /// transaction behind it. So the only failure mode is that the button was not there — a sitting
    /// already settled by somebody else offers none — and that is what the message says.</para>
    /// </summary>
    internal static async Task<CloseConfirmation> BeginCloseAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator close = page.Locator(CloseButtonSelector);

        try
        {
            await close.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSettleSectionAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The till is not offering Close & settle. §11.3 renders it only while the sitting is"
                    + $" open, so the likeliest cause is that this sitting has already been settled —"
                    + $" by an end-of-day pass (§5.4) or by somebody else at another till. {surface}"),
                exception);
        }

        await close.First.ClickAsync();

        ILocator prompt = page.Locator(CloseConfirmSelector);

        try
        {
            await prompt.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSettleSectionAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pressing Close & settle did not raise §11.3's confirmation. The button is an"
                    + $" @onclick that sets one field, so nothing can refuse it — if the prompt is absent"
                    + $" the click was not dispatched, which means no circuit. {surface}"),
                exception);
        }

        string sentence = (await prompt.First.InnerTextAsync()).Trim();
        ILocator amount = prompt.First.Locator("strong");

        if (await amount.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"§11.3's confirmation names no amount, so nobody pressing Yes would know what they"
                    + $" were agreeing to. It reads: '{sentence}'."));
        }

        return new CloseConfirmation((await amount.First.InnerTextAsync()).Trim(), sentence);
    }

    /// <summary>
    /// Confirms the close (§5.3) and returns once the till has flipped to §11.3's read-only settled view.
    ///
    /// <para>The barrier is the read-only note, which <c>CounterSitting.razor</c> renders from
    /// <c>!_sitting.IsOpen</c> — that is, from <c>closed_at</c> being set on the row this page re-read
    /// after the transaction committed. Waiting on the confirmation sentence instead would be wrong in
    /// the usual way: <c>_notice</c> survives until something clears it, so a second close of the same
    /// shape is satisfied by the first one's words.</para>
    ///
    /// <para>A refusal ends the wait immediately. <c>CloseSittingOutcome.SittingNotFound</c> writes a
    /// problem and leaves the page open; <c>AlreadyClosed</c> writes a notice and <em>does</em> flip the
    /// view, because the sitting really is settled — so this returns normally for the second and names
    /// the first. A scenario that cares which of its own close it observed has the notice text.</para>
    /// </summary>
    internal static async Task<SettledTill> ConfirmCloseAsync(IPage page, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator confirm = page.Locator(ConfirmCloseButtonSelector);

        try
        {
            await confirm.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (PlaywrightException exception)
        {
            string surface = await DescribeSettleSectionAsync(page);

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is no confirmation to accept — §11.3's prompt is not on screen, so"
                    + $" BeginCloseAsync either was not called or its prompt has since been abandoned."
                    + $" {surface}"),
                exception);
        }

        await confirm.First.ClickAsync();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            // The read-only note is looked for first, because AlreadyClosed produces both it and a
            // notice while SittingNotFound produces a problem and no flip — so a poll that watched the
            // problem first would report a losing race as a fault.
            if (await page.Locator(ReadOnlyNoteSelector).CountAsync() > 0)
            {
                return await ReadSettledTillAsync(page);
            }

            IReadOnlyList<string> refusals = await ReadRefusalReasonsAsync(page);

            if (refusals.Count > 0)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The close was refused and the sitting is still open, so no total was stamped."
                        + $" The till says: {string.Join(" | ", refusals)}"));
            }

            await Task.Delay(PollInterval);
        }

        string settle = await DescribeSettleSectionAsync(page);

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The sitting neither settled nor refused within {timeout.TotalSeconds:F0}s. §5.3 takes"
                + $" FOR UPDATE on the sitting row and §6.6 has every order writer take FOR SHARE on the"
                + $" same one, so a genuinely contended close waits — but nothing else here is writing."
                + $" {settle}"));
    }

    /// <summary>
    /// The till's reading of a settled sitting: what it says the total is, what it calls that total, and
    /// how much of §11.3's open-sitting apparatus is left on screen.
    ///
    /// <para>Safe to call on an open sitting, and worth doing: every field is then the other value —
    /// "Running total", no read-only note, line controls present — which is how a scenario establishes
    /// that a flip happened rather than that the page always looked settled.</para>
    /// </summary>
    internal static async Task<SettledTill> ReadSettledTillAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator notice = page.Locator(NoticeSelector);

        return new SettledTill(
            (await page.Locator(TotalLabelSelector).First.InnerTextAsync()).Trim(),
            (await page.Locator(TotalAmountSelector).First.InnerTextAsync()).Trim(),
            (await page.Locator(SettlePanelTotalSelector).First.InnerTextAsync()).Trim(),
            (await page.Locator(HeaderMetaSelector).First.InnerTextAsync()).Trim(),
            await notice.CountAsync() > 0 ? (await notice.First.InnerTextAsync()).Trim() : null,
            await page.Locator(ReadOnlyNoteSelector).CountAsync() > 0,
            await page.Locator(CorrectedTotalSelector).CountAsync() > 0,
            await page.Locator(LineActionsSelector).CountAsync(),
            await page.Locator(CloseButtonSelector).CountAsync() > 0
                || await page.Locator(ConfirmCloseButtonSelector).CountAsync() > 0,
            await page.Locator(StaffAddSelector).CountAsync() > 0);
    }

    /// <summary>
    /// The counter board's two lists (§11.3), read from a page already on <see cref="BoardPath"/>.
    ///
    /// <para>This does not navigate, deliberately. A scenario that has just settled a table wants to know
    /// what the board says <em>when a counter goes back to it</em>, and making the navigation the
    /// caller's own step keeps "I went back to the floor" visible in the scenario rather than hidden
    /// inside a reader.</para>
    /// </summary>
    internal static async Task<CounterFloor> ReadFloorAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator cards = page.Locator(OpenSittingSelector);
        int openCount = await cards.CountAsync();

        List<string> open = new(openCount);

        for (int index = 0; index < openCount; index++)
        {
            open.Add((await cards.Nth(index).Locator("h2").First.InnerTextAsync()).Trim());
        }

        ILocator rows = page.Locator(SettledRowSelector);
        int settledCount = await rows.CountAsync();

        List<SettledTableRow> settled = new(settledCount);

        for (int index = 0; index < settledCount; index++)
        {
            ILocator row = rows.Nth(index);

            string label = (await row.Locator("a.counter-settled-name").First.InnerTextAsync()).Trim();
            string when = (await row.Locator("span.counter-settled-when").First.InnerTextAsync()).Trim();

            // The amount block nests §5.3's "now …" corrected figure when §6.7 events exist, exactly as
            // a bill line nests its unit price — so the child's text is removed from the parent's by the
            // same means rather than by splitting on a line break.
            string amountBlock =
                (await row.Locator("div.counter-settled-amount").First.InnerTextAsync()).Trim();

            ILocator corrected = row.Locator("span.counter-settled-corrected");

            string correctedBlock = await corrected.CountAsync() > 0
                ? (await corrected.First.InnerTextAsync()).Trim()
                : string.Empty;

            settled.Add(new SettledTableRow(label, WithoutUnitPrice(amountBlock, correctedBlock), when));
        }

        return new CounterFloor(open, settled);
    }

    /// <summary>The whole bill on screen right now, read in one pass (§11.3).</summary>
    internal static async Task<CounterBill> ReadBillAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        ILocator surface = page.Locator(SittingSurfaceSelector).First;

        string tableLabel = (await surface.Locator("h1").First.InnerTextAsync()).Trim();
        string runningTotal =
            (await surface.Locator("span.counter-detail-total-amount").First.InnerTextAsync()).Trim();

        ILocator entries = page.Locator(BillEntrySelector);
        int entryCount = await entries.CountAsync();

        List<CounterBillEntry> people = new(entryCount);

        for (int index = 0; index < entryCount; index++)
        {
            ILocator entry = entries.Nth(index);

            string billName = (await entry.Locator("h2").First.InnerTextAsync()).Trim();
            string personTotal =
                (await entry.Locator("span.counter-person-total").First.InnerTextAsync()).Trim();

            ILocator lines = entry.Locator(BillLineSelector);
            int lineCount = await lines.CountAsync();

            List<CounterBillLine> theirLines = new(lineCount);

            for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                theirLines.Add(await ReadLineAsync(lines.Nth(lineIndex)));
            }

            people.Add(new CounterBillEntry(billName, personTotal, theirLines));
        }

        return new CounterBill(tableLabel, runningTotal, people);
    }

    /// <summary>A short, quotable rendering of a bill, for a failure message.</summary>
    internal static string Describe(CounterBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        if (bill.People.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"'{bill.TableLabel}' at {bill.RunningTotalText}, nobody has ordered");
        }

        string people = string.Join(
            " | ",
            bill.People.Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"'{entry.BillName}' {entry.PersonTotalText}: {DescribeLines(entry.Lines)}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"'{bill.TableLabel}' at {bill.RunningTotalText} — {people}");
    }

    /// <summary>A short, quotable rendering of a set of bill lines, for a failure message.</summary>
    internal static string DescribeLines(IReadOnlyList<CounterBillLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Count == 0
            ? "nothing on this order right now"
            : string.Join(
                "; ",
                lines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{line.Quantity} × '{line.Name}' {line.LineTotalText} ({line.UnitPriceText} each)"
                    + $" [{(line.IsDelivered ? "delivered" : "with the kitchen")}]")));
    }

    /// <summary>A short, quotable rendering of a settled till, for a failure message.</summary>
    internal static string DescribeSettled(SettledTill till)
    {
        ArgumentNullException.ThrowIfNull(till);

        string leftovers = till.LineControlCount == 0 && !till.OffersClose && !till.OffersStaffAdd
            ? "no open-sitting controls"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{till.LineControlCount} line control block(s)"
                + $"{(till.OffersClose ? ", a close button" : string.Empty)}"
                + $"{(till.OffersStaffAdd ? ", the staff-add panel" : string.Empty)}");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"'{till.TotalLabel}' {till.TotalText}, settle panel {till.TableTotalText},"
            + $" read-only note {(till.SaysReadOnly ? "present" : "absent")},"
            + $" correction {(till.ShowsCorrection ? "shown" : "not shown")}, {leftovers};"
            + $" header says '{till.HeaderMeta}'; notice {till.Notice ?? "(none)"}");
    }

    /// <summary>A short, quotable rendering of the counter board's two lists, for a failure message.</summary>
    internal static string DescribeFloor(CounterFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);

        string open = floor.OpenTableLabels.Count == 0
            ? "nothing open"
            : string.Join(", ", floor.OpenTableLabels.Select(label => $"'{label}'"));

        string settled = floor.Settled.Count == 0
            ? "nothing settled"
            : string.Join(
                ", ",
                floor.Settled.Select(row => string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{row.TableLabel}' {row.AmountText} ({row.SettledBy})")));

        return string.Create(CultureInfo.InvariantCulture, $"open: {open}; settled today: {settled}");
    }

    // --- internals ---------------------------------------------------------------------------------

    /// <summary>
    /// The leading integer of a sentence that begins with one, or <c>0</c> when it does not. §11.3 writes
    /// the pending-line warning as "N lines are still with the kitchen."; zero is not a value that
    /// sentence can carry, because the warning is not rendered at all when nothing is pending — so it is
    /// unambiguous as "the copy changed shape and this needs looking at".
    /// </summary>
    private static int LeadingCount(string sentence)
    {
        int end = 0;

        while (end < sentence.Length && char.IsAsciiDigit(sentence[end]))
        {
            end++;
        }

        return end > 0
            && int.TryParse(
                sentence[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? count
            : 0;
    }

    /// <summary>
    /// What §11.3's settle section is currently offering, for a failure message. The three states it can
    /// be in — offering Close, holding a confirmation, or settled and offering neither — are exactly what
    /// distinguishes the ways a close goes wrong.
    /// </summary>
    private static async Task<string> DescribeSettleSectionAsync(IPage page)
    {
        ILocator section = page.Locator("#counter-sitting-surface section.counter-settle");

        if (await section.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"There is no settle panel on the page at all; the browser is at '{page.Url}'.");
        }

        bool offersClose = await page.Locator(CloseButtonSelector).CountAsync() > 0;
        bool confirming = await page.Locator(ConfirmCloseButtonSelector).CountAsync() > 0;
        bool readOnly = await page.Locator(ReadOnlyNoteSelector).CountAsync() > 0;
        string total = (await page.Locator(SettlePanelTotalSelector).First.InnerTextAsync()).Trim();

        string state = (offersClose, confirming, readOnly) switch
        {
            (true, _, _) => "it is offering Close & settle",
            (_, true, _) => "it is holding a confirmation prompt",
            (_, _, true) => "the sitting is already settled",
            _ => "it offers nothing and does not say the sitting is settled, which should be impossible",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The settle panel reads {total} and {state}; the browser is at '{page.Url}'.");
    }

    /// <summary>
    /// Reads one bill line. The two money figures live in one element and are separated here rather than
    /// at the two selectors, because §11.3 nests the unit price <em>inside</em> the price block —
    /// <c>span.counter-line-price</c> contains both "$22.00" and its child
    /// <c>span.counter-line-unit</c>'s "$11.00 each", so its own inner text carries them together.
    /// Removing the child's text from the parent's is exact and does not depend on how a flex column
    /// happens to be turned into line breaks.
    /// </summary>
    private static async Task<CounterBillLine> ReadLineAsync(ILocator line)
    {
        // "2×" — the multiplication sign is markup rather than data, so it is trimmed off rather than
        // parsed around. The same treatment KitchenJourneys gives the kitchen's own quantity.
        string quantityText = (await line.Locator("span.counter-line-quantity").First.InnerTextAsync())
            .Trim()
            .TrimEnd('×');

        string name = (await line.Locator("span.counter-line-name").First.InnerTextAsync()).Trim();

        string priceBlock = (await line.Locator("span.counter-line-price").First.InnerTextAsync()).Trim();
        string unitBlock = (await line.Locator("span.counter-line-unit").First.InnerTextAsync()).Trim();

        ILocator note = line.Locator("p.counter-line-note");

        string? noteText = await note.CountAsync() > 0
            ? (await note.First.InnerTextAsync()).Trim()
            : null;

        // §11.3 renders a delivered line's chip as .chip-ok and a pending one's as .chip-warn, which is
        // the state rather than the copy beside it.
        bool delivered = await line.Locator("span.chip-ok").CountAsync() > 0;

        return new CounterBillLine(
            int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity)
                ? quantity
                : 0,
            name,
            WithoutUnitPrice(priceBlock, unitBlock),
            WithoutEachSuffix(unitBlock),
            noteText,
            delivered);
    }

    /// <summary>The line total alone, with the nested unit-price text removed from the price block.</summary>
    private static string WithoutUnitPrice(string priceBlock, string unitBlock)
    {
        if (unitBlock.Length == 0)
        {
            return priceBlock;
        }

        int at = priceBlock.LastIndexOf(unitBlock, StringComparison.Ordinal);

        return at < 0 ? priceBlock : priceBlock[..at].Trim();
    }

    /// <summary>"$11.00 each" → "$11.00", so a scenario compares against a formatted amount.</summary>
    private static string WithoutEachSuffix(string unitBlock)
    {
        const string suffix = "each";

        return unitBlock.EndsWith(suffix, StringComparison.Ordinal)
            ? unitBlock[..^suffix.Length].Trim()
            : unitBlock;
    }

    /// <summary>
    /// The <c>li.counter-line</c> for the named item, or a failure naming what the bill holds instead.
    /// Matched by reading the names rather than by selector text, for the escaping reason in the type
    /// remarks.
    /// </summary>
    private static async Task<ILocator> LocateLineAsync(IPage page, string menuItemName, string verb)
    {
        ILocator lines = page.Locator($"{SittingSurfaceSelector} {BillLineSelector}");
        int count = await lines.CountAsync();

        for (int index = 0; index < count; index++)
        {
            ILocator line = lines.Nth(index);
            string name = (await line.Locator("span.counter-line-name").First.InnerTextAsync()).Trim();

            if (string.Equals(name, menuItemName, StringComparison.Ordinal))
            {
                return line;
            }
        }

        string bill = Describe(await ReadBillAsync(page));

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"There is no line for '{menuItemName}' on this bill to {verb}. It holds: {bill}."));
    }

    /// <summary>The named line as the bill currently renders it, or <c>null</c> when it is not there.</summary>
    private static async Task<CounterBillLine?> FindLineAsync(IPage page, string menuItemName)
    {
        CounterBill bill = await ReadBillAsync(page);

        return bill.People
            .SelectMany(entry => entry.Lines)
            .FirstOrDefault(line => string.Equals(line.Name, menuItemName, StringComparison.Ordinal));
    }

    /// <summary>
    /// §11.3's refusal, as the list of reasons it names. The problem sentence comes first and §6.5.9's
    /// per-operation reasons follow it, because a refusal names every reason rather than only the first —
    /// and an empty list is the honest answer when the till is not refusing anything.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadRefusalReasonsAsync(IPage page)
    {
        List<string> reasons = [];

        ILocator problem = page.Locator($"{SittingSurfaceSelector} p.status-error");

        if (await problem.CountAsync() > 0)
        {
            reasons.Add((await problem.First.InnerTextAsync()).Trim());
        }

        ILocator perOperation = page.Locator($"{SittingSurfaceSelector} ul.counter-rejection li");

        if (await perOperation.CountAsync() > 0)
        {
            IReadOnlyList<string> all = await perOperation.AllInnerTextsAsync();
            reasons.AddRange(all.Select(text => text.Trim()));
        }

        return reasons;
    }

    private static async Task<string> DescribeRefusalAsync(IPage page)
    {
        IReadOnlyList<string> reasons = await ReadRefusalReasonsAsync(page);

        if (reasons.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The till reports no refusal; the browser is at '{page.Url}'.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The till says: {string.Join(" | ", reasons)}");
    }

    private static async Task<string> DescribeBoardAsync(IPage page)
    {
        ILocator cards = page.Locator(OpenSittingSelector);
        int count = await cards.CountAsync();

        if (count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"no open table at all; the browser is at '{page.Url}'");
        }

        List<string> described = new(count);

        for (int index = 0; index < count; index++)
        {
            ILocator card = cards.Nth(index);
            string label = (await card.Locator("h2").First.InnerTextAsync()).Trim();
            string amount = (await card.Locator("div.counter-sitting-amount").First.InnerTextAsync()).Trim();

            described.Add(string.Create(CultureInfo.InvariantCulture, $"'{label}' at {amount}"));
        }

        return string.Join("; ", described);
    }

    private static async Task<string> DescribeSurfaceAsync(IPage page)
    {
        ILocator surface = page.Locator(SittingSurfaceSelector);

        if (await surface.CountAsync() == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"there is no bill on the page at all; the browser is at '{page.Url}'");
        }

        string? live = await surface.First.GetAttributeAsync("data-live");

        return string.Create(CultureInfo.InvariantCulture, $"data-live='{live ?? "absent"}'");
    }

    /// <summary>
    /// The sitting identifier out of a <c>/counter/sittings/{id}</c> URL. Parsed rather than trusted:
    /// under enhanced navigation the address bar can be ahead of the document, so a scenario that means
    /// to cross-check which sitting it opened deserves to be told when the URL is not one at all.
    /// </summary>
    private static Guid SittingIdentifierFrom(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && parsed.AbsolutePath.StartsWith(SittingPathPrefix, StringComparison.Ordinal)
            && Guid.TryParse(parsed.AbsolutePath[SittingPathPrefix.Length..], out Guid sittingIdentifier))
        {
            return sittingIdentifier;
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Following a table's bill link landed on '{url}', which is not a"
                + $" {SittingPathPrefix}{{id}} URL."));
    }

    private static string Money(decimal amount)
        => MoneyText.Format(amount, RestaurantInstance.CurrencyCode);
}
