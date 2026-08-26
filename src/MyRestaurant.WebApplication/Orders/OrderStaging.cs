using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;

namespace MyRestaurant.WebApplication.Orders;

public sealed record StagedOrderLine(
    Guid StagingIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    string? CustomizationNote);

public sealed record StagedRemoval(Guid OrderLineIdentifier, string Description);

public sealed record StagingResult(bool Accepted, string? Reason)
{
    public static StagingResult Accept { get; } = new(true, null);

    public static StagingResult Refuse(string reason) => new(false, reason);
}

public sealed record StagedBatch(IReadOnlyList<OrderOperation> Operations, IReadOnlyList<string> Descriptions);

public sealed class OrderStaging
{
    public const int MinimumQuantity = OrderMutationValidator.MinimumQuantity;

    public const int MaximumQuantity = OrderMutationValidator.MaximumQuantity;

    private readonly List<StagedOrderLine> _lines = [];
    private readonly List<StagedRemoval> _removals = [];

    public IReadOnlyList<StagedOrderLine> Lines => _lines;

    public IReadOnlyList<StagedRemoval> Removals => _removals;

    public bool IsEmpty => _lines.Count == 0 && _removals.Count == 0;

    public int OperationCount => _lines.Count + _removals.Count;

    public StagingResult Stage(MenuItemSummary menuItem, int quantity, string? customizationNote)
    {
        ArgumentNullException.ThrowIfNull(menuItem);

        if (!menuItem.IsActive)
        {
            return StagingResult.Refuse($"{menuItem.Name} is currently unavailable.");
        }

        if (quantity is < MinimumQuantity or > MaximumQuantity)
        {
            return StagingResult.Refuse($"Choose a quantity between {MinimumQuantity} and {MaximumQuantity}.");
        }

        _lines.Add(new StagedOrderLine(
            Guid.NewGuid(),
            menuItem.MenuItemIdentifier,
            menuItem.Name,
            quantity,
            NormalizeNote(customizationNote)));

        return StagingResult.Accept;
    }

    public bool Unstage(Guid stagingIdentifier)
        => _lines.RemoveAll(line => line.StagingIdentifier == stagingIdentifier) > 0;

    public StagingResult SetQuantity(Guid stagingIdentifier, int quantity)
    {
        if (quantity is < MinimumQuantity or > MaximumQuantity)
        {
            return StagingResult.Refuse($"Choose a quantity between {MinimumQuantity} and {MaximumQuantity}.");
        }

        int index = _lines.FindIndex(line => line.StagingIdentifier == stagingIdentifier);
        if (index < 0)
        {
            return StagingResult.Refuse("That item is no longer in your basket.");
        }

        _lines[index] = _lines[index] with { Quantity = quantity };
        return StagingResult.Accept;
    }

    public bool IsMarkedForRemoval(Guid orderLineIdentifier)
        => _removals.Any(removal => removal.OrderLineIdentifier == orderLineIdentifier);

    public void SetMarkedForRemoval(Guid orderLineIdentifier, string description, bool marked)
    {
        if (!marked)
        {
            _removals.RemoveAll(removal => removal.OrderLineIdentifier == orderLineIdentifier);
            return;
        }

        if (!IsMarkedForRemoval(orderLineIdentifier))
        {
            _removals.Add(new StagedRemoval(orderLineIdentifier, description));
        }
    }

    public int PruneRemovals(IEnumerable<Guid> stillRemovableLineIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(stillRemovableLineIdentifiers);

        HashSet<Guid> keep = new(stillRemovableLineIdentifiers);
        return _removals.RemoveAll(removal => !keep.Contains(removal.OrderLineIdentifier));
    }

    public void Clear()
    {
        _lines.Clear();
        _removals.Clear();
    }

    public StagedBatch Build(IIdentifierFactory identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        List<OrderOperation> operations = new(OperationCount);
        List<string> descriptions = new(OperationCount);

        foreach (StagedOrderLine line in _lines)
        {
            operations.Add(new LineAddedOperation(
                identifiers.Create(),
                line.MenuItemIdentifier,
                line.Quantity,
                0m,
                line.CustomizationNote));

            descriptions.Add($"{line.Quantity} × {line.MenuItemName}");
        }

        foreach (StagedRemoval removal in _removals)
        {
            operations.Add(new LineRemovedOperation(removal.OrderLineIdentifier, null));
            descriptions.Add($"Remove {removal.Description}");
        }

        return new StagedBatch(operations, descriptions);
    }

    private static string? NormalizeNote(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}
