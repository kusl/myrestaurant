# ADR-0004 — Podman Compose is canonical; Aspire is optional convenience

**Status:** Accepted (2026-07-17)
**Finding trail:** F-14, F-17
**Requirements:** `REQUIREMENTS.md` §2

## Context

The requirements name two orchestration mechanisms — rootless Podman Compose and .NET Aspire — without a hierarchy (F-14). CI is required to be thin, with all real logic in shell scripts that are reproducible locally. Two authoritative orchestrators would guarantee drift.

## Decision

**Podman Compose is the single canonical orchestrator** for development, CI, and production. The same `compose.yaml` (with profiles, see technical specification §1 and §14) is driven by the same shell scripts everywhere: `run.sh` locally and in production, `scripts/continuous_integration.sh` in CI. GitHub Actions contains nothing but checkout and one script invocation.

An Aspire AppHost project **may** exist as an optional developer convenience (dashboard, service discovery during inner-loop work), but:

- no script, test, CI job, or documented procedure may require it;
- it must not own configuration — it reads the same `.env` contract;
- if it drifts from `compose.yaml`, compose wins and the AppHost is fixed or deleted.

## Consequences

- One source of truth for topology; CI parity with local runs is structural, not aspirational.
- Contributors who never install Aspire lose nothing.
- The Aspire project, if created, is a maintenance liability accepted knowingly; deleting it is always a legal move.

## History

- 2026-07-16 — drafted.
- 2026-07-17 — accepted unchanged.
