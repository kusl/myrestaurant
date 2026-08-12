# M6 Slice 34 — the number written twice (F-65), the fifth copy (F-66), and the gate that read prose (F-67)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-34-detail-vocabulary-and-touch-targets.tar.gz
git status
```

**Files to DELETE: none.**

**No `git add` is required.** Every file in this archive already exists and is tracked — no new files and no
new directories in this slice, so `git ls-files` already sees everything the gates read.

---

## Read this first

**Slice 33 is in the tree and has not been run.** The terminal logs here are Slice 32's — 1074 tests, 16
scenarios — and the Slice 33 commit was applied without being exercised. This slice is built on top of it,
against the actual text of it. If the first run is red, both slices' changes are candidates, and the honest
order to read them in is Slice 33 first.

---

## What this slice is

Stage 1b's second half. Five components stop declaring their own copy of one vocabulary, the last two
retired names leave the tree, and §16.3 scenario 16 goes from six surfaces to ten.

Three findings came out of doing it, and the first one was **blocking**:

- **F-67** — adding `.chip` and `.muted` to `SharedSelectorPrefixes`, which the plan required, reported
  findings on `KitchenBoard`, `CounterBoard` and `CounterSitting`. All three are correct: each names the
  shared vocabulary it leans on in a **CSS comment**. The fact matched a prefix against the *text* of a
  `<style>` block, stripping Razor comments and not CSS comments — while the custom-property fact thirty
  lines below it in the same file stripped both. So the rule's reach was bounded by which shared names
  happened not to appear in somebody's prose.
- **F-66** — `ManageMenuItem`, `ManagePerson`, `ManageTable` and `TableDisplays` each declared their own
  inline copy of one `.manage-*` vocabulary: twenty duplicated rules, five drifted. Three overlaps with
  `app.css` are the substance. `.visually-hidden` was still the deprecated `clip: rect(…)` on all four,
  which is exactly what Slice 30 said it centralised the name to remove. `.chip-ok`/`.chip-warn` were pinned
  to `#fdecea`/`#a3261c` against the palette's `#fbeaea`/`#7f1d1d`. And `.manage-inline-form input`/`select`
  had **no `min-height` and no font-size floor**, so the one control each page exists for was ~34px against
  §11.12's 44 and zoomed an iPhone's viewport on focus without zooming back.
- **F-65** — found by writing the assertion F-66 needed. `.session-link` and `.link-button` both declared
  `min-height: 2.25rem`. That is 36px against the 44 §11.12 names those exact controls in by name, and
  between them they are the **sign-out control in the header of every page in both layouts** and the
  destructive action on four administration surfaces. The comment above `.session-link` said the links
  "carry the §11.12 target height" and used "vertical padding rather than a min-height" — above three lines
  declaring a `min-height` and no padding at all.

---

## The decisions to veto, if you want to

**1. The barrier grew to ten surfaces, which is the thing I asked you about and you said continue to.**

Six indexes plus four detail pages, built from identifiers scenario 16 already mints. Fifteen controls
expected, floor fourteen. The alternative was leaving the barrier at six and recording four newly converted
pages as unmeasured — but converting a page and not measuring it is how F-59 survived four milestones, and
all four had 34px form controls at the moment they were added.

**A red first run is a real possibility and would be information.** These four pages have never been laid
out at 375px by anything. If one overflows, the failure message names the widest element outside a scroll
container.

**2. `/administration/sittings/{sitting}` is converted and deliberately not measured.**

Reaching a sitting needs a guest, a table token and a join before there is an identifier for the route —
scenario 3's arrangement — and an invented identifier meets the not-found panel, which has no page head, so
the barrier would fail on arrival rather than measure anything. Its conversion rests on the contract test and
on reading `app.css`. Same trade hidden-records is already measured with, one route deeper.

**3. The `.manage-` name is kept rather than renamed, which is the opposite of what `.admin-header` got.**

