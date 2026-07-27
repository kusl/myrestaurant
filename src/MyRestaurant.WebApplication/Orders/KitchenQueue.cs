using MyRestaurant.DataAccess.Orders;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>One pending line on a kitchen ticket (TECHNICAL_SPECIFICATION §11.2).</summary>
public sealed record KitchenQueueLine(
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    string? CustomizationNote,
    DateTimeOffset AddedAt)
{
    /// <summary>True when the guest asked for something (§7 free text) — the thing §11.2 wants prominent.</summary>
    public bool HasNote => !string.IsNullOrWhiteSpace(CustomizationNote);
}

/// <summary>
/// One person's outstanding lines at one table — a ticket (TECHNICAL_SPECIFICATION §11.2). The unit the
/// kitchen actually works in: everything one diner is still waiting for, in the order they asked for it.
/// </summary>
public sealed record KitchenQueueTicket(
    Guid GuestOrderIdentifier,
    Guid SittingIdentifier,
    Guid PersonIdentifier,
    string PersonName,
    Guid TableIdentifier,
    string TableLabel,
    IReadOnlyList<KitchenQueueLine> Lines,
    IReadOnlyList<DateTimeOffset> SendTimes)
{
    /// <summary>The ticket's age: the oldest line still outstanding on it. This is what orders the board.</summary>
    public DateTimeOffset OldestAddedAt => Lines[0].AddedAt;

    /// <summary>How many distinct lines are outstanding (a line of three soups is one).</summary>
    public int LineCount => Lines.Count;

    /// <summary>How many individual items are outstanding (a line of three soups is three).</summary>
    public int ItemCount => Lines.Sum(line => line.Quantity);

    /// <summary>True when any line on the ticket carries a note — used to flag the ticket at a glance.</summary>
    public bool HasNotes => Lines.Any(line => line.HasNote);
}

/// <summary>
/// Every outstanding ticket at one table (TECHNICAL_SPECIFICATION §11.2's outer grouping). Tables are
/// the kitchen's spatial unit — a runner carries a tray to a table, not to a person — so they group
/// first even though the work is per person.
/// </summary>
public sealed record KitchenQueueTable(
    Guid TableIdentifier,
    string TableLabel,
    IReadOnlyList<KitchenQueueTicket> Tickets)
{
    /// <summary>The table's age: the oldest outstanding line anywhere on it.</summary>
    public DateTimeOffset OldestAddedAt => Tickets[0].OldestAddedAt;

    public int LineCount => Tickets.Sum(ticket => ticket.LineCount);

    public int ItemCount => Tickets.Sum(ticket => ticket.ItemCount);
}

