# ADR-0012 — DbUp migrations executed at application startup

**Status:** Accepted (2026-07-17)
**Finding trail:** F-17 (run.sh definition), F-13/F-14 context (compose canonical)
**Requirements:** `REQUIREMENTS.md` §2 (Dapper, no Entity Framework), §8

## Context

With Entity Framework banned there are no EF migrations. The schema (technical specification §8) still needs versioned, repeatable evolution across dev machines, CI, and the single production host, with zero manual DBA steps — `./run.sh` and `podman-compose up` must "just work" on an empty volume.

## Decision

Use **DbUp** (MIT) with plain `.sql` scripts:

- Scripts live in `src/MyRestaurant.DataAccess/Migrations/` named `NNNN_description.sql` (`0001_initial_schema.sql` carries the full DDL from technical specification §8), and are **embedded resources** of the DataAccess assembly — the deployed container is self-sufficient.
- The web application runs the upgrader **at startup, before binding HTTP**: connect (with bounded retry while PostgreSQL boots), `EnsureDatabase`, execute pending scripts in name order inside transactions, record each in DbUp's journal table (`schema_version`).
- **Fail fast:** any script failure logs the error and exits non-zero; the container restarts and retries rather than serving requests against a half-migrated schema. Health endpoints only go live after migration success.
- Scripts are **append-only and immutable** once committed — corrections are new scripts (same roll-forward philosophy as the order event log). No down-scripts; recovery is restore-from-backup (`OPERATIONS.md` §6).
- Single-writer safety: DbUp's journal insert plus PostgreSQL transactional DDL make a concurrent second instance effectively a no-op racer; the compose stack runs one web replica anyway (ADR-0006 records the single-process assumption).

## Consequences

- `podman-compose up` on a blank volume produces a fully migrated, ready database with no human steps; the same path upgrades production on image update.
- Schema history is readable SQL in git, reviewable in diffs, and mirrored in the `schema_version` journal at runtime.
- Startup gains a migration phase (milliseconds when current); observable via startup logs and the readiness probe.

## History

- 2026-07-17 — created and accepted.
