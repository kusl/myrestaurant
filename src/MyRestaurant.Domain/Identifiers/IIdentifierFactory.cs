namespace MyRestaurant.Domain.Identifiers;

/// <summary>
/// Produces primary keys. All identifiers are application-generated UUIDv7 (ADR-0011,
/// TECHNICAL_SPECIFICATION §8.1) — never database defaults — so rows sort by creation time
/// and the application owns identity before the row is written.
/// </summary>
public interface IIdentifierFactory
{
    /// <summary>
    /// Mints the next identifier. <b>Successive calls return values that ascend under PostgreSQL's
    /// <c>uuid</c> ordering, including when they land in the same millisecond</b> — that is a contract of
    /// this interface and not merely a property of the algorithm behind it, because it is what nine reads
    /// and one projection depend on and it is not what a plain UUIDv7 gives you (F-95).
    ///
    /// <para><b>Why the guarantee belongs here rather than in each reader.</b> Every event table in §8 is
    /// append-only and every mutation stamps its rows with one <c>IClock.UtcNow</c> instant, so a
    /// transaction that writes two events writes them at the <em>same</em> <c>occurred_at</c> — a create
    /// that also files an item under a heading and sets its description writes three. The reads therefore
    /// order by <c>occurred_at</c> and break the tie on the identifier, which is only a tie-break if the
    /// identifiers ascend in the order they were minted. When they did not, §11.4's per-item history read
    /// its three rows in the minted order one time in six, and the guest's basket order did not survive
    /// being sent: <see cref="Orders.OrderProjection"/> orders lines by their added-at instant and then by
    /// <c>order_line_identifier</c>, and every line in one send shares that instant.</para>
    ///
    /// <para><b>The ordering is PostgreSQL's, which is unsigned byte-wise over the sixteen bytes in RFC
    /// 9562 layout — and it is not <see cref="Guid.CompareTo(Guid)"/>.</b> The BCL compares a
    /// <see cref="Guid"/> field by field, and the second field is a <em>signed</em> 16-bit integer holding
    /// the low sixteen bits of the millisecond, so it reads as negative for half of every 65.536-second
    /// window and the two orders disagree across those boundaries. Anything asserting this contract must
    /// compare <see cref="Guid.ToByteArray(bool)"/> with <c>bigEndian: true</c>; asserting it with
    /// <c>CompareTo</c> would be asserting a different relation from the one the database applies.</para>
    ///
    /// <para>Callers may still rely on the coarse creation instant being readable from the value, and on
    /// values from separate processes interleaving only at millisecond granularity. Neither changed.</para>
    /// </summary>
    Guid Create();
}