`.page-head` exists because a shared declaration under the old name would have lost the specificity argument
on every page still carrying an inline copy — an argument about a migration that **spans slices**. This one
spans none: all four holders empty in the same commit. The reasoning is recorded beside the declaration,
because the two decisions look contradictory and the difference is what makes either correct.

**4. Three of the four `.manage-*` pages end with no `<style>` block at all.**

`ManageTable`, `ManagePerson` and `ManageMenuItem` turned out to declare nothing that was theirs alone. Each
keeps a Razor comment saying so, because an empty absence and a deliberate one look identical.

**5. `.link-button` is 8px taller everywhere, including "Sign out" in the header of every page.**

That is the fix for half of F-65 and it is not confined to the administration area. If the header looks
wrong to you on a phone, that is a judgement I cannot make from here.

**6. `ManageMenuItem`'s history table became a shared record list.**

It was a table inside `overflow-x: auto`, so the *document* never scrolled sideways and the barrier would
have passed it — but three unlabelled cells in a card is the half of F-59 about the reader rather than the
affordance.

---

## Files in this archive (14, all complete, none new)

```
docs/BUILD_PROGRESS.md
docs/DOCUMENTATION_REVIEW.md
docs/MENU_AND_HANDHELD_PLAN.md
docs/TECHNICAL_SPECIFICATION.md
src/MyRestaurant.WebApplication/wwwroot/app.css
src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor
src/MyRestaurant.WebApplication/Components/Pages/Administration/ManagePerson.razor
src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageSitting.razor
src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageTable.razor
src/MyRestaurant.WebApplication/Components/Pages/Administration/TableDisplays.razor
tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs
tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs
tests/MyRestaurant.WebApplication.Tests/Components/HandheldLayoutContractTests.cs
_CHANGES.md
```

`docs/TECHNICAL_SPECIFICATION.md` moves to **v1.19**; the header and the newest changelog entry move
together, which is F-48's rule and is asserted by `SpecificationVersionTests`.

---

## Where to look when you run it

```
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. The authored-text count does NOT move —
#    no new files. If it moves, something else is in your working tree.

bash scripts/check_repository.sh
#    expect: 3 gates plus a SKIP, exit 0. Gate 3 must still report "none": nothing added here
#    asserts a repository setting.

bash scripts/ci_local.sh
#    expect: 8 numbered gates, same number and same order as Slice 33.

dotnet build
#    the five Razor files are the likely site of a complaint. TableDisplays and ManageSitting are
#    substantially restructured; the other three lost a <style> block and gained a .page-head wrapper.

dotnet test
#    expect: 1077 total, 0 failed. Arithmetic from 1074 (Slice 32's observed run) plus Slice 33's two
#    new facts plus this slice's one. If Slice 33 has not run, this number is a prediction about two
#    slices at once.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 16 passed, 0 skipped. Scenario 16 is THE ONE TO WATCH: it now opens four pages nothing has
#    ever measured. A failure there names the surface, the two numbers, and the widest element outside a
#    scroll container.
```

**If scenario 16 is red on a detail surface**, the message distinguishes the three cases: a page wider than
its viewport, a control whose box lies outside it, or a control under 44px. The first two are layout
findings on a page that has never been measured; the third would mean a rule in `app.css` is being overridden
by something I did not find.

---

## What I could not verify

No .NET SDK and no browser here. All seven contract facts were ported to Python and run: **seven pass on the
delivered tree, five fail on the tree as it stands**, which is a before/after rather than a claim. Ten
planted regressions, ten results as designed — including the one that must **not** fire, a CSS comment
naming `.chip` and `.muted`, which is F-67 demonstrated rather than asserted. Brace, paren, bracket, CS1620,
CS4007, Razor tag-tree and byte hygiene all clean, each proven sensitive.

Nothing compiled. No browser rendered anything. `docs/BUILD_PROGRESS.md` has the full account, including one
scanner defect I introduced and fixed that would otherwise have had me "correcting" five correct files.
