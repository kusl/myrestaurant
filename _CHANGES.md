# M6 Slice 52 — the transport question that had a third answer, and a count of seven that said six

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-52-menu-images-surfaces.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` IS required — two new files, both in directories that already exist.**

```
git add src/MyRestaurant.WebApplication/Menu/MenuItemImageEndpoints.cs
git add tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageSurfaceContractTests.cs
```

`git status` should show **eleven modifications and two untracked paths**; anything else untracked means
the archive was extracted somewhere other than the repository root.

**No new directory, no migration, no schema change, no new event type, no new package, no `.slnx` edit, no
`.csproj` edit, no `compose.yaml` edit, no `REQUIREMENTS.md` edit, no `OPERATIONS.md` edit, no ADR amended,
no `export.sh` edit, and no §16.3 scenario added or extended.**

---

## Read this first: your last run was clean

`total: 1223, failed: 0, succeeded: 1223, skipped: 0` — against a prediction of **1223**. §18's arithmetic
matched to the digit for the fourth consecutive slice. Seventeen scenarios passed against real browsers
twice, and `ci_local.sh --with-all --with-e2e` reported every gate passing.

The tree was reconstructed from `dump.txt` before anything was authored: **360 file records, 359 matching
their SHA-256 exactly**, the one exception being `LICENSE`, which the dump elides to metadata and hash by
design.

This is **Stage 4b**, plus the defect found while reading the type it consumes.

---

## The stage: a picture can now be attached, replaced, removed and seen

Slice 51 shipped the schema and two data-access services with **no caller outside their integration tests**,
said so in four places, and named the obligation it was re-opening. This discharges it.

### The open question, and why the answer is neither of the two that were on the table

§11.4's pages are static SSR, `[SupplyParameterFromForm]` does not bind a file, and `InputFile` needs an
interactive render mode. The plan named a **minimal API endpoint** and **making a page interactive**.

Neither is taken. A plain `enctype="multipart/form-data"` form posts to **the page itself** under an
ordinary `@formname`, and the handler reads the part out of `HttpContext.Request.Form.Files`.

**Why that costs nothing:** Blazor's static form handling has already read the request body — that is how it
found `_handler` and dispatched to this callback rather than to one of the other six on the page. The
request the model binder declined to bind one field of is sitting right there, cached.

**What the two alternatives would have cost.** An endpoint acquires an **authorization rule of its own**
that then has to agree with `ManageMenuItem`'s `[Authorize]` — two places that can disagree about who may
change a menu, which is what §3.7 exists to prevent. An interactive page puts a circuit under §11.4's
largest form surface to move one file, and makes this the only administration page whose forms behave
differently from every other one's.

**What is given up: model binding for exactly one field**, which is the field the model binder refuses.

### The route

`GET /menu/image/{menu_item_image_identifier}` — a minimal-API endpoint, **anonymous**, 404 for an
identifier the table does not hold, `Cache-Control: public, max-age=31536000, immutable`.

Anonymous because §11.1's guest menu is what it exists for and §4.3 puts registration at the moment of
joining a table, so a guest reading a menu may have no session at all. The immutable header is **true rather
than hopeful**, because ADR-0015 keys the route on the image and a replace mints a new identifier.

**No §3.5 obligations exemption**, unlike the clock and the source offer — those are asked for *by* a page a
locked-down principal is looking at, where this is a subresource of a page such a principal was redirected
away from before it rendered.

### No number was added anywhere, and that was the hard part

The obvious thing to write in the handler is a size check. It is not there: §8.2's named CHECK is the cap
and the write reports a violation of it by constraint name, so a second copy would be F-65's mechanism and,
worse, the belt that hides the buckle.

**What bounds the buffer, then?** Kestrel's request-body limit already bounds every POST this application
accepts, on every form, today. The picture form declares no ceiling because it **inherits** one, and there is
still exactly one number about image size in the tree.

### The surface deliberately does not identify the format, though it could

`ImageFormat.IdentifyContentType` is public. The page could name the format from the bytes and never produce
`UnsupportedContentType` or `ContentTypeContradictedByBytes`.

**That is the reason not to.** The write is the one place that decides what an image is, and a surface that
pre-judged would leave two of that verb's outcomes unreachable from the only form that can produce them —
the same defect this project keeps recording about verbs with no caller, one register in. The browser's
`Content-Type` is handed on unaltered and the refusal message **names what the browser sent**.

---

## The finding: an enum that counted itself and got it wrong (F-102)

`AttachMenuItemImageOutcome`'s summary opened *"**Six answers** rather than a boolean, and every one of them
is a different sentence for the person who chose the file."*

**The enum has seven members.** They begin three lines below that sentence — and the sentence saying six is
the sentence arguing that no answer may be collapsed.

**Deleted rather than corrected**, on F-77's ruling. The row is earned by the shape: this is that ruling's
sixth form, after a version header, a variable four documents agreed about, a port three helpers dialled, a
touch target eight pixels short, a class census in three places, and an index counting one thing while
saying another. **A census in prose is wrong at the moment it is written or at the moment the thing it
counts changes, and nothing in between can tell which.**

Found by reading the type in order to write the surface that renders one sentence per member — **F-93's
timing for the third time**. **No gate is added** (F-41, F-47), and what shrinks the residual here is a side
effect rather than a repair: the handler switches over those outcomes, so a member with no sentence is now a
missing `case`.

---

## Two things worth knowing before you read the diff

**One CSS declaration is load-bearing rather than presentational.** `.manage-picture-image` sets
`max-width: 100%`. Nothing in this stack can resize an image, so that element's intrinsic width is whatever
a camera produced — and without the constraint a 3000px photograph makes the **document** wider than a 375px
viewport, so **§16.3 scenario 16 fails on a page whose every control is correctly placed.**

**No test file was edited to accommodate new CSS.** The three new rules use the `.manage-` prefix, which
`HandheldLayoutContractTests` already protects, and the attach form uses `.manage-inline-form`, which the
375px barrier already reaches — F-93's rule obeyed by putting a new control under a selector that was
already right.

---

## Test count arithmetic

Uncompiled, per §18. **1223 → 1233.**

| Where | Assertions |
| --- | --- |
| `MenuItemImageSurfaceContractTests` (new) | 6 |
| `MenuWiringTests` | 3 |
| `ContentSecurityPolicyContractTests` | 1 |
| **Total added** | **10** |

§16.4's counted-class floor moves 27 → 28. §16.3 stays at seventeen. **Any deviation from 1233 is the first
thing to investigate.**

---

## The two things to check first on a red run

**1. Whether Blazor's static form handling dispatches a `multipart/form-data` post.** This is the
load-bearing assumption of the transport decision. `_handler` is an ordinary form field and
`HttpContext.Request.Form` reads multipart bodies, so the dispatch sees what it always sees — but no request
was made. **The symptom if it is wrong is unmistakable:** the handler never runs, the page re-renders with
no flash and no error, and nothing is written. The fallback is the minimal API endpoint the plan named, at
the cost of a second authorization rule.

**2. Whether `HttpContext.Request.Form.Files[name]` returns null rather than throwing for an absent part.**
The handler treats null as an empty upload and reaches `BytesEmpty`. If the indexer throws, it is an
unhandled exception on a form submitted with nothing chosen.

---

## Files in this archive

| Path | New? |
| --- | --- |
| `src/MyRestaurant.WebApplication/Menu/MenuItemImageEndpoints.cs` | **new** |
| `src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs` | modified |
| `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs` | modified |
| `src/MyRestaurant.WebApplication/Program.cs` | modified |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor` | modified |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | modified |
| `src/MyRestaurant.DataAccess/Menu/MenuItemImages.cs` | modified (F-102) |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageSurfaceContractTests.cs` | **new** |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | modified |
| `tests/MyRestaurant.WebApplication.Tests/Security/ContentSecurityPolicyContractTests.cs` | modified |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | modified |
| `docs/TECHNICAL_SPECIFICATION.md` | modified (v1.37) |
| `docs/MENU_AND_HANDHELD_PLAN.md` | modified |
| `docs/DOCUMENTATION_REVIEW.md` | modified |
| `docs/BUILD_PROGRESS.md` | modified |
| `_CHANGES.md` | modified |

---

## What is next

**Stage 4c — the guest's menu**, which is the half that was actually asked for. A thumbnail beside the name
rather than a hero above it, `loading="lazy"` below the first section, and an `alt_text` column: one `ALTER`
with a `DEFAULT ''` on `0004`'s precedent, because a picture on a guest's card may say something its own
name does not.

**Still deferred by name:** whether a browser downscales before upload (a `<canvas>` round trip in
`wwwroot/js/`, no schema change) — a phone camera produces four megabytes against a 512 KiB cap, so this is
now the thing that decides whether the feature is usable by the person who asked for it.
