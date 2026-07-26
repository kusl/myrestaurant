using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.Orders;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// One item a guest has put in the staging area but not yet sent (TECHNICAL_SPECIFICATION §11.1).
///
/// <para>It carries <b>no price</b> on purpose. §6.5.4 prices every added line server-side from the menu
/// read inside the send transaction, so a price captured at staging time would be a second, older
/// authority — and the one moment it disagreed with the charge would be the moment a guest noticed. The
/// surface renders the current menu price beside the staged line instead, which is correct by
/// construction and updates itself when <c>MenuChanged</c> arrives (§7, §9).</para>
///
/// <para><see cref="MenuItemName"/> <em>is</em> captured, and only for the rejection panel: those
/// sentences describe the batch that was actually sent, and after a rename the name at send time is the
/// truthful label for it.</para>
/// </summary>
/// <param name="StagingIdentifier">A client-side key for this row. Not the order line identifier — that is minted at send time.</param>
/// <param name="MenuItemIdentifier">The chosen item (§7).</param>
/// <param name="MenuItemName">The item's name when it was staged; a label, never an authority.</param>
/// <param name="Quantity">1–100 (§6.5.4).</param>
/// <param name="CustomizationNote">Free text, trimmed, blank collapsed to <c>null</c> (§7).</param>
public sealed record StagedOrderLine(
    Guid StagingIdentifier,
    Guid MenuItemIdentifier,
    string MenuItemName,
    int Quantity,
    string? CustomizationNote);

/// <summary>
/// One committed line the guest has ticked for removal in the next send (§11.1's
/// "mark-my-pending-line-for-removal"). The description is captured so the rejection panel can name the
/// line even if the surface has re-read the order since.
/// </summary>
public sealed record StagedRemoval(Guid OrderLineIdentifier, string Description);

/// <summary>Whether a staging request was taken, and if not, the one sentence to show the guest.</summary>
public sealed record StagingResult(bool Accepted, string? Reason)
{
    public static StagingResult Accept { get; } = new(true, null);

    public static StagingResult Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The operations of one send, plus a human description per operation in the same order — so a
/// rejection's <see cref="OrderMutationError.OperationIndex"/> can be turned into "2 × Soup: the menu
/// item is currently unavailable" instead of "operation 3 failed" (§6.5.9, §11.1).
/// </summary>
public sealed record StagedBatch(IReadOnlyList<OrderOperation> Operations, IReadOnlyList<string> Descriptions);

/// <summary>
/// The guest staging area (TECHNICAL_SPECIFICATION §11.1): what has been picked but not yet sent, and
/// which committed lines are ticked for removal in the same batch.
///
/// <para>It lives outside the Razor component for the reason <c>ProfileDetails</c>,
/// <c>ObligationsEnforcement</c>, and <c>PairingCode.Normalize</c> do — a Razor component is not
/// unit-testable in this repository (no bUnit, §16.1), so the parts with decisions in them move out.
/// The decisions here are small but load-bearing: what may be staged, how a batch becomes an
/// all-or-nothing list of operations, and which marks must be dropped when the world moves underneath
/// them.</para>
///
/// <para><b>This is circuit state, not stored state.</b> A staged basket lives in the component that
/// owns this object and dies with the circuit — a refresh empties it. That is deliberate: §6 gives an
/// order exactly one persistence mechanism, the append-only event log, and a half-composed basket is
/// not an order event. Persisting drafts would mean a second write path, a second projection, and a new
/// question ("whose draft is on this table?") that §11.1 never asks.</para>
///
/// <para>Nothing here validates: §6.5 is enforced inside the transaction, under the lock, against a
/// menu re-read there (§6.6). What this class refuses — an inactive item, a quantity outside 1–100 — it
/// refuses only so the guest is told at the moment they tap rather than after a round trip. The
/// transaction decides again regardless, and its answer is the one that counts.</para>
/// </summary>
public sealed class OrderStaging
{
    /// <summary>Mirrors §6.5.4's floor, taken from the validator so the two can never drift.</summary>
    public const int MinimumQuantity = OrderMutationValidator.MinimumQuantity;

    /// <summary>Mirrors §6.5.4's ceiling, taken from the validator so the two can never drift.</summary>
    public const int MaximumQuantity = OrderMutationValidator.MaximumQuantity;

