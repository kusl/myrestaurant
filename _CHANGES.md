# M6 Slice 15 — §16.3 scenario 12, and the last placeholder in the matrix

Every file below is a **full file** at its **repo-relative path**. Extract at the repo root and the
contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-slice15-reset-and-reenroll.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `Program.cs` edit, no `.slnx` edit, no new file and therefore no new
test folder.

## The files

| File | Change |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | §16.3 scenario **12** implemented; its placeholder and the now-unused `PendingHarnessExtension` removed |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AccountJourneys.cs` | `SignOutAsync` strict-mode fix; §3.5 obligation (2); §3.4 and §3.3 voluntary surfaces |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` | §3.7 credential reset; the management page's fact chips read by label |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManagePerson.razor` | one additive class on the reset panel's password (**one line** plus its comment) |
| `docs/BUILD_PROGRESS.md` | Slice 15 appended (**complete file**, 5,572 lines — I did the appending) |
| `_CHANGES.md` | this file |

All five pre-edit files were checked against the SHA-256 hashes `export.sh` recorded in `dump.txt`
before anything was touched. **All five matched**, so every byte I did not edit is known identical to
your working tree.

**Scenario 12 is the fifteenth and last of §16.3.** There are no `[Fact(Skip)]` placeholders left in
`EndToEndScenarios.cs`, which is why `PendingHarnessExtension` goes with it — a `private const` with
no remaining reference is an IDE0051 waiting for the next CI run, where
`EnforceCodeStyleInBuild` meets `TreatWarningsAsErrors`.

## Two harness problems this scenario found before it could be written

### 1. `SignOutAsync` could not sign a trapped principal out

This is a real bug in the existing harness, not a shortcoming of the new code, and it would have
surfaced as a strict-mode violation thirty seconds into the run.

`ObligationsEnforcement.IsExemptPath` exempts sign-out and the two obligation pages and nothing else
— **`/sign-in` is not on that list.** So a principal holding an obligation cannot reach the sign-in
page at all, and sign-out is the only route to a fresh cookie. But both obligation pages render a
sign-out form of their own beside the header's — *"Not ready right now?"* on
`ChangePasswordRequired.razor`, *"Done for now?"* on `EnrollTotpRequired.razor` — because §3.5
promises leaving is always possible. `SignOutAsync` held a bare

```csharp
ILocator signOutButton = page.Locator("form.sign-out-form button[type='submit']");
```

which resolves to two elements on exactly those pages, and every `Locator` method that acts on a
single element throws a strict-mode violation when it does. `.First` fixes it, and taking the first
is safe rather than merely convenient: the two forms are identical in effect — same endpoint, same
antiforgery token, neither carries a `returnUrl`, so `SafeLocalReturnUrl(null)` sends both to `/` —
and the header's comes first in document order. I counted the forms on both pages to confirm it is
exactly one page-level form beside the layout's, not more.

Scenario 12 signs a trapped principal out twice, so this was not optional.

### 2. One person needs two browsers, and the reason is `passkey.js`

A WebAuthn private key never leaves the authenticator that minted it, so the passkey this scenario
registers belongs to one browser context for good. That context **cannot also be where the password
sign-ins happen.** `passkey.js`'s `tryAutofillPasskey` fires a conditional-mediation
`navigator.credentials.get()` on *every* sign-in page load, and `VirtualAuthenticator` is configured
with `hasResidentKey: true` and `automaticPresenceSimulation: true` — so once a discoverable
credential exists in that context, a "password sign-in" there may be answered by the authenticator
before a password is ever typed.

It would still land on the forced-change page. **The scenario would pass, for the wrong reason, and
the clause about the password path would be asserting nothing.** `SignInWithPasskeyAsync`'s existing
comment already anticipates this happening — *"an authenticator that simulates presence can satisfy
it with no gesture at all"* — it just had not yet mattered.

So the staff member gets a **device** (virtual authenticator; holds the passkey; does the passkey
sign-ins) and a **terminal** (no authenticator, so its conditional request is never satisfied; does
the password walk). The one password sign-in that happens on the device is the first one, and it is
safe there for a stated reason rather than by luck: the authenticator is still empty at that point,
so the conditional request has nothing to answer with.

## Why the sign-out ordering is load-bearing

