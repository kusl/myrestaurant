# ADR-0006 — Redis is excluded from v1

**Status:** Accepted (2026-07-17)
**Finding trail:** F-13
**Requirements:** `REQUIREMENTS.md` §2 (caching row)

## Context

The stack table lists Redis as an optional infrastructure component. The v1 architecture is a single `web` instance per restaurant; all live updates flow through an in-process broadcaster; PostgreSQL is the only durable store; no computed result in v1 is expensive enough to cache. Redis would run as a container that nothing reads or writes.

## Decision

Redis is **not** part of the v1 compose stack. No package reference, no configuration key, no health check.

Trigger conditions for reintroduction (edit this ADR when one fires):

1. The system ever runs more than one `web` instance — Redis becomes the SignalR backplane and the broadcaster's fan-out transport.
2. A measured hot read path (profiling evidence required, not intuition) would materially benefit from a cache that PostgreSQL cannot serve.

## Consequences

- One fewer container, volume, health check, and failure mode.
- The in-process broadcaster (technical specification §9) is written behind an interface (`IDomainEventBroadcaster`) so a Redis-backed implementation can be substituted without touching subscribers.

## History

- 2026-07-16 — drafted.
- 2026-07-17 — accepted unchanged.