    private readonly List<StagedOrderLine> _lines = [];
    private readonly List<StagedRemoval> _removals = [];

    /// <summary>Items picked but not yet sent, in the order they were picked.</summary>
    public IReadOnlyList<StagedOrderLine> Lines => _lines;

    /// <summary>Committed lines ticked for removal in the next send, in the order they were ticked.</summary>
    public IReadOnlyList<StagedRemoval> Removals => _removals;

    /// <summary>True when there is nothing to send — §11.1's "a Send button that is disabled while empty".</summary>
    public bool IsEmpty => _lines.Count == 0 && _removals.Count == 0;

    /// <summary>How many operations the next send would carry.</summary>
    public int OperationCount => _lines.Count + _removals.Count;

    /// <summary>
    /// Puts an item in the staging area. Refuses an inactive item (§7 — visible, unorderable) and a
    /// quantity outside 1–100 (§6.5.4). The note is trimmed and a blank one collapses to <c>null</c>,
    /// which is exactly what the transaction would have done to it, so the staged row and the stored row
    /// read the same. Length is <em>not</em> checked: §7 says a customization note is free text and is
    /// "never validated against any rules engine", and an unwelcome one is handled by a human walking to
    /// the table.
    /// </summary>
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

    /// <summary>Takes a staged item back out. Returns false when the key is unknown (a double tap).</summary>
    public bool Unstage(Guid stagingIdentifier)
        => _lines.RemoveAll(line => line.StagingIdentifier == stagingIdentifier) > 0;

    /// <summary>
    /// Changes a staged item's quantity in place, keeping its position in the basket. Refuses the same
    /// range <see cref="Stage"/> does.
    /// </summary>
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

    /// <summary>True when this committed line is ticked for removal in the next send.</summary>
    public bool IsMarkedForRemoval(Guid orderLineIdentifier)
        => _removals.Any(removal => removal.OrderLineIdentifier == orderLineIdentifier);

    /// <summary>Ticks (or unticks) a committed line for removal. Ticking twice is a no-op, not a duplicate.</summary>
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

    /// <summary>
    /// Drops marks for lines that are no longer the guest's to remove — the kitchen fulfilled one while
    /// the tick sat there, or staff removed it outright. Without this, one stale tick would sink an
    /// otherwise-good batch every time, because §6.5.9 rejects the whole event on any failed operation.
    /// Called by the surface after every re-read; returns how many marks were dropped so it can say so.
    /// </summary>
    public int PruneRemovals(IEnumerable<Guid> stillRemovableLineIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(stillRemovableLineIdentifiers);

        HashSet<Guid> keep = new(stillRemovableLineIdentifiers);
        return _removals.RemoveAll(removal => !keep.Contains(removal.OrderLineIdentifier));
    }

    /// <summary>Empties the staging area — what a committed send leaves behind.</summary>
    public void Clear()
    {
        _lines.Clear();
        _removals.Clear();
    }

    /// <summary>
    /// Turns the staging area into the operations of one <c>guest_submission</c> (§6.3: "one
    /// guest_submission event owning N added + M removed rows"), adds first then removals, with a
    /// parallel description per operation for the rejection panel.
    ///
    /// <para>Order line identifiers are minted <em>here</em>, at send time, rather than when the item was
    /// staged: the identifier is the line's identity in the log (§6.4), and a basket that has not been
    /// sent has no lines yet. A rejected send writes nothing, so a retry mints fresh ones and nothing is
    /// left dangling.</para>
    ///
    /// <para>Every added line is proposed at a price of zero. §6.5.4 says client-sent prices are ignored
    /// and the transaction overwrites this from the menu it reads under the lock; sending zero rather
    /// than a plausible-looking number means that if the overwrite ever stopped happening, the bug would
    /// show up as a free lunch on the first order rather than as a stale price nobody noticed.</para>
    /// </summary>
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
            // §6.3 allows a reason on a removal and §11.1 does not ask the guest for one — a guest
            // un-ordering their own pending line is self-explanatory, and a required box would be a
            // question with no reader. Staff removals (§11.3) carry one.
            operations.Add(new LineRemovedOperation(removal.OrderLineIdentifier, null));
            descriptions.Add($"Remove {removal.Description}");
        }

        return new StagedBatch(operations, descriptions);
    }

    private static string? NormalizeNote(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}
