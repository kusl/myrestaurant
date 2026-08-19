# M6 Slice 54 — the transition that broke the build, and the history a picture had never had

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-54-picture-history.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` IS required — two new files, both in directories that already exist.**

```
git add src/MyRestaurant.DataAccess/Menu/MenuItemImageEventLog.cs
git add tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageEventLogTests.cs
```

That is **two** new files. `git status` should show **eleven modifications and two untracked paths** — plus
whatever `docs/llm/vendor/claude-output.txt` was already showing, which your last run had modified before this
archive was built. Anything else untracked means the archive was extracted somewhere other than the repository
root.

**No new directory, no new package, no `.slnx` edit, no `.csproj` edit, no migration, no `compose.yaml` edit,
no `REQUIREMENTS.md` edit, no `OPERATIONS.md` edit, no ADR amended, no `export.sh` edit, no `app.css` edit,
and no §16.3 scenario added or extended.**

---

## First: your build is broken by one character, and this fixes it

```
TableOrderSurface.razor(248,22): error RZ1010: Unexpected "{" after "@" character.
```

Line 248 was `@{ int headingIndex = -1; }`, and it sits inside the `else` block that renders the grouped
menu — where Razor is **already parsing C#**. `@{` is the transition *from markup into code*, so writing it
where there is no markup to leave is a syntax error. It is now a bare `int headingIndex = -1;`.

Everything else in your run was green and legitimately so. Tree hygiene passed on 365 files, governance
passed, all twelve shell scripts passed `shellcheck`, and `run.sh --smoke` got a 200 out of `/healthz/ready`
against real PostgreSQL. **A Razor syntax error is invisible to every gate in this repository except the one
that compiles** — and because `MyRestaurant.WebApplication` did not build, every project depending on it never
ran. That is F-82 a fourth time, and this time the cost is exactly nameable: the two facts that would have
reported the second defect below are in the project that did not build.

**No gate is added for it, and I want to be explicit that this is a ruling rather than laziness.** Deciding
markup context from code context is not decidable from text without a Razor parser. The rule *is* already
executed — by `csc`, in three seconds, naming the file and the column — so a second implementation inside the
test suite would be **two parsers for one rule with the worse one blocking**, which is F-59's mechanism
inverted, and F-71's ruling covers it: a test re-asserting what the compiler already refuses is a monument.
What is added is the reason written beside the statement, and a residual recorded in §18.

Worth knowing, because it is the part that would catch you again: the three other `@{` blocks in this tree are
all correct, and every one of the four sits after a closed element at the same indentation. What separates
them is the **nearest enclosing construct** — `<section>`, `<li>` and `<div>` for the legal three, `else {` for
this one — and that is not visible on the line.

## The menu progress: Stage 4d, the picture's history

The tree named this slice itself. §16.4 has said since Slice 51:

> *There is deliberately no `IMenuItemImageEventLog` … the reader arrives with the surface that renders it,
> exactly as `IMenuSectionEventLog` arrived with the section editor.*

`0006` created `menu_item_image_event`, three slices wrote to it, `0007` took it to four event types, and
nothing could read it — so §11.4 could not say when a photograph last changed or who changed it, and
`alt_text_changed` was written from its first day and rendered nowhere.

**The one thing worth your attention is a join that is deliberately absent.** Every other reader in this
family joins the row its events are about, and here that row is *usually gone*: a replace mints a new
identifier and deletes the old row so that `Cache-Control: immutable` is a true statement, and a removal
deletes it outright. An INNER JOIN would return a history that silently **begins at the current photograph**
— it reads like a complete history and nothing throws — and a LEFT JOIN would add a column null on every row
but the newest. `0006` declared no foreign key on that column precisely so the log can outlive its subject;
this slice asserts it rather than trusting the comment that says so.

The panel goes under the picture forms on `/administration/menu/{id}`, and it renders **whether or not a
picture is attached now** — because *no picture now, three of them previously* is exactly the state the page
could not describe. No CSS: `.record-list`, `.manage-subheading` and `data-label` are all §11.12 vocabulary,
and `.manage-subheading` was already declared and already in use on `ManageSitting.razor`.

## And a second defect, found reading §7 to write the reader (F-105)

§7 described this table, in the indicative, as `attached | replaced | removed` with **two** named
biconditionals. It is four types and three, and has been since `0007`. **Three sections above it**, §7 states
in bold that `menu_item_event`'s vocabulary is *not counted in prose anywhere* and that the list there is the
only copy — a rule this project wrote down after F-77 and then did not apply to the table it added next.

Corrected, the count deleted in favour of a rule, and **discharged with something executable**, because on
this project's own evidence a corrected sentence is worth nothing: the next migration to widen this vocabulary
will be written by somebody who has not read it. `MenuEventVocabularyContractTests` gains a fact that every
type the migration admits has a sentence on the surface that renders that log.

**This is the vetoable part of the delivery.** If you would rather not take a gate that reads a `switch` in
Razor text, delete the `EveryPictureEventType_HasASentenceOnTheSurfaceThatRendersIt` method and revert that
file's `ItemConstraintName` / `PictureConstraintName` pair to the single `ConstraintName` const it had, with
`ReadDeclaredVocabulary()` taking no argument. The §7 correction stands either way; the count becomes **1249**
and `MinimumCountedClasses` goes back to **28** in
`tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs`. You would also want to
drop the `holds 3 assertions` paragraph's third clause in §16.4 back to two, and F-105's ledger rows lose their
*names something executable* half.

---

## Test count prediction

**1242 → 1250.** Note that 1242 was never tested — the build failed before `dotnet test` produced a count — so
the last verified number is **1233**.

| Class | Was | Now | Why |
|---|---|---|---|
| `MenuItemImageEventLogTests` | — | 6 | the stream oldest-first; the payload each type is allowed; the history outliving every picture it names; a cleared caption as `""`; the actor fallback; one dish's history and two kinds of empty |
| `MenuWiringTests` | 27 | 28 | the picture history reader resolves in a scope |
| `MenuEventVocabularyContractTests` | 2 | 3 | every picture event type has a sentence on the surface that renders it |

`MinimumCountedClasses` moves **28 → 29**. §16.3 stays at seventeen. Any deviation from 1250 is the first thing
to investigate.

## What I could not check

Nothing was compiled — no SDK, no database, no browser. Given what this slice repairs, that is worth stating
twice: the instrument that would have caught F-104 is the one this environment does not have, and the two it
does have (tag-tree walk, brace balance) both passed on the broken file.

The new SQL has never run. The panel has never been rendered, so whether `Replaced with a new picture —
image/jpeg, 284736 bytes` wraps acceptably in a `.record-list` cell at 375px is a judgement nobody has made
against a real screen. The byte length is rendered bare rather than as `278 KiB`, matching what §11.4's
existing picture facts already do.

## Files in this archive

**New (2):**

```
src/MyRestaurant.DataAccess/Menu/MenuItemImageEventLog.cs
tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageEventLogTests.cs
```

**Modified (11):**

```
src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor
src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor
src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs
tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs
tests/MyRestaurant.WebApplication.Tests/Events/MenuEventVocabularyContractTests.cs
tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs
docs/TECHNICAL_SPECIFICATION.md
docs/DOCUMENTATION_REVIEW.md
docs/MENU_AND_HANDHELD_PLAN.md
docs/BUILD_PROGRESS.md
_CHANGES.md
```

Thirteen files in the archive. Six carry behaviour, five carry documentation, and `_CHANGES.md` is this note.
Nothing in the archive is a script, a patch, or a fragment — every file is complete and lands where its path
says.
