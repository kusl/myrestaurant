# ADR-0011 — Application-generated UUIDv7 identifiers everywhere

**Status:** Accepted (2026-07-17)
**Finding trail:** none (schema-wide convention decision)
**Requirements:** `REQUIREMENTS.md` §8 (naming: `..._identifier`, no abbreviations)

## Decision

Every primary key in the schema is `uuid` named `{table_name}_identifier`, generated **in the application** with .NET's `Guid.CreateVersion7()` at entity construction time — never by the database (`DEFAULT` clauses for identifiers are absent by design; columns are plain `uuid PRIMARY KEY`).

## Context and rationale

- **UUIDv7 is time-ordered.** Values sort by creation instant, so B-tree inserts are append-mostly (the random-UUID index-fragmentation problem disappears) and `ORDER BY {x}_identifier` approximates chronological order for free — pleasant for event tables.
- **Application-side generation** lets the domain construct complete aggregates (an `order_event` and its operation rows referencing it) before any round trip, keeps Dapper inserts single-statement, and makes unit tests deterministic (inject an id factory).
- `.NET 10` ships `Guid.CreateVersion7()` in the box; no dependency.
- PostgreSQL stores it as an ordinary `uuid`; no extension needed.

## Consequences

- Identifiers leak coarse creation time (millisecond precision). Accepted: order events already carry `occurred_at`, and none of these identifiers are secrets. Secrets in this system are explicitly separate values (`join_secret`, device cookie secrets, pairing codes) and are random, not UUIDv7.
- All fixtures/tests must go through the application's id factory; hand-written UUIDv4 literals in seed SQL are tolerated (uniqueness is what matters) but new code paths must use `CreateVersion7()`.

## History

- 2026-07-17 — created and accepted.
