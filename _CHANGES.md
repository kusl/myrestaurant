# F-06a — passkeys work on Cloudflare quick tunnels (course correction)

Every file below is a **full file** at its **repo-relative path**. Drop this folder's
contents over your working tree (same paths) and rebuild. `git status` will show exactly
these 17 files as modified/added.

## Files to DELETE

**None.** Every change is an in-place edit or a new file. Nothing is removed or renamed.
`ResolveWebAuthnRelyingPartyId()` is kept (it now supplies the QR-URL / fallback host,
not a pinned RP ID), and its existing test is kept. `wwwroot/js/passkey.js` and ADR-0010
needed no change, so they are not in this bundle.

## New files (2)

- `src/MyRestaurant.WebApplication/Identity/WebAuthnOriginPolicy.cs`
- `src/MyRestaurant.WebApplication/Identity/PublicOriginMiddleware.cs`

## Edited — code (3)

- `src/MyRestaurant.WebApplication/Identity/IdentityServiceCollectionExtensions.cs`
- `src/MyRestaurant.WebApplication/Configuration/RestaurantOptions.cs`
- `src/MyRestaurant.WebApplication/Program.cs`

## Edited / new — tests (3)

- `tests/MyRestaurant.WebApplication.Tests/Identity/WebAuthnOriginPolicyTests.cs`  (new)
- `tests/MyRestaurant.WebApplication.Tests/Identity/IdentityWiringTests.cs`        (edited)
- `tests/MyRestaurant.WebApplication.Tests/RestaurantOptionsTests.cs`             (edited)

## Edited — scripts & config (4)

- `scripts/quick_tunnel.sh`  (rewritten: one-command orchestrator; `chmod +x`)
- `run.sh`                   (comment/banner text only — the quick-tunnel note was wrong)
- `compose.yaml`             (passes `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` to `web`)
- `.env.example`             (documents `RESTAURANT_TRUSTED_ORIGIN_PATTERNS`)

## Edited — docs (5)

- `docs/adr/0005-origins-and-tls-cloudflare-named-tunnel.md`  (rewritten ruling + F-06a revision)
- `docs/TECHNICAL_SPECIFICATION.md`  (§3.3, §13 table, §14.3, accepted-risks, traceability)
- `docs/BUILD_PROGRESS.md`           (settled-note, both manual-test items, new Slice 7)
- `docs/OPERATIONS.md`               (§7 reasoning, §9 precision, §10 runbook)
- `README.md`                        (one-command demo + corrected passkey framing)

## The one-line why

`IdentityPasskeyOptions.ServerDomain` was pinned to the boot-time origin host, so the RP ID
could never match a quick tunnel's per-run `*.trycloudflare.com` hostname. It is now left
**null**, so the .NET 10 handler derives the RP ID from `Request.Host` per request
(normalized by `PublicOriginMiddleware`, gated by `ValidateOrigin`) — the same per-request
approach your GoTunnels project uses. Safe because credentials are RP-ID-scoped by the
authenticator. The named tunnel remains the production origin for *persistence*, not as a
passkey prerequisite.
