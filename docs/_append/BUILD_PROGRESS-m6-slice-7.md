### M6 Slice 7 — documentation catches up, and one dependency moves (landed)

Not a feature slice. Two slices of product landed while the documentation stood still, one package went a
minor version behind, and an analyzer started failing the CI build. This closes all three, and touches no
behaviour whatsoever — no `.cs`, no `.razor`, no SQL, no migration, no wiring.

`dotnet test` counts are unchanged at **971 total / 956 succeeded / 15 skipped**, and
`MYRESTAURANT_E2E=1` unchanged at **8 passed / 7 skipped**, because nothing here is executable.

---

#### Why the documentation had drifted two slices

`README.md` still said *"five of the fifteen §16.3 scenarios"* and *"all ~934 facts"*. Both were true on
the day they were written and neither had been true since. That is the ordinary failure mode of a status
sentence living in a document nobody edits when the status changes, and the fix is not a process — it is
noticing, which is what this slice is.

More consequentially, **M6 Slice 5 shipped a product surface without a ledger row.** It built `/register`,
argued in its own change notes that no specification edit was required, and was right about the narrow
question it asked — R§4.3, S§4.4 and S§11.1 all already mandated guest registration, so nothing they said
became false. But the atomic-documentation rule (R§10 · S§18) is not only about contradictions. A behaviour
change lands with its ledger row, and a new anonymous route that writes a `person` row is a behaviour
change by any reading.

So **F-37** is entered now, late, and says so. Its content is the interesting part rather than its
existence: it is the *same shape* as F-35, the profile page. A requirement stated plainly in
`REQUIREMENTS.md`, a mechanism described in the specification, and no line in §19's build order claiming
it — so every milestone review passed, because every milestone had done what its own list said. Twice is a
pattern. The ledger's closing note now names it, along with the cheap guard: when a requirement section
names a capability, §19 should name the milestone that owns it. Both gaps were found the same way in the
end — by somebody trying to *use* the thing, not by anybody re-reading the documents.

The specification gains **§11.8 `/register`**, appended rather than inserted. §11.7 (the wall clock) keeps
its number because a dozen source files cite it in comments, and renumbering to put registration next to
§11.6 `/account` — where it belongs thematically — would have silently falsified every one of them. Section
numbers are an interface once code quotes them.

Also new: **S§17** now carries the missing rate limit as a named accepted risk rather than a note in a
change file nobody will find again. The reason it is not a two-line addition is written down — the limiter
lives inside `AddRestaurantDisplays`, `RateLimiterOptions.OnRejected` and `RejectionStatusCode` are
single-valued, and a second `AddRateLimiter` policy would silently take over the rejection handler so that
a refused registration answers with §4.2's *"Too many pairing attempts from this device"*. Wrong, and
deliberate-looking, which is worse than absent. Four things bound it meanwhile, and none of them is a
policy; all four are recorded.

Specification version bumps **1.0 → 1.1** with a dated changelog, per its own §18.

---

#### The package

`Net.Codecrete.QrCodeGenerator` **3.0.0 → 3.1.0**, the only outdated reference in the tree.

The release is additive: balanced sizing for *structured append*, which is the feature that splits one long
text across several linked QR codes. Nothing here does that — every code this application renders encodes
one short URL. The three members it actually uses (`QrCode.EncodeText(string, Ecc)`, `QrCode.Size`,
`QrCode.ToGraphicsPath(int)`) carry identical signatures at the v3.1.0 tag, checked against the source
rather than inferred from semver.

One consequence worth stating because central package management hides it: this package is referenced by
**both** the web application and the end-to-end test project, so one bump moves both. That is not
incidental. `JoinQrCodes` asserts that a paired display is showing the code the table's secret produces by
recomputing the expected path with this same library, and that assertion is only real while both sides
encode identically. A version skew between them would turn scenario 2 into a test of the library.

No golden module path is pinned anywhere — the QR assertions are structural (`starts with <svg`, `has a
viewBox`, `path begins with a moveto`, `makes no external references`) — so the modules may legitimately
change shape without anything going red.

`dotnet list package --outdated` cannot see `NSubstitute`, because no project references it; it is a
version pin standing ready for §16.1's *"NSubstitute acceptable"*, not a dependency. Checked by hand: 6.0.0
is current. The comment in `Directory.Packages.props` now says so, so the next refresh does not have to
rediscover why one entry is invisible to the tool.

---

#### The analyzer

Four `xUnit2031` findings in `EndToEndScenarios.cs` — `Assert.Single(xs.Where(p))` where
`Assert.Single(xs, p)` is meant. Warnings on a workstation, **errors in CI**, since
`TreatWarningsAsErrors` is conditioned on `ContinuousIntegrationBuild`; the local build was green and the
strict build was not, which is exactly the split `Directory.Build.props` documents and exactly why
`scripts/ci_local.sh` exists.

Worth fixing rather than suppressing, and the reason is diagnostic rather than stylistic. `Assert.Single`'s
failure message prints the collection it was given. With `.Where(…)` in front, the collection it was given
is the *filtered* one — so a scenario that expected one soup line and found none reports an empty
collection, which is a restatement of the failure rather than information about it. The predicate overload
(`public static T Single<T>(IEnumerable<T>, Predicate<T>)`) prints every line that *was* there, which for
an end-to-end failure is most of the diagnosis.

The rewrite was applied mechanically rather than by hand, by a parser that only touches a call whose entire
argument is `<receiver>.Where(<lambda>)` — leaving `Assert.Single(xs)`, `Assert.Single(xs, p)`,
`Assert.Single(xs.Where(p).ToList())` and any occurrence inside a comment or a string literal alone. Run
across all 300-odd `.cs` files in the tree it changes nothing outside the four intended sites, and running
it twice changes nothing at all.

---

#### Carried forward, unchanged

The `docs/_append/` backlog is the one piece of housekeeping this slice does *not* fix, because merging it
is a `cat` the owner runs, not a file this archive can safely contain: `docs/BUILD_PROGRESS.md` is far too
large to regenerate, and shipping a partial one would overwrite the whole document.