/// <summary>
/// Folds the flat <c>kitchen_pending_line</c> read into the shape §11.2 describes: "grouped by (table
/// label → person display name → order), ordered by the group's oldest <c>added_at</c>; each group shows
/// the send timestamp(s)".
///
/// <para>Pure, and outside the component, for the reason <see cref="OrderStaging"/> and
/// <c>OrderNarrative</c> are (§16.1 — no bUnit in this repository): the ordering rule here is the whole
/// behaviour of the screen, and a rule that can only be checked by rendering a Razor component is a rule
/// nobody checks.</para>
///
/// <para><b>Oldest first, at both levels.</b> §11.2 says "ordered by the group's oldest
/// <c>added_at</c>", and the direction that sentence implies is the only one that makes sense on a pass:
/// the table that has been waiting longest is the next thing to cook. Note that this is the group's
/// oldest line, not the group's newest — a table that ordered at 7:00 and again at 7:40 stays where its
/// 7:00 line puts it, because that line is still the oldest thing in the building. Sorting by the most
/// recent send would quietly push the forgotten order further down the screen every time somebody at
/// that table asked for another drink, which is precisely the failure §10.2's reminder exists to catch.
/// </para>
///
/// <para><b>Ties break deterministically.</b> Lines added by one send share an <c>occurred_at</c> to the
/// microsecond, and two tables can be sent to in the same instant. Every comparison therefore falls
/// through to a label and then to an identifier, so a re-read of unchanged data produces a byte-identical
/// board — a queue whose rows shuffle under a cook's hand on every live update is worse than a slightly
/// wrong order.</para>
/// </summary>
public static class KitchenQueue
{
    /// <summary>
    /// Groups and orders the pending lines. The input may be in any order; the caller's SQL ordering is
    /// not relied on, because this is the file that owns the rule.
    /// </summary>
    public static IReadOnlyList<KitchenQueueTable> Build(IReadOnlyList<KitchenPendingLineView> pendingLines)
    {
        ArgumentNullException.ThrowIfNull(pendingLines);

        if (pendingLines.Count == 0)
        {
            return [];
        }

        List<KitchenQueueTable> tables = [];

        foreach (IGrouping<Guid, KitchenPendingLineView> tableGroup in pendingLines.GroupBy(line => line.TableIdentifier))
        {
            List<KitchenQueueTicket> tickets = [];

            foreach (IGrouping<Guid, KitchenPendingLineView> orderGroup in tableGroup.GroupBy(line => line.GuestOrderIdentifier))
            {
                KitchenPendingLineView first = orderGroup.First();

                KitchenQueueLine[] lines = orderGroup
                    .OrderBy(line => line.AddedAt)
                    .ThenBy(line => line.OrderLineIdentifier)
                    .Select(line => new KitchenQueueLine(
                        line.OrderLineIdentifier,
                        line.MenuItemIdentifier,
                        line.MenuItemName,
                        line.Quantity,
                        NormalizeNote(line.CustomizationNote),
                        line.AddedAt))
                    .ToArray();

                // §11.2: "each group shows the send timestamp(s)". Distinct, because every line of one
                // send shares an instant and a ticket built from three sends should read as three times,
                // not as seven.
                DateTimeOffset[] sendTimes = lines
                    .Select(line => line.AddedAt)
                    .Distinct()
                    .OrderBy(instant => instant)
                    .ToArray();

                tickets.Add(new KitchenQueueTicket(
                    orderGroup.Key,
                    first.SittingIdentifier,
                    first.PersonIdentifier,
                    first.PersonName,
                    first.TableIdentifier,
                    first.TableLabel,
                    lines,
                    sendTimes));
            }

            tickets.Sort(CompareTickets);

            KitchenPendingLineView anyLine = tableGroup.First();
            tables.Add(new KitchenQueueTable(tableGroup.Key, anyLine.TableLabel, tickets));
        }

        tables.Sort(CompareTables);
        return tables;
    }

    /// <summary>
    /// Total outstanding lines across the whole board — the number the header shows, and the one a cook
    /// glances at to decide whether the pass is under control.
    /// </summary>
    public static int TotalLineCount(IReadOnlyList<KitchenQueueTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables.Sum(table => table.LineCount);
    }

    /// <summary>
    /// Trimmed, or <c>null</c> when the note is blank. §7 forbids validating notes and the write path
    /// already collapses whitespace-only notes to <c>null</c>; this makes the read side agree, so a note
    /// that is a single space does not render an empty quotation mark on a ticket.
    /// </summary>
    private static string? NormalizeNote(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static int CompareTickets(KitchenQueueTicket left, KitchenQueueTicket right)
    {
        int byAge = left.OldestAddedAt.CompareTo(right.OldestAddedAt);
        if (byAge != 0)
        {
            return byAge;
        }

        int byPerson = string.Compare(left.PersonName, right.PersonName, StringComparison.OrdinalIgnoreCase);
        return byPerson != 0
            ? byPerson
            : left.GuestOrderIdentifier.CompareTo(right.GuestOrderIdentifier);
    }

    private static int CompareTables(KitchenQueueTable left, KitchenQueueTable right)
    {
        int byAge = left.OldestAddedAt.CompareTo(right.OldestAddedAt);
        if (byAge != 0)
        {
            return byAge;
        }

        int byLabel = string.Compare(left.TableLabel, right.TableLabel, StringComparison.OrdinalIgnoreCase);
        return byLabel != 0
            ? byLabel
            : left.TableIdentifier.CompareTo(right.TableIdentifier);
    }
}
