using MyRestaurant.DataAccess.Orders;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class KitchenQueueTests
{
    private static readonly DateTimeOffset Noon = new(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid TableOne = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TableTwo = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void NoPendingLines_YieldsAnEmptyBoard()
    {
        Assert.Empty(KitchenQueue.Build([]));
        Assert.Equal(0, KitchenQueue.TotalLineCount([]));
    }

    [Fact]
    public void LinesAreGroupedByTableThenByOrder()
    {
        Guid adasOrder = Guid.NewGuid();
        Guid gracesOrder = Guid.NewGuid();
        Guid linussOrder = Guid.NewGuid();

        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", adasOrder, "Ada", "Soup", minutesAgo: 10),
            Line(TableOne, "Table 1", gracesOrder, "Grace", "Salad", minutesAgo: 8),
            Line(TableTwo, "Table 2", linussOrder, "Linus", "Steak", minutesAgo: 5),
        ]);

        Assert.Equal(2, board.Count);

        KitchenQueueTable first = board[0];
        Assert.Equal("Table 1", first.TableLabel);
        Assert.Equal(2, first.Tickets.Count);
        Assert.Equal(["Ada", "Grace"], first.Tickets.Select(ticket => ticket.PersonName));

        KitchenQueueTable second = board[1];
        Assert.Equal("Table 2", second.TableLabel);
        Assert.Equal(linussOrder, Assert.Single(second.Tickets).GuestOrderIdentifier);
    }

    [Fact]
    public void TablesAreOrderedOldestFirst()
    {
        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableTwo, "Table 2", Guid.NewGuid(), "Linus", "Steak", minutesAgo: 3),
            Line(TableOne, "Table 1", Guid.NewGuid(), "Ada", "Soup", minutesAgo: 20),
        ]);

        Assert.Equal(["Table 1", "Table 2"], board.Select(table => table.TableLabel));
    }

    [Fact]
    public void ATableIsOrderedByItsOldestLine_NotItsNewest()
    {
        Guid neglected = Guid.NewGuid();

        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", neglected, "Ada", "Soup", minutesAgo: 20),
            Line(TableOne, "Table 1", neglected, "Ada", "Coffee", minutesAgo: 1),
            Line(TableTwo, "Table 2", Guid.NewGuid(), "Linus", "Steak", minutesAgo: 10),
        ]);

        Assert.Equal(["Table 1", "Table 2"], board.Select(table => table.TableLabel));
        Assert.Equal(Noon.AddMinutes(-20), board[0].OldestAddedAt);
    }

    [Fact]
    public void TicketsWithinATableAreOrderedOldestFirst()
    {
        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", Guid.NewGuid(), "Zoe", "Soup", minutesAgo: 2),
            Line(TableOne, "Table 1", Guid.NewGuid(), "Ada", "Salad", minutesAgo: 9),
        ]);

        Assert.Equal(["Ada", "Zoe"], Assert.Single(board).Tickets.Select(ticket => ticket.PersonName));
    }

    [Fact]
    public void LinesWithinATicketAreOrderedByWhenTheyWereAdded()
    {
        Guid order = Guid.NewGuid();

        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", order, "Ada", "Coffee", minutesAgo: 1),
            Line(TableOne, "Table 1", order, "Ada", "Soup", minutesAgo: 12),
            Line(TableOne, "Table 1", order, "Ada", "Salad", minutesAgo: 6),
        ]);

        KitchenQueueTicket ticket = Assert.Single(Assert.Single(board).Tickets);
        Assert.Equal(["Soup", "Salad", "Coffee"], ticket.Lines.Select(line => line.MenuItemName));
    }

    [Fact]
    public void SendTimesAreDistinctAndAscending()
    {
        Guid order = Guid.NewGuid();

        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", order, "Ada", "Soup", minutesAgo: 12),
            Line(TableOne, "Table 1", order, "Ada", "Salad", minutesAgo: 12),
            Line(TableOne, "Table 1", order, "Ada", "Coffee", minutesAgo: 4),
        ]);

        KitchenQueueTicket ticket = Assert.Single(Assert.Single(board).Tickets);

        Assert.Equal([Noon.AddMinutes(-12), Noon.AddMinutes(-4)], ticket.SendTimes);
    }

    [Fact]
    public void LineCountCountsLines_AndItemCountCountsItems()
    {
        Guid order = Guid.NewGuid();

        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", order, "Ada", "Soup", minutesAgo: 5, quantity: 3),
            Line(TableOne, "Table 1", order, "Ada", "Salad", minutesAgo: 5, quantity: 2),
        ]);

        KitchenQueueTable table = Assert.Single(board);

        Assert.Equal(2, table.LineCount);
        Assert.Equal(5, table.ItemCount);
        Assert.Equal(2, KitchenQueue.TotalLineCount(board));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNoteIsNoNote(string? note)
    {
        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", Guid.NewGuid(), "Ada", "Soup", minutesAgo: 5, note: note),
        ]);

        KitchenQueueLine line = Assert.Single(Assert.Single(Assert.Single(board).Tickets).Lines);

        Assert.Null(line.CustomizationNote);
        Assert.False(line.HasNote);
    }

    [Fact]
    public void ANoteIsTrimmedAndFlagsItsTicket()
    {
        IReadOnlyList<KitchenQueueTable> board = KitchenQueue.Build(
        [
            Line(TableOne, "Table 1", Guid.NewGuid(), "Ada", "Soup", minutesAgo: 5, note: "  no onions  "),
        ]);

        KitchenQueueTicket ticket = Assert.Single(Assert.Single(board).Tickets);

        Assert.Equal("no onions", Assert.Single(ticket.Lines).CustomizationNote);
        Assert.True(ticket.HasNotes);
    }

    [Fact]
    public void TiesOnAgeBreakDeterministically()
    {
        KitchenPendingLineView[] lines =
        [
            Line(TableTwo, "Table 2", Guid.NewGuid(), "Linus", "Steak", minutesAgo: 7),
            Line(TableOne, "Table 1", Guid.NewGuid(), "Ada", "Soup", minutesAgo: 7),
        ];

        string[] firstPass = KitchenQueue.Build(lines).Select(table => table.TableLabel).ToArray();
        string[] reversedInput = KitchenQueue.Build(lines.Reverse().ToArray())
            .Select(table => table.TableLabel).ToArray();

        Assert.Equal(["Table 1", "Table 2"], firstPass);
        Assert.Equal(firstPass, reversedInput);
    }

    [Fact]
    public void InputOrderDoesNotMatter()
    {
        Guid order = Guid.NewGuid();

        KitchenPendingLineView[] lines =
        [
            Line(TableOne, "Table 1", order, "Ada", "Coffee", minutesAgo: 1),
            Line(TableOne, "Table 1", order, "Ada", "Soup", minutesAgo: 9),
        ];

        IReadOnlyList<KitchenQueueTable> shuffled = KitchenQueue.Build(lines.Reverse().ToArray());

        Assert.Equal(
            ["Soup", "Coffee"],
            Assert.Single(Assert.Single(shuffled).Tickets).Lines.Select(line => line.MenuItemName));
    }

    private static KitchenPendingLineView Line(
        Guid tableIdentifier,
        string tableLabel,
        Guid guestOrderIdentifier,
        string personName,
        string menuItemName,
        int minutesAgo,
        int quantity = 1,
        string? note = null) => new(
            guestOrderIdentifier,
            OrderLineIdentifier: Guid.NewGuid(),
            MenuItemIdentifier: Guid.NewGuid(),
            menuItemName,
            quantity,
            note,
            AddedAt: Noon.AddMinutes(-minutesAgo),
            SittingIdentifier: Guid.NewGuid(),
            PersonIdentifier: Guid.NewGuid(),
            personName,
            tableIdentifier,
            tableLabel);
}