`ObligationsMiddleware` decides from the cookie's claims, not from the row. So the device signs out
**before** the terminal clears the two flags. Left signed in, its cookie would still carry
`must_change_password` and it would still be redirected — and the closing assertion that the pipeline
*releases* a passkey session could not then tell a fresh cookie from
`ChangePasswordRequired.razor`'s own stale-claim guard firing on the way past.

That closing assertion is the point of including it. Without it, *"the passkey path hits the
pipeline"* is satisfied equally well by a middleware that refuses passkey sessions **permanently**,
which would be a considerably worse defect than the one the earlier assertion guards against.

## Four form posts of arrangement, and no shortcut available

§3.7's create-staff form writes `must_change_password` and nothing else — no secret, no passkey, and
deliberately not `must_enroll_totp` — so an enrolled account with a passkey cannot be arranged by an
administrator. It could have been arranged by `INSERT`, and that would have been the wrong move:
**the reset under test probes `totp_secret_protected`** to decide whether to clear an authenticator at
all, so a fixture that got that one column wrong would produce a password-only reset and the
scenario's second obligation would never exist. The account enrols itself through §3.4's voluntary
page and adds its own passkey through §3.3's, which is what a real staff member does.

## Chips rather than columns

Every flag this scenario asserts on has a row in `person` that could be read directly, and reading it
directly would prove nothing about §3.7: that `must_change_password` is set is one claim, and that an
administrator can *see* it is another. Only the second is a product behaviour.

So the flags are read as the chips `ManagePerson.razor` renders, found by the `span.manage-label`
beside each group rather than by position — the same reasoning as `TickRoleAsync`, and for the same
reason: indexing works today and silently starts reading roles as credentials the day a fourth fact
is added above an existing one, which is exactly the kind of failure a scenario would blame on
authorization.

The **Credentials** group is the interesting one, because it is *derived* rather than stored.
"Authenticator" appears iff `totp_secret_protected IS NOT NULL` (§3.4 has no enrolled column), so the
chip's absence after the reset is the surface agreeing the secret is gone, and its return at the end
is the surface agreeing a new one landed.

Two new sites needed **declared** text rather than rendered text, and Slice 14's
`ScreenText.DeclaredAsync` is why they are not two more red tests: `.manage-label` is upcased for the
eyebrow treatment, so the label the reader matches on comes back as `STATUS` and every lookup would
miss; and `.chip-role` is capitalized, so a role chip whose markup says `kitchen` — the stored
vocabulary, which is what `person_role.role_name`'s CHECK constrains — reads back as `Kitchen`.

## Assertions in pairs, never singletons

Every claim in the post-reset block is of the form *"this chip is there now"*, which a chip that had
always been there satisfies perfectly. So the same three groups are read **before** the reset as
well, and the pair is the assertion.

The recovery codes take the same shape: two sets of ten, asserted **disjoint**. §3.7's reset deletes
every `totp_recovery_code` row and §3.4 replaces the set on confirmation, so an overlap of even one
code would mean a code the administrator's reset was supposed to have destroyed is still live — and
nothing else in the suite would notice.

Three things are also asserted **not** to have moved across the reset, because a reset that had
quietly deactivated the account or dropped its grant would clear every obligation assertion below it
and be caught by nothing else: the account is still `Active`, it still holds `kitchen`, and it still
carries a `Password`.

## One non-default `ReturnUrl`, deliberately placed

The pipeline's destination in this scenario is `/`, which is also `SafeLocalReturnUrl`'s fallback — so
*"lands home"* on its own cannot separate **carried the destination across two redirects and two
cookie re-issues** from **dropped it**. The trapped device therefore asks for `/kitchen`, the one
board its role could otherwise walk straight into, and the redirect is asserted to carry
`ReturnUrl=%2Fkitchen`.

That is two things at once: §3.5's *"no authenticated endpoint is reachable"* asserted on a real area
page rather than on a sign-in navigation, and the one place in the scenario where step (3)'s carry is
distinguishable from the default. `Assert.Equal("/", new Uri(url).AbsolutePath)` stays where §16.3
put it, and the comment says which of the two it is and is not evidence for.

## The one product change

`ManagePerson.razor`'s reset panel wrote the temporary password into `<p class="totp-secret">`, which
is the same collision Slice 12 fixed on `CreateStaff.razor`: an element holding a password,
addressable only by a class named for an authenticator key.

