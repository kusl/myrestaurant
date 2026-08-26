namespace MyRestaurant.DataAccess.Identity;

public interface ISecurityEventLog
{
    Task RecordAsync(
        Guid subjectPersonIdentifier,
        Guid? actorPersonIdentifier,
        string eventType,
        CancellationToken cancellationToken = default);
}
