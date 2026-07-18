namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// Appends rows to the <c>security_event</c> table (TECHNICAL_SPECIFICATION §8.2). Security events are
/// an audit trail, never mutated and never deleted: sign-in outcomes and lockouts today (§3.5), and
/// administrative actions — resets, grants/revocations, deactivations — as those land in later M2
/// slices (§3.7). It lives beside the Dapper Identity store because it is pure persistence; callers in
/// the web layer depend on this interface rather than on SQL.
/// </summary>
public interface ISecurityEventLog
{
    /// <summary>
    /// Records one security event.
    /// </summary>
    /// <param name="subjectPersonIdentifier">
    /// The account the event is about (the <c>security_event.subject_person_identifier</c>; NOT NULL).
    /// </param>
    /// <param name="actorPersonIdentifier">
    /// The person who caused it, or <c>null</c> when the subject acted on themselves or the system did
    /// (e.g. a sign-in attempt, a lockout). Maps to the nullable <c>actor_person_identifier</c>.
    /// </param>
    /// <param name="eventType">
    /// One of <see cref="MyRestaurant.Domain.Authentication.SecurityEventType"/> — validated before the
    /// write so an unknown value fails fast rather than as a raw CHECK violation.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RecordAsync(
        Guid subjectPersonIdentifier,
        Guid? actorPersonIdentifier,
        string eventType,
        CancellationToken cancellationToken = default);
}
