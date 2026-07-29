# M5 Slice 5 — the event explorer: three logs, one question

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 15 files as
modified/added, and **no deletions**.

```bash
tar -xzf m5-slice5-event-explorer.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed and nothing is superseded. No migration ships, no package changes, no schema
change.

## What this closes

§19's M5 line reads "bills, price adjustment, close & settle, end-of-day, counter fallback QR, menu
management + events, **event explorer**, hide/unhide, post-close corrections". This is the emphasised
phrase, and it was the last thing M5 owed. §11.4 states it in one clause: "Event explorer (filter
security/order/menu events by subject, actor, type, and time)."

**With this, M5 is closed.** M6 is hardening: the Playwright matrix, the backup/restore drill, and CI.

## What was actually missing

Three append-only logs, three readers, and no way to ask a question of all three at once.

`ISecurityEventLog` writes `security_event` and has never had a read side at all. `IMenuEventLog` reads
`menu_item_event`, scoped to one item or capped to a recent-activity panel. `ISittingRecordReads` reads
`order_event`, scoped to one sitting or one order. Each takes a subject the caller already names — correct
for the screens they serve, useless for §11.4's question, which is the opposite shape: *what happened,
anywhere, in this window*. Answering it by calling all three and merging in memory would mean fetching
every event in the restaurant to render the fifty most recent, so the interleaving, the ordering and the
cap have to happen in one statement.

## New files (7)

### Code — DataAccess (1)

- `src/MyRestaurant.DataAccess/Events/EventExplorerReads.cs`
  `EventStream` (three query-local discriminators), `EventTypeCatalogue` (the 29 words the filter's
  dropdown offers, grouped), `ExplorerEvent`, `EventExplorerFilter`, and
  `IEventExplorerReads`/`DapperEventExplorerReads` — one `UNION ALL` over the three tables, filtered,
  ordered and capped once.

### Code — WebApplication (2)

- `src/MyRestaurant.WebApplication/Events/EventExplorerQuery.cs`
  Query string ↔ filter ↔ canonical URL, immutable, with the date parsing and the reversed-range and
  unknown-word reporting. Container-free and fully tested — the thing `HiddenRecords`' identical inline
  logic is not.
- `src/MyRestaurant.WebApplication/Events/EventsServiceCollectionExtensions.cs`
  `AddRestaurantEventExplorer()`. One scoped registration.

### Surface (1)

- `src/MyRestaurant.WebApplication/Components/Pages/Administration/EventExplorer.razor` —
  `/administration/events`

### Tests (3)

- `tests/MyRestaurant.DataAccess.Tests/Events/EventExplorerReadsTests.cs` (21 facts — 19 Testcontainers,
  2 pure)
- `tests/MyRestaurant.WebApplication.Tests/Events/EventExplorerQueryTests.cs` (25 facts, no container)
- `tests/MyRestaurant.WebApplication.Tests/Events/EventsWiringTests.cs` (3 facts, no container)

### Docs (1, append-then-keep)

`docs/BUILD_PROGRESS.md` is large and is not regenerated. The new section ships as
`docs/_append/BUILD_PROGRESS-m5-slice-5.md`, matching the sections already in that folder.

## Edited (8)

- `src/MyRestaurant.WebApplication/Program.cs`
  One `using` and one `AddRestaurantEventExplorer()`. **The first startup edit since M4 Slice 1** — see
  "Why Program.cs was edited this time" below.
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationHome.razor`
- `…/AdministrationTables.razor`
- `…/AdministrationMenu.razor`
- `…/AdministrationSittings.razor`
- `…/HiddenRecords.razor`
  One `<a>` each: an **Events** link in the header actions. Nothing else changes in any of the five.
- `tests/MyRestaurant.DataAccess.Tests/Orders/OrderTestWorld.cs`
  `AddSecurityEventAsync` and `AddMenuItemEventAsync`, plus their two statements. Same shape as the
  `AddVisibilityEventAsync` the last slice added, and for the same reason.
- `_CHANGES.md` (this file)

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §11.4
already specifies, in the words it already uses.

## Seven decisions worth knowing before you read the diff

**The security branch's `LEFT JOIN` is the load-bearing line.**
`security_event.actor_person_identifier` is the only nullable actor column in the three tables (§8.2: NULL
means the subject acted on themselves, or the system did). An `INNER` join there would compile, run,
return rows, and silently hide every lockout and every failed sign-in from the one screen an administrator
opens to look for them. It does not throw and it does not look wrong.
`Security_WithNoActor_KeepsTheRowAndReportsNoActor` is the test that notices.

**The type filter is exact, and that is only sound because the three vocabularies do not overlap.**
`created` is the menu's word; `account_created` is security's, and contains it. A substring match would
answer "when was this item created" with a list of accounts. One flat `event_type = @EventType` across the
union works because no word appears in two streams — a property of the schema rather than a coincidence,
so two facts assert it directly, including pinning the security list to `SecurityEventType.All`.

**Three streams, not four.** §6.8's `order_visibility_event` is deliberately absent. §11.4 names security,
order and menu, and gives visibility its own screen — the hidden-records view, where its log sits beside
the order it is about and next to the Unhide button that is its only counterpart.

