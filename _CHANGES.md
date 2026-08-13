# M6 Slice 37 — menu sections exist (Stage 2, section half), an advisory nobody read (F-74), the gate that could not see it (F-75), and a claim one word too strong (F-76)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-37-menu-sections-and-three-findings.tar.gz
git status
```

**Files to DELETE: none.**

**Four `git add` invocations are required**, and the gates will not see these files without them —
`scripts/check_tree.sh` walks `git ls-files`, so an untracked file is invisible to tree hygiene:

```
git add src/MyRestaurant.DataAccess/Migrations/0003_menu_sections.sql
git add src/MyRestaurant.DataAccess/Menu/MenuSectionDirectory.cs
git add src/MyRestaurant.DataAccess/Menu/MenuSectionAdministration.cs
git add tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionAdministrationTests.cs
git add tests/MyRestaurant.WebApplication.Tests/Deployment/VulnerabilityAuditParityContractTests.cs
```

**No new directory.** `Migrations/`, `Menu/` and `Deployment/` all exist. The migration needs no `.csproj`
edit — `MyRestaurant.DataAccess.csproj` already globs `Migrations/*.sql` as an `EmbeddedResource` — and the
test SDK globs `**/*.cs`, so neither test project needs one either. Everything else in this archive already
exists and is tracked.

---

## Read this first

**Slice 36 was green, exactly as predicted, and that is what this slice is about.**

```
Test summary: total: 1082, failed: 0, succeeded: 1082, skipped: 0
§16.3 end to end: 16 of 16
all local CI gates passed
```

Two of the three findings below were **printed by that run**. `NU1903` named a high-severity advisory twice
on every restore, and tree hygiene printed `all files end with exactly one LF` about a tree in which eleven
files ended with two.

---

## Menu — Stage 2's section half, and a moved stage boundary

`menu_section`, `menu_section_event`, `0003_menu_sections.sql`, `MenuSectionDirectory`,
`MenuSectionAdministration`, their registration, and twenty integration facts. **Nothing that already existed
changed** — `menu_item` still has four columns and no surface reads a section.

**The boundary moved, and it needs your eye.** The plan's Stage 2 was one migration adding both tables *and*
the three `menu_item` columns. `menu_section_identifier` is `NOT NULL`, so the moment that applies
`CreateMenuItem.razor` cannot write a row without a section, and six of the sixteen §16.3 scenarios drive
that real form — which is why the plan's own correction had to pull three surfaces forward to ship green.

This archive cuts between the **tables** instead. `0003` touches nothing existing, so the suite stays green
by construction rather than by inspection. The nullable-then-tighten alternative the plan rejected is
**still rejected**: `menu_section_identifier` goes from non-existent to `NOT NULL` in `0004`, so no reading
surface ever gains a code path for an item under no heading. The cost is one extra script, and DbUp journals
by name, so that is not a cost. The conditional seed moved to `0004` too, beside the backfill that needs it.

If you would rather have the whole of Stage 2 in one archive, say so and the surfaces come with it.

---

## The three findings

**F-74 — a high-severity advisory, riding in transitively, printed twice per restore.**
GHSA-q939-rpr3-3284 / CVE-2026-48798, high, CVSS 7.1, published 2026-08-12 — one day before your dump.
`ScpClient`'s recursive download builds local paths from server-supplied names with no containment check.
`Testcontainers` pins SSH.NET at 2025.1.0 and `Testcontainers.PostgreSql` drags it into both test projects.
One `PackageVersion` line fixes it, because transitive pinning has been enabled since M1. **Recorded as
unreachable rather than urgent**, because that is what the evidence says: `ScpClient` appears zero times in
`testcontainers/testcontainers-dotnet`, and nothing here names `Renci`, `SshNet`, `ScpClient` or
`SftpClient`. 2026.0.0 declares no known breaking changes and adds a `net10.0` asset the old version lacked.

**F-75 — the script that names its one blind spot had two.** `scripts/ci_local.sh` calls the boot-smoke job
*the one* gate it cannot reproduce; CI's `vulnerable package audit (advisory)` step had no local counterpart
for fourteen slices. That is F-74's mechanism, not a defect beside it. The audit is now gate 7, on CI's own
advisory terms, and `VulnerabilityAuditParityContractTests` holds both files to one command — including
`--include-transitive`, the flag that matters most here. **The header's claim is repaired by becoming true
again rather than by being softened.**

**F-76 — `exactly one LF`, checked as `at least one`.** Eleven files ended with two newlines. Gate 3 gains a
third half counting the last two bytes; the eleven are repaired in this archive, because a gate strengthened
without them is a gate delivered red. Each repaired file is byte-identical to its recorded SHA-256 but for
the removed newline — nothing else moved inside them.

---

## What was verified, and what was not

**A real PostgreSQL 16 was installed in the authoring environment**, so the schema half is measured. `0001`
and `0002` replayed, `0003` applied, then every constraint exercised in both directions and every statement
the two services emit executed — the `FOR UPDATE` read, the list and get, all four UPDATEs, the `MAX + 1`
probe, and the history read joined to `person`. The behaviour twenty facts assert was replayed: appends land
0, 1, 2; stored order is not alphabetical; after a move to 9 the next append is 10 and not the row count; two
sections at position 0 sort by name. **And the one that would have been easy to get backwards:** renaming the
same row `Apple` → `APPLE` does not trip the `citext` UNIQUE.

**Two simulations caught two real failures before packaging** — §16.4's contract test rejected a paragraph
stating two assertion counts, and the Markdown table gate found `\|\|` unescaped inside an Appendix A cell,
which is F-72's own finding. Both fixed; both gates then clean.

**Nothing compiled.** No .NET SDK here. Five C# files are new or edited; structural verification was
string- and comment-aware and proven sensitive by three planted defects, but it cannot see an overload — the
F-71 lesson. **The migration ran on PostgreSQL 16, not the 17 the stack uses, and DbUp did not apply it**;
its journalling and statement splitter are exercised for the first time on your run.

**Predicted test count: 1107** (1082 + 20 + 2 + 1 + 2). Arithmetic on the last observed figure, not an
observation. §16.3 stays at 16. Per §18, any other number is the next thing to chase.
