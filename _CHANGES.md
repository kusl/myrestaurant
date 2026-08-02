# M6 Slice 7 — documentation catches up, and one dependency moves

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 9 files as
modified/added, and **no deletions**.

```bash
tar -xzf m6-slice7-docs-and-dependency.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed or superseded. No migration, no schema change, no new package, no
`Program.cs` edit, no `.slnx` edit, and — deliberately — **not one `.cs` or `.razor` file**.

## Read this first: the archive does not contain the E2E fix

Your `dump.txt` is at commit `7172839`, which is **M6 Slice 5**. Your build output is from after **Slice
6**. I confirmed the gap three ways rather than assuming it:

- The dump's `EndToEndScenarios.cs` is 559 lines; your errors cite 566 and 568.
- The dump's `TableJourneys.cs:113` still holds the `string.Create` call that raised CS1620 — the bug you
  have already fixed.
- `github.com/kusl/myrestaurant@main` is at the same state: `EndToEndScenarios.cs` is 31,473 bytes there,
  byte-identical to the dump, and there is no `Harness/TableOrderJourneys.cs`, no `Harness/KitchenJourneys.cs`
  and no `BUILD_PROGRESS-m6-slice-6.md`. Slice 6 exists only on your disk.

So a "full file" for anything under `tests/MyRestaurant.EndToEnd.Tests/` would be a Slice 5 file wearing a
Slice 6 name, and extracting it would silently revert two scenarios. Those files are **not in this
archive**. Neither are `TableOrderSurface.razor` and `KitchenBoard.razor`, which Slice 6 also edited.

Everything that *is* here was untouched by Slice 6 — its own change notes list nine files and none of them
is documentation or `Directory.Packages.props`.

## The nine files

| File | Change |
| --- | --- |
| `Directory.Packages.props` | `Net.Codecrete.QrCodeGenerator` 3.0.0 → **3.1.0**; refreshed audit comment |
| `README.md` | five scenarios → eight, with a table; `~934 facts` → `~970`; `/register` in the layout and caveats; M6 roadmap |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.0 → v1.1**: new §11.8, §11.1 pointer, §17 accepted risk, §19 note, Appendix A row, changelog |
| `docs/REQUIREMENTS.md` | §4.3 points at the surface that implements it |
| `docs/DOCUMENTATION_REVIEW.md` | **F-37** added; status header and closing note updated |
| `docs/OPERATIONS.md` | §3 gains a paragraph on guests needing nothing from the operator |
| `docs/_append/BUILD_PROGRESS-m6-slice-7.md` | new |
| `docs/llm/vendor/fix_assert_single.py` | new — the xUnit2031 rewriter, in your existing vendor folder |
| `_CHANGES.md` | this file |

## The package bump, and why it is safe

3.0.0 → 3.1.0 is additive. The whole release is balanced sizing for **structured append** — splitting one
long text across several linked QR codes — and nothing here does that; every code this application renders
encodes one short URL.

I checked the v3.1.0 tag rather than trusting semver. All three members in use carry identical signatures:

```csharp
public static QrCode EncodeText(string text, Ecc ecl)   // QrCode.cs:79
public int Size => _modules.Size;                       // QrCode.cs:377
public string ToGraphicsPath(int border = 0)            // QrCode.cs:475
```

No test pins a golden module path — the QR assertions are structural — so the modules may change shape
without anything going red.

**One thing to know:** this package is referenced by the web application *and* the end-to-end test project,
and central package management moves both at once. That is load-bearing. `JoinQrCodes` proves a display is
showing the code the table's secret produces by recomputing the expected path with this same library, and
that assertion is only real while both sides encode identically. Never pin them apart.

`dotnet list package --outdated` is silent about `NSubstitute` because nothing references it — it is a pin
standing ready for §16.1, not a dependency. Checked by hand: 6.0.0 is current. The props comment now says
so, so the next refresh does not rediscover it.

## The four xUnit2031 errors — run this, do not hand-edit

`Assert.Single(xs.Where(p))` → `Assert.Single(xs, p)`. Worth fixing rather than suppressing, for a reason
that is diagnostic rather than stylistic: `Assert.Single` prints the collection it was given on failure,
and with `.Where(…)` in front that is the *filtered* collection — so a scenario expecting one soup line and
finding none reports an empty collection, which restates the failure instead of explaining it. The
predicate overload (`public static T Single<T>(IEnumerable<T>, Predicate<T>)`, confirmed in
`xunit/assert.xunit`) prints every line that *was* there.

Since I cannot see your file, here is a parser instead of a guess. It ships in the archive at
`docs/llm/vendor/fix_assert_single.py` — the folder you already use for generated artifacts, and one
`export.sh` excludes, so it will never appear in a future dump:

```bash
cd /home/kushal/src/dotnet/myrestaurant