It mattered more here than there. The account this panel just reset is on its way to
`/account/enroll-totp-required`, whose own `p.totp-secret` holds a **real** authenticator key — so one
selector meant two different secrets on two consecutive screens of one scenario. The element gains
`.staff-temporary-password` beside the class it already had, and the harness reuses the constant Slice
12 introduced rather than adding a second name for the same thing: both panels show "the one-time
temporary password this administration surface just minted", and one selector for one meaning is the
point.

There is **no CSS rule for that name anywhere in `src/`** — I checked — so nothing changes on screen.
That is the whole of the product diff: one line, plus its comment.

## The kitchen role, chosen rather than defaulted

§3.4's authenticator is a staff credential — §17 accepts a password-only counter and nothing in the
specification asks a guest to carry TOTP — so a staff account is the faithful subject of "a
TOTP-enrolled user". And the role gives the closing claim something to point at: `MainLayout` renders
the kitchen link to the kitchen role and to nobody else, **not even to administrators**, so "lands
home" becomes "landed home as this person, with this role's door on screen" rather than "reached a
page". Scenarios 9 and 10 use the counter role; a fixture of its own keeps a failure here
unambiguous.

## Build and test

```bash
dotnet build
#    expect: all seven projects succeed, 0 errors

dotnet test
#    expect: total 971, failed 0, succeeded 957, skipped 14 — one fewer skip than Slice 14.
#    Scenario 12 moves from [Fact(Skip)] to [Fact] + Assert.SkipUnless, and xUnit counts an
#    Assert.Skip as skipped too — so the total does not move and one test crosses from the
#    unconditionally-skipped column into the conditionally-skipped one.

bash scripts/ci_local.sh --with-all

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 15 passed, 0 skipped — the first fully green E2E run.
#    Scenario 12 adds roughly 40-50s: a /setup wizard, a staff account, four Argon2id hashes across
#    three password sign-ins and two forced changes, two TOTP confirmations, two passkey ceremonies
#    (one attestation, two assertions), a reset, four reads of the management page, and no waiting
#    on any timer.
```

Note that `dotnet test` at Slice 14 was **971 / 0 failed / 956 succeeded / 15 skipped** and I have
not seen a run since — the `claude-terminal.txt` in this dump predates Slice 14 (it still shows the
`SETTLED TOTAL` failure and two skips). If Slice 14's own numbers came out differently, adjust these
by the same offset: this slice moves exactly one test from skipped to run.

## Where to look if this breaks

**A strict-mode violation on a sign-out.** Something else has grown a second `form.sign-out-form`, or
the `.First` did not survive the merge. The message names the selector and the count.

**`The browser is not on /account/enroll-totp-required`.** The forced password change cleared
obligation (1) and the pipeline did *not* pick up obligation (2). Either `must_enroll_totp` was never
set — check `reset.ClearedAuthenticator`, which is asserted true one screen earlier, and the panel
sentence it reads — or `RefreshSignInAsync` issued a cookie that dropped the second claim.

**A password sign-in that lands somewhere unexpected on the device.** The two-browser split has
collapsed. The device holds a discoverable credential from step (b) onwards; if a password sign-in is
attempted there after that point, `passkey.js`'s conditional request may answer it first.

**`ReturnUrl=%2Fkitchen` missing.** `RedirectTargetFor` stopped composing `{PathBase}{Path}{QueryString}`,
or `/kitchen` became exempt. The former is the interesting one — it is what §3.5 step (3) rests on.

**Recovery-code sets overlapping.** `ResetCredentialsAsync` stopped deleting
`totp_recovery_code` rows, or `TotpEnrollment.IssueRecoveryCodesAsync` stopped replacing the set.
Either way a code an administrator's reset was meant to destroy is still live.

**A chip lookup failing with *"has no 'Credentials' group of facts"*.** A `span.manage-label` was
reworded or a fourth fact group was added. The message lists every label the page did offer, so the
fix is a one-word edit at the call site.

**A passkey ceremony failing on the device only.** `OpenIsolatedPageAsync(withVirtualAuthenticator: true)`
is what makes that context capable of one, and a credential minted in one context is invisible in every
other. If `SignInWithPasskeyAsync` starts failing in step (e) or (g) but the `/setup` wizard's
attestation still works, the credential and the browser have been separated.
