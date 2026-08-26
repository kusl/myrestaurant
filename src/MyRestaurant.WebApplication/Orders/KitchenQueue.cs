using MyRestaurant.DataAccess.Orders;

namespace MyRestaurant.WebApplication.Orders;

public sealed record KitchenQueueLine(
    Guid OrderLineIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    string? CustomizationNote,
    DateTimeOffset AddedAt)
{
    public bool HasNote => !string.IsNullOrWhiteSpace(CustomizationNote);
}

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
    public DateTimeOffset OldestAddedAt => Lines[0].AddedAt;

    public int LineCount => Lines.Count;

    public int ItemCount => Lines.Sum(line => line.Quantity);

    public bool HasNotes => Lines.Any(line => line.HasNote);
}

public sealed record KitchenQueueTable(
    Guid TableIdentifier,
    string TableLabel,
    IReadOnlyList<KitchenQueueTicket> Tickets)
{
    public DateTimeOffset OldestAddedAt => Tickets[0].OldestAddedAt;

    public int LineCount => Tickets.Sum(ticket => ticket.LineCount);

    public int ItemCount => Tickets.Sum(ticket => ticket.ItemCount);
}

public static class KitchenQueue
{
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

    public static int TotalLineCount(IReadOnlyList<KitchenQueueTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables.Sum(table => table.LineCount);
    }

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
