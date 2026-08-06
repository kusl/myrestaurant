# ADR-0013 — Response security headers are the application's, not the proxy's

**Status:** Accepted (2026-08-06)
**Finding trail:** F-49 (no security headers, and a Content-Security-Policy nobody wrote)
**Requirements:** `REQUIREMENTS.md` §8 (a published program must be safe to publish), §2
**Specification:** `TECHNICAL_SPECIFICATION.md` §11.11, §14, §16.4, §17
**Related:** ADR-0004 (compose canonical), ADR-0005 (origins and TLS)

## Context

`Content-Security-Policy`, `X-Content-Type-Options` and `Referrer-Policy` have to be emitted by
something. There are three candidates in this project's deployment story and they are not
interchangeable:

- **Caddy** terminates TLS in the dev profile (§14.1) and is *optional* in production.
- **Cloudflare** terminates TLS in production via a named tunnel (ADR-0005) and can add response
  headers from a Transform Rule — a dashboard, outside the repository, invisible to every gate.
- **The application**, which is present in all three cases and in the fourth nobody wrote down: a bare
  `dotnet run` on a workstation reproducing a bug, and the §16.3 harness, both of which serve plain
  HTTP with nothing in front of them at all.

The question was forced by finding that the application already emits a Content Security Policy and
that nothing in this tree had ever said so. `AddInteractiveServerRenderMode` installs an endpoint
convention that appends `frame-ancestors 'self'` to the header on component endpoints, because
WebSocket compression plus cross-origin framing is an attack the framework will not ship one half of.
It has been there since M1. It covers one directive, it covers only endpoints that render components,
and — the detail that decides this ADR — it **appends** with `StringValues.Concat` rather than
assigning, so anything else that writes the header is delivered *beside* it.

## Decision

**The application owns these headers, and owns all of them.**

- One middleware, `SecurityHeadersMiddleware`, writes all three on **every** response — pages, static
  files, health endpoints, the clock, the sign-out POST, 404s, the rate limiter's 429, and the
  obligations redirect alike. It runs immediately after `PublicOriginMiddleware` (so the policy can
  name the request's normalized host) and before anything that can produce a response (so a
  short-circuit cannot escape it). It writes **before** calling the rest of the pipeline, because a
  header written afterwards is a header written after the body was flushed.
- The framework's partial policy is switched off — `AddInteractiveServerRenderMode(o =>
  o.ContentSecurityFrameAncestorsPolicy = null)` — so exactly one policy is delivered. What replaces
  it is stronger in both directions: `frame-ancestors 'none'` rather than `'self'`, on every response
  rather than on component endpoints. WebSocket compression is untouched; that is a different option.
- The policy is **computed from the request**, not a constant. `connect-src` names `ws://{host}` and
  `wss://{host}` because CSP's `'self'` is an origin comparison, `wss://host` is not the same origin as
  `https://host`, and CSP3's carve-out for the secure pair does not clearly extend to `ws:` from an
  `http:` page — which is what a bare `dotnet run` and the §16.3 harness serve. The whole live-update
  layer (§9) is that WebSocket.
- **A proxy may add to this; nothing may be delegated to one.** `Strict-Transport-Security` is the
  single exception and is deliberately *not* emitted here — it is an operator decision with a long
  memory, it is meaningless on the plain-HTTP hop between a tunnel and this process, and the wrong
  `max-age` cannot be revoked from the application (OPERATIONS §14).

## Alternatives considered

**Put them in `Caddyfile`.** Rejected: Caddy is the dev profile's proxy and is optional in production,
so the headers would be present exactly where they matter least. It would also make the security
posture of a fork depend on a file the fork may not use.

**Put them in a Cloudflare Transform Rule.** Rejected for the reason F-42 and F-46 both produced: a
control that lives on a hosting provider's settings page is unverifiable from inside the repository,
cannot be tested, cannot be reviewed in a diff, and is absent from every fork. This project already
has a rule that a document must not assert platform state; a *control* that only exists as platform
state is the same mistake with worse consequences.

**Leave `frame-ancestors 'self'` to the framework and add the rest beside it.** Rejected: the header
would carry two policies, both enforced, and a reader of the response could not tell which came from
where. It also leaves the strongest available answer (`'none'`) unreachable and leaves every
non-component response — including every static file — with no framing policy at all.

**A `<meta http-equiv>` tag in `App.razor`.** Rejected: `frame-ancestors` is specified to have no
effect from a `meta` tag, which is precisely the directive with the sharpest consequence here; and a
meta policy protects nothing that is not an HTML document, which excludes every static asset.

**`default-src 'none'` instead of `'self'`.** Rejected, and this is the one place F-45's allow-list
ruling is deliberately not applied — see §11.11. That ruling concerned a set this project enumerates
and controls. A CSP fallback governs a set the *browser* defines and extends, so `'none'` is an
allow-list over somebody else's vocabulary, and the cost of guessing wrong is a screen in a working
restaurant that quietly stops showing something. `'self'` already denies every cross-origin origin,
and the directives that should be narrower are taken to `'none'` by name.

## Consequences

- The security posture of a deployment no longer depends on which of three proxies is in front of it,
  or on none being in front of it.
- The policy is source, so it is diffable, reviewable and testable. Three test classes assert it:
  the policy's own shape, the middleware's timing and reach, and — the one that will actually catch a
  regression — a contract test that scans the markup and fails if the tree stops fitting inside the
  policy (§16.4).
- Adding a CDN, a web font, an analytics script or an embedded map becomes a decision that fails a
  test rather than a change that silently works in dev and breaks under a policy nobody remembered.
- The application is slightly harder to embed on purpose. Nothing in v1 wants to be embedded.

## History

- 2026-08-06 — created and accepted (F-49, M6 Slice 24).