**Every column is aliased in every branch**, which `DapperSittingRecordReads`' five-way union deliberately
does not do. PostgreSQL takes the names from the first branch and ignores the rest, so the aliases below
the first are documentation — and with sixteen columns from three unrelated tables, documentation is
exactly what stops a future edit inserting a column into one branch and shifting five others.

**The stream flags sit inside the branches; the other four bounds sit outside.** A switched-off stream is
not scanned at all, and the rest of the WHERE clause exists once rather than three times — PostgreSQL
pushes those qualifiers down through a `UNION ALL` on its own, and three copies is how one copy ends up
fixed and the other two wrong.

**The page is read-only, and that is a constraint rather than a stage.** Nothing on
`/administration/events` changes anything. Every row links to the screen that owns the thing it is about,
and that screen has the buttons. An audit log with edit affordances on it is a different and much worse
object.

**Nothing is ever refused.** An unreadable date, a reversed range, a misspelled stream name, a type this
build does not know: each is ignored or passed straight through, and each adds a plain sentence the page
prints. A filter returning a wider answer is still a filter; a filter that throws is a blank page in front
of somebody trying to find out what happened.

## Why `Program.cs` was edited this time

The four existing extensions each resisted being split for a real reason: a host that wired ordering
without the reminder loop would alert and never remind; one that wired ordering without the menu would
have a staging area that could list nothing. Both are silent half-failures, which is why M5 Slices 2 and 4
both went out of their way to avoid a startup edit.

The explorer is not like that. It reads identity's audit log, ordering's event log and the menu's, and
belongs to none of them — putting it in `AddRestaurantOrders()` would make the ordering extension the
registrar of a reader of `security_event`. And its failure mode if omitted is not silent: one
administration route throws on resolve, loudly, in front of the person who asked for it.
`EventsWiringTests` asserts that its only dependency is the connection factory, because a registration
that grew a clock, an identifier factory, a broadcaster or a metric would mean the explorer had acquired a
write path or a notification, and it must never have either.

## Where to look if the build breaks

**`EventExplorer.razor`**, as always. Three things in it have no exact precedent in the tree:

1. `[SupplyParameterFromQuery(Name = …)] private string[]? Streams { get; set; }` — the first array-valued
   query parameter in the project. Repeated `?stream=a&stream=b` binds to it; the attribute's `Name` is a
   `const` on `EventExplorerQuery`, so the form field and the parser cannot drift.
2. `<fieldset>`/`<legend>` and `<optgroup>` — ordinary HTML, but the first of each here.
3. `class="event-stream-badge event-stream-@entry.Stream"` — an implicit expression finishing an attribute
   value. Common Razor, first use in this tree.

Deliberately avoided: no locals are declared directly inside a markup `@foreach`, matching `HiddenRecords`;
and every `<text>` block sits inside an `@if` body (code context), never in markup context — the one
separator that needed to live in markup is a `<span class="event-separator">`, mirroring
`HiddenRecords`' `.hidden-record-separator`.

Then `EventExplorerReads.cs`. Two things I could not check without a compiler, both deliberate:

1. `WHERE @IncludeSecurityEvents::boolean` as an entire WHERE clause — a bare boolean parameter, cast for
   the same belt-and-braces reason `DapperSecurityEventLog` casts `@ActorPersonIdentifier::uuid`.
2. `concat_ws(' ', subject.username::text, subject.display_name)` — first use of `concat_ws` in the tree.
   It returns `text`, skips NULLs, and yields `''` (not NULL) when every argument is NULL, which is the
   behaviour the actorless-security-event case depends on. `||` would have been wrong twice over: it
   annihilates on NULL, so a person with no display name would be unfindable by username.

`ESCAPE '\'` inside a C# raw interpolated string is one literal backslash on both sides — raw strings do
not process escapes, and `standard_conforming_strings` makes the SQL string a single backslash. Same as
`DapperOrderHistoryReads`, whose escaping this copies character for character so two search boxes on two
administration screens cannot behave differently.

## Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — the Razor page is the likely compiler-catch home, as always.
3. `dotnet test` — expect **+49 facts** (21 + 25 + 3). Taking the last slice's projected 877 as the
   baseline: 926 total, 911 passing, 15 still skipped with a container engine present. Without one, the
   19 container-dependent facts here skip as well.
4. `bash run.sh --smoke` — `Program.cs` changed, so this one is not a formality this time.
5. `bash run.sh --containers-only`, then as an administrator:
   - `/administration/events` — three streams interleaved, newest first.
   - Untick Orders and Menu — only security events; the URL says `?stream=security`.
   - Type a username into Subject; then the same word into Actor — different answers.
   - Pick a type from each of the three optgroups.
   - Set a date range, then reverse it — the range is dropped and the page says why.
   - Click a stream badge, then "only this subject" — the other bounds survive.
   - Follow a security row to its person, an order row to its sitting, a menu row to its item.

## Housekeeping carried over

`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2". Six appends are now unmerged in
`docs/_append/`, including this slice's:

```bash
cat docs/_append/BUILD_PROGRESS-m4-slice-2.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-1.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-5.md >> docs/BUILD_PROGRESS.md
```

## The one-line why

Every screen in this system answers a question about one account, one table, one sitting or one item —
and the question somebody actually asks at nine in the evening is "what just happened", which until now
nothing could answer.
