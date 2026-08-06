# M6 Slice 24 — the policy nobody wrote

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, no scripts that edit your
code or your documents. **`docs/BUILD_PROGRESS.md` is included whole**, with Slice 24 appended, so
there is nothing to merge afterwards and nothing to remember.

```bash
tar -xzf m6-slice-24-response-security-headers.tar.gz -C /home/kushal/src/dotnet/myrestaurant
cd /home/kushal/src/dotnet/myrestaurant
git add src/MyRestaurant.WebApplication/Security \
        tests/MyRestaurant.WebApplication.Tests/Security \
        docs/adr/0013-security-headers-are-the-applications.md
```

Then:

```bash
bash scripts/check_tree.sh
bash scripts/check_repository.sh
```

## Files to DELETE

**None.** Nothing is renamed, superseded or orphaned.

No migration, no schema change, no package change, no `.slnx` edit, no `.csproj` edit, no Razor edit,
no shell script, no workflow, no `Containerfile`, no `.dockerignore`, no `Caddyfile`, no
`compose.yaml`.

**There is a `Program.cs` edit** — two changes, described below. That is rare enough in this project to
be worth naming at the top rather than leaving in a table.

**The `git add` is not optional.** Six new files land in two directories that do not exist yet, and
`check_tree.sh`, `check_repository.sh`, `ci_local.sh` and CI's `shell-scripts` job all enumerate with
`git ls-files` — an untracked new file is silently unchecked by every one of them. `dotnet test` will
still compile and run the tests (the test SDK globs `**/*.cs`), so the symptom of forgetting is a test
count that moves while the gates report an unchanged file count.

## What happened

I went looking at a layer nothing in this tree had ever described: what the application says about how
a browser may use the pages it serves. The search returned nothing — not one occurrence of
`Content-Security-Policy`, `frame-ancestors`, `nosniff` or `Referrer-Policy` in any source file, any
document, `Caddyfile`, `compose.yaml`, `run.sh` or either workflow.

That is not the finding.

## F-49, first half — there was a policy, and nobody in this project wrote it

`app.MapRazorComponents<App>().AddInteractiveServerRenderMode()` — the parameterless call, unchanged
since M1 — installs an endpoint convention that appends `frame-ancestors 'self'` to
`Content-Security-Policy`. Read out of `dotnet/aspnetcore` at `release/10.0`, its enabling condition
holds on both of this tree's defaults, so **every page this application has ever served carried a
Content Security Policy.** The framework adds it because WebSocket compression plus cross-origin
framing is an attack and it will not enable the first without mitigating the second.

So the gap is not *there is no policy*. It is **there is a policy this project cannot reason about**,
and that is a shape this ledger had no row for. F-35, F-37 and F-39 were capabilities a requirement
assumed and no milestone claimed. F-45 was a file that should have existed and never had. This is a
control that existed, worked, and was never decided on — one directive, at `'self'` rather than
`'none'`, on component endpoints only. Not on `app.css`. Not on `js/*.js`. Not on
`/_framework/blazor.web.js`, `/healthz/*`, the §11.7 clock, `POST /account/sign-out`, a 404, a 429 or
the obligations redirect.

And it **appends** — `StringValues.Concat`, not assignment — so a policy written beside it would have
been *delivered* beside it, two values on one response, both enforced as an intersection and neither
attributable. That is why the correct move is `ContentSecurityFrameAncestorsPolicy = null` rather than
"add the rest". The option's own remarks ask for exactly what replaces it, and what replaces it is
stronger in both directions: `'none'` instead of `'self'`, on every response instead of on some.
WebSocket compression is untouched — that is a different option, left at its default.

Everything else was genuinely absent. Two of the absences have names in this product rather than in a
checklist: **no `script-src`**, on a tree with six `MarkupString` sites where inline SVG can carry a
`<script>`; and **no `Referrer-Policy`**, on an application whose §4.3 join token travels in a query
string.

## F-49, second half — the obvious policy would have killed every live surface

`connect-src 'self'` is the natural choice for a single-origin application. It would have passed every
unit test anybody would write about the header, and it would have refused the Blazor circuit's
WebSocket on every plain-HTTP origin — because `'self'` is an **origin** comparison and `ws://host` is
not the same origin as `http://host`. CSP3's carve-out extends `'self'` to the `https:` and `wss:`
variants and says nothing about `ws:`; browsers have disagreed since 2015; MDN still carries the note.

`http:` is what a bare `dotnet run` serves and what the §16.3 harness boots. Every §9 notification
arrives over that socket. So `connect-src` names both origins, derived from the request host that
`PublicOriginMiddleware` has already normalized to a trusted public host — which is what makes writing
a request value into a response header safe rather than clever.

