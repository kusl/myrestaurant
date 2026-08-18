# ADR-0015 — A menu item's picture lives in the database

**Status:** Accepted (2026-08-18), implemented in Stage 4a of `docs/MENU_AND_HANDHELD_PLAN.md`. **The schema
and the data access landed in M6 Slice 51** (`0006`: `menu_item_image`, `menu_item_image_event`,
`IMenuItemImageDirectory`, `IMenuItemImageAdministration`, `MyRestaurant.Domain.Menu.ImageFormat`). **What is
outstanding is every surface** — the route that serves the bytes, the form that attaches them, and the
thumbnail §11.1 renders — which is Stage 4b.
**Trail:** *"in the future we might even have images"*, from the same conversation that produced ADR-0014
**Requirements:** `REQUIREMENTS.md` §6.8 (menu), §8 (self-hosted, honesty in UI)
**Specification:** `TECHNICAL_SPECIFICATION.md` §7, §8.2, §11.1, §11.11, §15
**Related:** ADR-0002 (relational append-only event logs), ADR-0011 (application-generated UUIDv7),
ADR-0012 (DbUp migrations at startup), ADR-0014 (menu sections and item descriptions)

## Context

A picture is the one thing a guest deciding between two unfamiliar dishes wants that this menu could not
carry. It was named in the original enhancement conversation as a *later* want, and it is being decided now
for a reason that has nothing to do with priority: **where bytes live is much cheaper to decide before there
are any than after.**

Three arrangements were available.

**A volume of files on disk, referenced from the row.** The conventional answer, and the one this project
cannot afford. §15 *defines* a recovery set as exactly two artefacts — the database dump and the Data
Protection key ring — and `scripts/restore_drill.sh` rehearses both on every push. A third artefact means
editing that definition, `backup.sh`, `restore.sh`, the drill and the runbook; and the failure mode is the
one **F-38** is a whole ledger row about: an operator who takes one more backup the old way afterwards holds
a set that restores, cleanly and quietly, an application whose menu has no pictures in it. The recovery path
that has never been executed is a hypothesis, and a recovery path that *has* been executed against the wrong
definition is worse.

**Object storage.** MinIO is a service to run, S3 is a paid dependency on somebody else's network, and both
contradict R§1's premise: this is software one restaurant self-hosts.

**`bytea` in PostgreSQL.** The cost is honest and small at this scale. Sixty items at 200 KB is twelve
megabytes, inside a `pg_dump -Fc` that already compresses, on a database whose entire reason for existing is
one restaurant's evening service. PostgreSQL stores a value this size out of line and compressed, so the
metadata reads never pay for the bytes they describe.

**The direction of reversibility settles it.** `bytea` → volume is a migration that reads rows and writes
files. Volume → `bytea` is a migration that cannot find the files, because by then some of them have been
moved, some are on a host that was rebuilt, and nothing in the tree ever knew which. Choosing the reversible
direction first is the whole reason to choose before there is data.

## Decision

**1. The bytes are a `bytea` column on a table of their own, one row per item.**

Not a column on `menu_item`: a picture is replaced far more often than a dish is renamed, and a `bytea` on
the row every read in §11.1 and §11.4 selects would put the images into every menu query in the application.
One row per item is expressed as `UNIQUE` on the referencing column rather than as a column on `menu_item`,
which also makes a gallery a later migration that drops the `UNIQUE` and adds a position, rather than a
redesign.

**2. A replace mints a new identifier and deletes the old row.**

This is the ruling with the most consequences and the least obvious motivation. §7's route is
`GET /menu/image/{menu_item_image_identifier}`, not `…/{menu_item_identifier}`, so that the URL changes when
the picture changes and `Cache-Control: public, max-age=31536000, immutable` is a **true statement**. Keying
the route on the item would make every image on the menu need an `ETag` and a revalidation round trip per
page load, on phones, to avoid serving last week's photograph — which is a per-request cost paid forever to
avoid a per-upload cost paid rarely.

**3. Removal deletes the row. This is a stated exception to §6.8's hide-never-delete rule.**