python3 docs/llm/vendor/fix_assert_single.py \
  tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs --dry-run --expect 4

# review the before/after it prints, then drop --dry-run
python3 docs/llm/vendor/fix_assert_single.py \
  tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs --expect 4
```

It only rewrites a call whose **entire** argument is `<receiver>.Where(<lambda>)`. `Assert.Single(xs)`,
an already-correct `Assert.Single(xs, p)`, `Assert.Single(xs.Where(p).ToList())`, and any occurrence inside
a comment or a string literal are left alone — it skips string, verbatim, interpolated, raw and character
literals as well as both comment forms, so a `)` inside `"with a )() note"` cannot close a call early. I
ran it across all 309 files of the Slice 5 tree: zero changes, byte-identical output. Running it twice
changes nothing.

Two of your four sites I recovered exactly; it will produce this for them:

```csharp
GuestOrderLine soupLine = Assert.Single(afterFulfillment,
    line => line.Name.Contains(service.Soup.Name, StringComparison.Ordinal));
GuestOrderLine pieLine = Assert.Single(afterFulfillment,
    line => line.Name.Contains(service.Pie.Name, StringComparison.Ordinal));
```

## Build/test checklist

```bash
cd /home/kushal/src/dotnet/myrestaurant

# 1. The package bump.
dotnet restore
dotnet list package --outdated     # expect: no updates for any project

# 2. Nothing executable changed, so nothing should move.
dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15  (unchanged)

# 3. The strict build — this is the one that was red.
bash scripts/ci_local.sh --with-all
#    expect: no xUnit2031, no CS-anything

# 4. Unchanged, but the QR bump touches both, so prove it once.
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 8 passed / 7 skipped
#    scenarios 2 and 15 are the ones that would notice a QR regression

# 5. Append the progress block.
cat docs/_append/BUILD_PROGRESS-m6-slice-7.md >> docs/BUILD_PROGRESS.md
```

## Housekeeping carried over

`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2". Fifteen appends are unmerged (you
merged Slice 5's already; Slice 6's is on your disk and not in this archive):

```bash
for slice in m4-slice-2 m4-slice-3 m4-slice-4 \
             m5-slice-1 m5-slice-2 m5-slice-3 m5-slice-4 m5-slice-5 \
             m6-slice-1 m6-slice-2 m6-slice-3 m6-slice-4 m6-slice-6 m6-slice-7; do
  cat "docs/_append/BUILD_PROGRESS-${slice}.md" >> docs/BUILD_PROGRESS.md
done
```

`shellcheck` is still not installed locally, so `ci_local.sh` step 1 only parses:

```bash
sudo dnf install ShellCheck
```

## Where to look if this breaks

| Symptom | Where to look |
| --- | --- |
| `NU1101` / restore cannot find 3.1.0 | The feed is stale or offline. `Directory.Packages.props` is the only place the version lives. |
| Scenario 2 or 15 fails on the QR after the bump | Version skew between the app and the test project — impossible under CPM unless a `.csproj` regained an explicit `Version=`. Check both. |
| A QR unit test fails | It should not; they are structural. If one does, it pinned a golden path at some point and that is the bug. |
| `xUnit2031` still reported | The script printed fewer than 4 sites, meaning a call has a shape it declines to touch — `.Where(p).ToList()`, or a query-syntax `where`. Fix that one by hand. |
| `dotnet build` green, `ci_local.sh` red | Working as designed: `TreatWarningsAsErrors` is on only under `ContinuousIntegrationBuild`. Always ask the strict question before pushing. |

## What is next

The fresh dump, then scenario **5** — a second guest joins on a fresh token, sees the first guest's order
live, and the first guest sees the roster update. It is the first with two guest circuits watching each
other rather than one guest and one staff board, but Slice 6 built the harness for exactly this shape, so
it should be short. Then 7 through 12, and the backup/restore drill.

## The one-line why

Two slices of product had landed and the documents still described the tree from before them — including a
ledger that had gone a whole surface without a row, for the second time, in the same way.