**The thing that would have caught the mistake already exists, and it was built for something else.** A
policy that kills the circuit is a policy under which no surface reports `data-live='true'`, so Slice
23's four barriers would have failed all fifteen scenarios in a way that named the cause. Second time
in two slices that a gate's most useful property was a failure nobody built it for.

## The files

| File | Change |
| --- | --- |
| `src/…/Security/ResponseSecurityHeaders.cs` | **NEW.** The policy: pure, BCL-only, strings in and strings out. Every directive carries the reason it is what it is |
| `src/…/Security/SecurityHeadersMiddleware.cs` | **NEW.** Writes the three headers on the way *in*, so a short circuit cannot escape them |
| `src/MyRestaurant.WebApplication/Program.cs` | **two changes.** `app.UseMiddleware<SecurityHeadersMiddleware>();` immediately after `PublicOriginMiddleware`, and `AddInteractiveServerRenderMode(serverOptions => serverOptions.ContentSecurityFrameAncestorsPolicy = null)`. The pipeline-order comment at the top gains the new step |
| `tests/…/Security/ResponseSecurityHeadersTests.cs` | **NEW.** 29 assertions on what the header *says* |
| `tests/…/Security/SecurityHeadersMiddlewareTests.cs` | **NEW.** 8 assertions on *when and to what* |
| `tests/…/Security/ContentSecurityPolicyContractTests.cs` | **NEW.** 9 assertions that the tree still fits inside the policy — the one that will actually catch a regression |
| `docs/adr/0013-security-headers-are-the-applications.md` | **NEW.** Why the application owns these and not Caddy, not Cloudflare, not a `<meta>` tag |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.9**: **§11.11** new and normative, §16.4 gains the three test classes, §17 names the threats bounded, Appendix A gains **F-49** |
| `docs/REQUIREMENTS.md` | **rev 5**: one new §8 principle |
| `docs/OPERATIONS.md` | §13 gains two runbook rows; §14 gains the `Strict-Transport-Security` ruling and where it belongs |
| `docs/DOCUMENTATION_REVIEW.md` | Group E gains F-49; *Going forward* gains the habit it earned |
| `docs/BUILD_PROGRESS.md` | **Full file, 484 KiB.** Slice 24 appended. Nothing to run |
| `_CHANGES.md` | this file |

`REQUIREMENTS.md` **does** move this time, on the rev 3 and rev 4 reasoning rather than the v1.2 one:
nothing in this tree previously asked the program to constrain what a browser may do with a page it
serves, so this is new intent rather than a mechanism catching up with an existing contract.

No Razor file is touched. That is worth stating because the policy is *about* the Razor files: it was
written to fit the markup as it stands, which is why the contract test scans the markup rather than
being told about it.

## Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. The authored-text count rises by SIX (two
#    source files, three test files, one ADR). If it does not move, the `git add` did not happen.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. Gate 3 must still say "none" — the OPERATIONS §14 note
#    names where a switch lives without asserting its value, which is the form F-46 ruled for.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates, same number and same order as Slice 23.

dotnet build
#    worth running on its own first. Two new source files, and the render-mode call gains a lambda —
#    the only line in the tree whose overload resolution changed.