That rule exists so that history is never orphaned. Here the history is not in the row: the append-only
`menu_item_image_event` records that a picture was attached, what format and size it was, which identifier it
had, who removed it and when — the whole of what a reader of §11.4 could want. What deletion discards is the
bytes, and keeping those would grow §15's recovery set by up to half a megabyte for every photograph anybody
ever retook, for no reader at all.

**4. Nothing decodes, resizes or re-encodes an upload.**

There is no free-libre .NET imaging library available to this stack for this use: ImageSharp's licence does
not admit it and SkiaSharp is a native dependency inside a rootless container. So the server validates and
stores what it is given, and **the size cap is the whole of the defence**. Two consequences are accepted
rather than hidden: a phone camera produces four megabytes against a cap of 512 KiB, so without a
browser-side downscale the answer to most uploads is *too large* — which is Stage 4b's open question and is
recorded there as one; and this project stores no image dimensions, because a number nothing in the stack can
measure would be the client's unverifiable claim recorded in the indicative (**F-101**).

**5. The declared media type is checked against the bytes' own signature.**

The `Content-Type` on an upload is the client's claim about its own file, and §7's route hands the stored
column straight back out as a response header on this application's origin. So
`MyRestaurant.Domain.Menu.ImageFormat` reads the first bytes — PNG's eight-byte signature, JPEG's
start-of-image marker, and **both halves** of WebP's RIFF header, since `RIFF` alone is also an AVI and a WAV
— and a declaration the bytes contradict is refused. It is in `Domain` because it is a pure function and
because the cases worth asserting are the malformed ones, which behind an `INSERT` would each cost a
container (**F-100**).

**6. The size cap is written once, in the DDL, and the code reports it by constraint name.**

`menu_item_image_bytes_within_cap` is a named CHECK; `DapperMenuItemImageAdministration` catches the check
violation, compares the constraint's name, and answers `BytesOverCap`. No number appears in C#. A second copy
would be **F-65**'s mechanism, and worse it would be the belt that hides the buckle — F-64, F-69 and F-75 are
each an instance of a redundant check making the first one's absence invisible.

**7. §11.11's Content Security Policy needs no change, and that must be asserted rather than assumed.**

§11.11 sets `default-src 'self'` and declares no `img-src`, so `'self'` already covers bytes this application
serves from its own origin. The policy is the one configuration in this project that becomes wrong by editing
a file it does not mention (**F-49**), so "no change needed" is exactly the kind of claim that has to be made
executable: `ContentSecurityPolicyContractTests` gains the fact in Stage 4b, with the route, rather than
being left true by accident.

## Consequences

**Stage 4 splits at the same seam Stage 2 did, and for the same reason.** `0006` adds two tables and touches
nothing that already exists, so every existing read, write, integration fact and end-to-end scenario means
exactly what it meant before — green by construction rather than by inspection. `OrderTestWorld.TruncateAsync`
needed no edit at all, because `TRUNCATE … CASCADE` on `menu_item` reaches both new tables.

**It re-opens an obligation, deliberately and by name.** Slice 43 closed the obligation that no verb behind
`IMenuWorkflow` lacks a surface. Nothing behind `IMenuWorkflow` is added here — but two data-access services
now exist with no caller outside their integration tests, which is the state `IMenuSectionAdministration` was
in from `0003` until the section editor. It is the weaker form of that defect and is recorded on every slice
until Stage 4b discharges it.

**No `OPERATIONS.md` edit, and that is the decision paying out immediately.** The runbook, both scripts and
the drill are untouched because the recovery set is still two artefacts. Had this ADR chosen a volume, the
same slice would have had to edit five files whose subject is getting a restaurant's data back.

**A gallery, an alt text and a downscale are all later and all cheap from here.** Dropping the `UNIQUE` gives
several pictures per item; an `alt_text` column is one `ALTER` with a `DEFAULT ''`, on the exact precedent
`0004` set for `description`; a browser-side `<canvas>` downscale is `wwwroot/js/` and changes no schema.

## History

- **2026-08-18** — accepted; Stage 4a implemented in M6 Slice 51. Decisions 1 through 6 are embodied in
  `0006`, `MenuItemImages.cs` and `ImageFormat.cs`. Decision 7 is carried to Stage 4b with the route it is
  about, which is the earliest slice in which it can be asserted.
