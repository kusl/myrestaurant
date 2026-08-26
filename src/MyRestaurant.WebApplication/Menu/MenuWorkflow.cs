using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.Menu;

public interface IMenuWorkflow
{
    Task<CreateMenuSectionResult> CreateMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
        Guid menuSectionIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
        Guid menuSectionIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ResequenceMenuSectionsOutcome> ResequenceMenuSectionsAsync(
        IReadOnlyList<Guid> orderedMenuSectionIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
        Guid menuSectionIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<SetMenuItemAvailabilityResult> SetMenuItemActiveAsync(
        Guid menuItemIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<AttachMenuItemImageResult> AttachMenuItemImageAsync(
        Guid menuItemImageIdentifier,
        Guid menuItemIdentifier,
        string contentType,
        byte[] bytes,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<RemoveMenuItemImageOutcome> RemoveMenuItemImageAsync(
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);

    Task<SetMenuItemImageAltTextOutcome> SetMenuItemImageAltTextAsync(
        Guid menuItemIdentifier,
        string altText,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class MenuWorkflow : IMenuWorkflow
{
    private readonly IMenuAvailability _availability;
    private readonly IMenuAdministration _administration;
    private readonly IMenuSectionAdministration _sections;
    private readonly IMenuItemImageAdministration _images;
    private readonly IDomainEventBroadcaster _broadcaster;

    public MenuWorkflow(
        IMenuAvailability availability,
        IMenuAdministration administration,
        IMenuSectionAdministration sections,
        IMenuItemImageAdministration images,
        IDomainEventBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(broadcaster);

        _availability = availability;
        _administration = administration;
        _sections = sections;
        _images = images;
        _broadcaster = broadcaster;
    }

    public async Task<CreateMenuSectionResult> CreateMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        CreateMenuSectionResult result = await _sections
            .CreateMenuSectionAsync(
                menuSectionIdentifier, name, description, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.Created)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<RenameMenuSectionOutcome> RenameMenuSectionAsync(
        Guid menuSectionIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        RenameMenuSectionOutcome outcome = await _sections
            .RenameMenuSectionAsync(menuSectionIdentifier, name, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is RenameMenuSectionOutcome.Renamed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<DescribeMenuSectionOutcome> DescribeMenuSectionAsync(
        Guid menuSectionIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DescribeMenuSectionOutcome outcome = await _sections
            .DescribeMenuSectionAsync(
                menuSectionIdentifier, description, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is DescribeMenuSectionOutcome.Described)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<ReorderMenuSectionOutcome> ReorderMenuSectionAsync(
        Guid menuSectionIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ReorderMenuSectionOutcome outcome = await _sections
            .ReorderMenuSectionAsync(
                menuSectionIdentifier, displayOrder, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is ReorderMenuSectionOutcome.Reordered)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<ResequenceMenuSectionsOutcome> ResequenceMenuSectionsAsync(
        IReadOnlyList<Guid> orderedMenuSectionIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ResequenceMenuSectionsOutcome outcome = await _sections
            .ResequenceMenuSectionsAsync(
                orderedMenuSectionIdentifiers, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is ResequenceMenuSectionsOutcome.Resequenced)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<MenuSectionActivationOutcome> SetMenuSectionActiveAsync(
        Guid menuSectionIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        MenuSectionActivationOutcome outcome = await _sections
            .SetMenuSectionActiveAsync(
                menuSectionIdentifier, isActive, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is MenuSectionActivationOutcome.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<CreateMenuItemResult> CreateMenuItemAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        string name,
        string? description,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        CreateMenuItemResult result = await _administration
            .CreateMenuItemAsync(
                menuItemIdentifier,
                menuSectionIdentifier,
                name,
                description,
                priceAmount,
                actorPersonIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Created)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<RenameMenuItemResult> RenameMenuItemAsync(
        Guid menuItemIdentifier,
        string name,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        RenameMenuItemResult result = await _administration
            .RenameMenuItemAsync(menuItemIdentifier, name, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<RepriceMenuItemResult> RepriceMenuItemAsync(
        Guid menuItemIdentifier,
        decimal priceAmount,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        RepriceMenuItemResult result = await _administration
            .RepriceMenuItemAsync(menuItemIdentifier, priceAmount, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<DescribeMenuItemOutcome> DescribeMenuItemAsync(
        Guid menuItemIdentifier,
        string? description,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DescribeMenuItemOutcome outcome = await _administration
            .DescribeMenuItemAsync(menuItemIdentifier, description, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is DescribeMenuItemOutcome.Described)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<ReorderMenuItemOutcome> ReorderMenuItemAsync(
        Guid menuItemIdentifier,
        int displayOrder,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ReorderMenuItemOutcome outcome = await _administration
            .ReorderMenuItemAsync(menuItemIdentifier, displayOrder, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is ReorderMenuItemOutcome.Reordered)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<ResequenceMenuItemsOutcome> ResequenceMenuItemsAsync(
        Guid menuSectionIdentifier,
        IReadOnlyList<Guid> orderedMenuItemIdentifiers,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        ResequenceMenuItemsOutcome outcome = await _administration
            .ResequenceMenuItemsAsync(
                menuSectionIdentifier,
                orderedMenuItemIdentifiers,
                actorPersonIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome is ResequenceMenuItemsOutcome.Resequenced)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<MoveMenuItemToSectionOutcome> MoveMenuItemToSectionAsync(
        Guid menuItemIdentifier,
        Guid menuSectionIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        MoveMenuItemToSectionOutcome outcome = await _administration
            .MoveMenuItemToSectionAsync(
                menuItemIdentifier, menuSectionIdentifier, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is MoveMenuItemToSectionOutcome.Moved)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<SetMenuItemAvailabilityResult> SetMenuItemActiveAsync(
        Guid menuItemIdentifier,
        bool isActive,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        SetMenuItemAvailabilityResult result = await _availability
            .SetActiveAsync(menuItemIdentifier, isActive, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (result.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<AttachMenuItemImageResult> AttachMenuItemImageAsync(
        Guid menuItemImageIdentifier,
        Guid menuItemIdentifier,
        string contentType,
        byte[] bytes,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        AttachMenuItemImageResult result = await _images
            .AttachMenuItemImageAsync(
                menuItemImageIdentifier,
                menuItemIdentifier,
                contentType,
                bytes,
                actorPersonIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome is AttachMenuItemImageOutcome.Attached
            or AttachMenuItemImageOutcome.Replaced)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return result;
    }

    public async Task<RemoveMenuItemImageOutcome> RemoveMenuItemImageAsync(
        Guid menuItemIdentifier,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        RemoveMenuItemImageOutcome outcome = await _images
            .RemoveMenuItemImageAsync(menuItemIdentifier, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is RemoveMenuItemImageOutcome.Removed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }

    public async Task<SetMenuItemImageAltTextOutcome> SetMenuItemImageAltTextAsync(
        Guid menuItemIdentifier,
        string altText,
        Guid actorPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        SetMenuItemImageAltTextOutcome outcome = await _images
            .SetMenuItemImageAltTextAsync(
                menuItemIdentifier, altText, actorPersonIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is SetMenuItemImageAltTextOutcome.Changed)
        {
            _broadcaster.Publish(new MenuChanged());
        }

        return outcome;
    }
}
