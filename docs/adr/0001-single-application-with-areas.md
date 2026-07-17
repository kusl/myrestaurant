# ADR-0001 — One Blazor Server application with routed areas

**Status:** Accepted (2026-07-17)
**Finding trail:** none (original architecture decision)
**Requirements:** `REQUIREMENTS.md` §3

## Context

The system presents five distinct experiences: table (guest), kitchen, counter, administration, and table display. The requirements deliberately leave open whether these are separate applications or routed sections of one application. All five experiences share one identity system, one database, and one live-update mechanism (the Blazor Server SignalR circuit). The deployment target is a single restaurant on a single host.

## Decision

One ASP.NET Core Blazor Server application (interactive server render mode, .NET 10) serves all five experiences as routed areas of a single app:

- `/table` — guest ordering
- `/kitchen` — kitchen queue and fulfillment
- `/counter` — billing, close and settle
- `/administration` — full administrative authority
- `/display` — table display devices (rotating join QR)

Authorization policies per area are defined in the technical specification §3.7.

## Consequences

- One image, one container, one deployment unit. The Podman Compose stack stays small.
- Live updates flow through one in-process broadcaster; no cross-application messaging exists or is needed.
- A fault in one area can affect all areas (shared process). At single-restaurant scale this is an accepted trade for operational simplicity.
- Separate applications would multiply deployment surface, cookie/authentication configuration, and observability wiring without adding isolation that matters at this scale.

## History

- 2026-07-16 — drafted (four areas).
- 2026-07-17 — accepted; fifth area `/display` added by the F-12 ruling (see ADR-0009).
