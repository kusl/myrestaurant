# Security policy

This project handles passwords, WebAuthn credentials, TOTP secrets and capability tokens. It is
published as free software so that other people will run it. Both halves of that sentence are why
this file exists.

**There is no bounty.** No money, no swag, no points. Said first rather than in a footnote, because
finding out afterwards is worse than being told.

## Reporting

Use GitHub's private reporting form: the **Security** tab of
[`kusl/myrestaurant`](https://github.com/kusl/myrestaurant/security) → **Report a vulnerability**.
That opens a draft advisory visible only to you and the maintainer, and it is the only channel
offered on purpose — a private one, because a report about a capability token should not be a public
page while it is still true.

**Please do not open a public issue for a security problem, and please do not open a pull request
with the fix.** `CONTRIBUTING.md` explains why patches are not accepted here in general; a report is
the exception to that document, and the reason is in the next section. A patch is not: it would
publish the defect in a diff before there is a release to upgrade to.

If the form is unavailable to you for any reason, say so in a normal issue **without describing the
problem** — "I have a security report and cannot reach the advisory form" is enough, and it will get
you a private channel.

## Read §17 first — it will save you a week

`docs/TECHNICAL_SPECIFICATION.md` **§17** is this project's accepted-risks register. It is not a
disclaimer: it records decisions that were argued, ruled and written down, each with the reasoning
and the thing that bounds it. The ones most likely to look like findings:

- **A join token is replayable inside its window** (≤120 s, ten-second floor). Rotating tokens
  replaced printed static QR codes precisely to make the window finite; what a replay buys inside it
  is bounded by the membership and visibility rules, not by the token.
- **`/register` has no rate limit.** Known, ruled, and the reason it is not a two-line fix is
  recorded — a second `AddRateLimiter` policy would silently take over the pairing endpoint's
  single-valued rejection handler and answer a refused registration with the wrong message.
- **A guest sees their table-mates' display names and orders.** That is the product, not a leak.
- **The counter role may operate password-only.** Ruled; no passkey mandate below administrator.
- **In-house ordering hairpins through Cloudflare**, so LAN ordering depends on WAN health.
- **Quick-tunnel passkeys do not survive a run**, because `trycloudflare.com` is on the Public
  Suffix List. Demo only; the script says so loudly.
- **The running version is on every page and there is no switch to hide it.** Deliberate, and the
  reasoning is in `README.md` under *Known caveats*.

A report arguing one of these should be **re-ruled** is welcome and will be read as an argument. A
report presenting one of them as *news* will be answered with a link back here.

## In scope

The code in this repository, at `main` or at the newest tag:

- Authentication or authorization bypass: reaching a gated area, acting as another person, escalating
  a role, or getting past the §3.5 obligations pipeline without discharging it.
- Join-token forgery, cross-table token acceptance, or anything that reaches a table's join secret —
  that secret is the one thing that never leaves the server.
- Reading or writing another table's sitting, order or history.
- Mutating order history without the trace §6.5 promises: an event that vanishes, a total that
  disagrees with its fold, a removal with no actor or reason.
- Credential handling: a TOTP secret or recovery code recoverable from the database alone, an
  Argon2id parameter below the §3.2 floor being accepted, a pairing code that survives its use.
- Secrets in a place they should not be: a log line, a rendered page, an OpenTelemetry attribute, a
  backup archive, an error response.
- Anything in the recovery path that would silently produce an unrestorable backup set.

## Out of scope

- **A deployment you do not operate.** If you found something on somebody's restaurant instance, the
  operator is the person who can act on it. This repository ships the code; under the AGPL each
  operator runs their own copy, and nobody here can reach their box, their data or their guests.
- **Scanner output with no exploit path.** A missing header, a cookie flag, a TLS grade, a
  dependency version — bring the path or bring the impact.
- **Denial of service by resource exhaustion** against a single-restaurant instance behind a
  Cloudflare tunnel. §17's trust model is explicit that this is not defended against.
- **The §17 accepted risks, restated.** See above.
- **Social engineering**, physical access to the box, and anything requiring an already-compromised
  administrator account.
- **Findings in PostgreSQL, .NET, Podman, Caddy or Cloudflare.** Report those upstream; if this
  project uses one of them wrongly, that part *is* in scope.

## What happens next

One person maintains this, in their own time. That is the honest constraint, so here is what it
means rather than an SLA nobody would meet:

| Stage | Target |
| --- | --- |
| Acknowledgement | within 7 days |
| Assessment, and a decision on whether it is a defect or an accepted risk | within 30 days |
| Fix for something exploitable | as fast as it can be done properly, and you will be told the plan |
| Publication | a GitHub Security Advisory when the fix ships, with a CVE where one is warranted |

**Coordinated, not silent.** The advisory is published when there is a release to upgrade to, and
not held indefinitely: if a fix is going to take a long time, that gets said out loud and a date gets
agreed with you rather than decided at you.

**Credit** in the advisory, under whatever name you give, unless you would rather not be named.

**Supported versions.** The newest tag, and that is all. There are no maintenance branches and no
backports — one maintainer cannot honestly promise them, and a promise nobody can keep is worse than
its absence. A fix lands on `main` and is published as a new tag.

| Version | Supported |
| --- | --- |
| newest `v*` tag | yes |
| anything older | no — upgrade |
| your fork | you |

## If you run a fork

You are the security contact for your own instance and your own guests; that is the arrangement the
AGPL describes, and `docs/OPERATIONS.md` §15 covers what else comes with it. Two things worth doing:

- **Change this file.** It names a channel that reaches *this* maintainer, not you. A fork that
  leaves it unedited is pointing its reporters at a stranger.
- **Watch the tags.** There is no notification mechanism from here to your box. A published advisory
  is the only signal, and you have to be the one looking.

## Nothing here is legal advice

`LICENSE` (AGPL-3.0-only) is the text that governs, and §§15–16 of it are the warranty disclaimer.
This document describes how a report gets handled, not what anyone is entitled to.
