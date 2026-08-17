# ADR-0011 — Application-generated UUIDv7 identifiers everywhere

**Status:** Accepted (2026-07-17). Amended 2026-08-17 (M6 Slice 45): the factory is required to be
monotonic, and the reason is F-95.
**Finding trail:** F-95 (the ordering the schema leaned on was never guaranteed)
**Requirements:** `REQUIREMENTS.md` §8 (naming: `..._identifier`, no abbreviations)

## Decision

Every primary key in the schema is `uuid` named `{table_name}_identifier`, generated **in the application**
at entity construction time — never by the database (`DEFAULT` clauses for identifiers are absent by design;
columns are plain `uuid PRIMARY KEY`).

Generation goes through `IIdentifierFactory`, and that interface carries a **contract beyond the format**:
successive calls return values that **ascend under PostgreSQL's `uuid` ordering, including when they land
in the same millisecond**. `UuidV7IdentifierFactory` keeps it with a 12-bit counter in the `rand_a` field —
RFC 9562 §6.2's first named method for exactly this — advanced by compare-and-swap over one process-wide
`long` holding the millisecond count above the counter.

## Context and rationale

- **UUIDv7 is time-ordered, but only between milliseconds.** Values sort by creation instant, so B-tree
  inserts are append-mostly (the random-UUID index-fragmentation problem disappears) and
  `ORDER BY {x}_identifier` approximates chronological order for free — pleasant for event tables.
- **Which is exactly half of what this schema needs, and the missing half was assumed for eight months.**
  `Guid.CreateVersion7()` is `Guid.NewGuid()` with the 48-bit timestamp written over the top and the version
  and variant nibbles set; the remaining 74 bits stay cryptographically random. Every mutation in §8 stamps
  all the rows of its transaction with one `IClock.UtcNow`, so nine reads and `OrderProjection` order by an
  instant and then break the tie on an identifier — and inside one millisecond that tie-break was a coin
  flip, measured at 49.8% inverted. A menu item created under a heading with a description writes three
  events at one instant and §11.4 read them in the minted order one time in six; a guest's basket order did
  not survive being sent. F-95 has the full account.
- **The guarantee belongs to the factory rather than to each reader**, because there is nothing nine
  `ORDER BY` clauses could do about it individually. Making the identifier stream monotonic makes every one
  of them correct at once, and it is the F-47 habit: where a rule can be executed, a list should not exist.
- **Application-side generation** lets the domain construct complete aggregates (an `order_event` and its
  operation rows referencing it) before any round trip, keeps Dapper inserts single-statement, and makes
  unit tests deterministic (inject an id factory).
- `.NET 10` ships `Guid.CreateVersion7()` in the box; it is still what supplies the random `rand_b` bits and
  the version and variant nibbles, so no dependency was added and no randomness source changed.
- PostgreSQL stores it as an ordinary `uuid`; no extension needed.

## Consequences

- Identifiers leak coarse creation time (millisecond precision). Accepted: order events already carry
  `occurred_at`, and none of these identifiers are secrets. Secrets in this system are explicitly separate
  values (`join_secret`, device cookie secrets, pairing codes) and are random, not UUIDv7.
- **They now also leak how many identifiers the process has minted in the current millisecond**, which is
  the counter, and 62 random bits remain. Accepted on the same terms and for the same reason: an identifier
  in this system is not a capability, and nothing is authorized by holding one.
- **The embedded instant can run briefly ahead of the wall clock.** More than 4096 identifiers inside one
  millisecond exhausts the counter, and the increment carries into the timestamp rather than wrapping —
  because a wrap would hand out a value sorting *before* its predecessor, which is the whole defect back
  under a smaller name. Nothing reads the embedded instant: every row that records a time records it in a
  `timestamptz` column from `IClock`, and this ADR has always said these identifiers are not the time of
  record. A clock that steps **backwards** is the same case from the other side and gets the same answer, so
  the stream never doubles back.
- **The factory's state is static, and that is a decision rather than a shortcut.** The web application
  registers it as a singleton, so an instance field would suffice there — but the guarantee is about the
  *process's* stream of identifiers, and two instances that each ascended independently would satisfy an
  instance field while handing two callers values that interleave in the wrong order. Static means the
  contract cannot be broken by a registration lifetime, by a test constructing its own factory, or by a
  second factory somebody adds later.
- **Anything asserting the contract must compare `ToByteArray(bigEndian: true)`, not `Guid.CompareTo`.** The
  BCL compares a `Guid` field by field and the second field is a *signed* 16-bit integer holding the low
  sixteen bits of the millisecond, so it reads as negative for half of every 65.536-second window and the
  BCL's order and PostgreSQL's genuinely disagree across those boundaries. A test written with `CompareTo`
  would pin a relation no read uses.
- All fixtures/tests must go through the application's id factory; hand-written UUIDv4 literals in seed SQL
  are tolerated (uniqueness is what matters) but new code paths must use the factory. **`Guid.NewGuid()`
  remains correct for the values that are not stored-row identifiers** — security stamps, broadcast
  subscription tokens, and `OrderStaging`'s client-side staging keys — because none of those is ever
  ordered. `OrderStaging`'s *line* identifiers are minted through the factory at send time and always were,
  which is why F-95 reached the guest's basket at all.

## History

- 2026-07-17 — created and accepted.
- 2026-08-17 — amended (M6 Slice 45, F-95): monotonicity within a millisecond becomes a stated contract of
  `IIdentifierFactory` rather than an assumed property of UUIDv7, and `UuidV7IdentifierFactory` gains the
  RFC 9562 §6.2 counter that keeps it. No schema change; the column type, the naming rule and the
  application-generation rule are untouched.