dotnet test
#    expect: 1051 total, 0 failed. Was 1005. Forty-six new: 29 + 8 + 9. No existing test is edited,
#    so any other movement is not this slice.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. THIS IS THE ONE THAT MATTERS. Every scenario now runs against a
#    real Chromium enforcing a real Content Security Policy over plain http, which is the exact
#    configuration the connect-src design exists for.
```

And once it is up, the two `curl`s that show what this slice did — the second is the interesting one,
because a static file is the response class an endpoint convention misses:

```bash
curl -sSI http://localhost:8080/        | grep -i -e content-security -e x-content-type -e referrer
curl -sSI http://localhost:8080/app.css | grep -i -e content-security -e x-content-type -e referrer
```

`podman build` is not in the list: nothing in the build context changed.

## Where to look if this breaks

**Every E2E scenario times out on a `data-live='true'` barrier.** The policy is refusing the circuit's
WebSocket. Open the browser console on the failing page and look for a `connect-src` violation; the
`blockedURI` names the socket URL the browser wanted. The two candidates are a host that
`IsExpressibleAsHostSource` refused (an address literal), or the harness serving on a host the policy
did not derive from. `ResponseSecurityHeaders.WebSocketSourcesFor` is the one method involved and it is
directly testable with the host string from the failure.

**One page renders unstyled, or a control does nothing.** A CSP violation, and the console names the
directive. Do not widen the policy at a proxy — a header added in front of this application arrives
*beside* the one it sent, and two policies are enforced as an intersection, so the addition will not
have the effect you expect. Fix it in `ResponseSecurityHeaders`, and expect
`ContentSecurityPolicyContractTests` to have gone red first if the cause is new markup.

**`ContentSecurityPolicyContractTests` fails on `NoMarkupCarriesAnInlineScript` or
`NoMarkupCarriesAnInlineEventHandlerAttribute`.** That is the gate working. Something added an inline
`<script>` or an HTML `on*=` attribute, which `script-src 'self'` refuses with no hash and no nonce.
Move it into `wwwroot/js/` beside `passkey.js`, `display.js`, `kitchen.js` and `clock.js`. Blazor's
`@onclick` is a directive attribute and is deliberately not matched — the scan keys on a whitespace
character before `on`, which `@` is not.

**`TheInlineStyleConcessionIsStillEarned` or `TheOnlyDataUrlIsTheFaviconThatImgSrcAdmits` fails.** The
opposite direction, and the message says so: a fact that justified a concession has gone, so the
concession should go with it. This is the only mechanism that would ever remove one.

**`TheCompositionRootDeliversThePolicyAndOnlyThePolicy` fails.** Either the middleware moved behind
something that can answer a request, or `ContentSecurityFrameAncestorsPolicy = null` was dropped from
the render-mode call. The second is the quiet one: everything keeps working and every response
silently carries two policies.

**Test count is not 1051.** Forty-six were added and none removed or edited. If the total moved by some
other amount, look at what changed between your last run and this one rather than at this slice.

**`Program.cs` will not compile.** The likeliest of the three source edits, and it is one line:
`AddInteractiveServerRenderMode` gains an `Action<ServerComponentsEndpointOptions>` overload's lambda.
The parameter type is inferred, so no `using` was added for it — if the compiler disagrees, add
`using Microsoft.AspNetCore.Components.Server;`.

## What was actually verified here

No .NET SDK and no browser in the sandbox, so nothing was reasoned about that could be executed
instead:

- **The framework's convention was read, not remembered.** `ServerComponentsEndpointOptions.cs` and
  `ServerRazorComponentsEndpointConventionBuilderExtensions.cs` fetched from `dotnet/aspnetcore` at
  `release/10.0`, and the enabling condition evaluated against this tree's actual call: both defaults
  hold, so it has always been active. `StringValues.Concat` confirmed from the same source.
- **The `ws:` question was settled against the specification.** CSP3's own "Changes from Level 2"
  extends `'self'` to the `https:` and `wss:` variants and says nothing about `ws:`. The whole
  `connect-src` design turns on that sentence.
- **All 46 new assertions were executed as faithful ports against the delivered tree**, and all pass.
  The scan reports 48 `.razor` files, 5 `<script src>`, 1 stylesheet link, 21 inline `<style>` blocks,
  7 resource references, exactly 1 `data:` URL, and zero inline scripts, zero `on*` handlers, zero
  off-origin references and zero `url(`/`@import` in the stylesheets.
- **Sensitivity proven by planting the damage**: an inline `<script>`, an inline `onclick`, a CDN
  `<script src="https://…">`, and a second `data:` URL — each caught by the assertion named for it, and
  each a change that would have passed the whole suite before this slice.
- **Blazor's reconnection overlay was checked** rather than assumed: `DefaultReconnectDisplay.ts`
  creates a `<style>` and assigns `innerHTML`, which is why `style-src 'unsafe-inline'` cannot be
  dropped by moving the components' blocks alone. No `ImportMap`, no WebAssembly render mode, no inline
  handler — so none of the keywords Microsoft's starter policy carries is needed here.
- **`SpecificationVersionTests` re-run against the edited specification**: header 1.9, entries
  1.9 … 1.0 descending, both assertions hold.
- **Governance gate 3 re-run over every delivered file**: two matches, both pre-existing lines in
  `docs/DOCUMENTATION_REVIEW.md`, which is on `RECORD_FILES` by literal path. Nothing new matches.
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor
  occurs exactly once.** `docs/BUILD_PROGRESS.md` was verified byte-for-byte to be its existing bytes
  plus one appended section.
- **Balance checked on all five C# files with string, char and comment literals removed** — braces,
  parens and brackets all zero. (A crude counter reports two unmatched parens in the contract test;
  they are inside the literal `"url("`, which is what that test is looking for.)
- **`.editorconfig` hygiene checked on every delivered file**: LF endings, final newline, no
  whitespace-only lines, no trailing whitespace, no context-dump separator.

## Still not a file, and still yours

The tag, unchanged from Slice 22's and Slice 23's note. `release.yml` has still never executed; a
`workflow_dispatch` off `main` publishes only `ghcr.io/kusl/myrestaurant:sha-<commit>` and skips the
release-notes job, so it rehearses the path without spending a version number. Still a suggestion,
still not in this archive.
