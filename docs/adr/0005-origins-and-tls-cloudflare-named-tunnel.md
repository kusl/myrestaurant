# ADR-0005 — Origins and TLS: named Cloudflare Tunnel is the production origin

**Status:** Accepted (2026-07-17) — F-06 ruling
**Finding trail:** F-06
**Requirements:** `REQUIREMENTS.md` §2 (tunneling rows), §5.1

## Context

WebAuthn passkeys, the Screen Wake Lock API (kitchen display and table displays), and dependable audio autoplay all require a secure context (HTTPS). Passkeys additionally bind to a **relying-party identifier derived from the origin's host**, so the origin must be stable wherever passkeys are used. The original requirements were silent on certificates and leaned on Cloudflare quick tunnels for demos.

Two facts settled the design:

1. **`trycloudflare.com` is on the Public Suffix List.** Each quick tunnel gets a random subdomain, and because the parent is a public suffix, that random subdomain **is** the registrable domain. A passkey registered through a quick tunnel binds to that one-run hostname and dies with it. There is no way to make quick-tunnel passkeys durable.
2. Installing a private certificate authority root on **guests' phones** is not a real option, so any "internal CA on the LAN" origin can only ever serve staff-owned devices, never the guest ordering flow.

The owner's GoTunnels project served as the working reference for tunnel lifecycle, TLS posture, and observability wiring. Its session model — a never-expiring bearer token in `localStorage` — was examined and **explicitly rejected**: it does not fit Blazor Server's cookie-plus-circuit authentication model and would undermine the security-stamp revalidation this system relies on.

## Decision

1. **Production origin = a persistent, named Cloudflare Tunnel on the operator's stable domain.** `RESTAURANT_PUBLIC_ORIGIN` is that `https://` origin; it drives QR join URLs and the WebAuthn relying-party identifier. Guests, staff devices, and table displays all use this origin — including devices physically inside the restaurant.
2. **Topology:** `cloudflared` (compose `production` profile) connects outbound to Cloudflare and forwards to `web` over the private compose network. TLS terminates at the Cloudflare edge; the tunnel leg is encrypted by `cloudflared`. Caddy is not in the production request path.
3. **Development origin:** Caddy terminates TLS for `https://localhost:8443` with its internal CA (compose default profile), giving developers a secure context with zero ceremony.
4. **Optional staff LAN fallback (documented, off by default):** Caddy may additionally serve a LAN hostname with its internal CA, roots installed on **staff-owned** devices only. During a WAN outage this keeps kitchen/counter/administration reachable by password + TOTP (passkeys registered on the public origin will not work on the fallback origin). Guest ordering and table displays are down during a WAN outage regardless.
5. **Quick tunnels (`try.cloudflare.com`) are demo-only.** The tunnel scripts print, prominently: passkeys registered through a quick tunnel are unusable on any later run (Public Suffix List behavior above); accounts must be reachable by password + TOTP. **A quick tunnel must never carry the bootstrap of a real instance.** Quick-tunnel scripts keep per-name state directories (`.tunnels/<name>/` — PID file, captured URL, log; git-ignored) so multiple concurrent tunnels never share environment or trample each other.

## Consequences

- **Accepted risk (owner-acknowledged): in-house ordering hairpins through Cloudflare.** A guest sitting in the restaurant reaches the server in the same building via the Cloudflare edge; LAN ordering therefore depends on WAN health. This is the price of durable passkeys on one stable origin without touching guest devices.
- Rootless Podman cannot bind ports 80/443 by default; the stack publishes 8080/8443, and the operations guide offers the `net.ipv4.ip_unprivileged_port_start=80` sysctl for installations that want standard ports for the LAN fallback. The tunnel path does not need inbound ports at all.
- Rotating join tokens (ADR-0009) embed `RESTAURANT_PUBLIC_ORIGIN`; changing the domain invalidates printed materials (there are none — displays re-render) but does invalidate registered passkeys. Domain changes are a documented, disruptive operation (operations guide §9).

## History

- 2026-07-16 — drafted as "Caddy internal CA on LAN + named tunnel for remote".
- 2026-07-17 — reworked to tunnel-as-primary-origin after the F-06 ruling and Public Suffix List verification; GoTunnels adopted as reference, its bearer-token session model rejected.
