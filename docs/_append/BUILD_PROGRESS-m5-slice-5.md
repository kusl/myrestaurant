### M5 Slice 5 — the event explorer: three logs, one question (landed)

M5's build-order line (§19) reads "bills, price adjustment, close & settle, end-of-day, counter fallback
QR, menu management + events, **event explorer**, hide/unhide, post-close corrections". This slice is the
emphasised phrase, and it is the last thing M5 owed. §11.4 spells it out in one clause: "Event explorer
(filter security/order/menu events by subject, actor, type, and time)."

No migration, no packages, no schema change, and nothing deleted. Every table this reads shipped in
`0001_initial_schema.sql`. `Program.cs` gains one `using` and one call — the first startup edit since M4
Slice 1, and the reasoning for it is below.

---

#### What was actually missing

Three append-only logs, three readers, and no way to ask a question of all three at once.

`ISecurityEventLog` writes `security_event` and has never had a read side at all. `IMenuEventLog` reads
`menu_item_event`, scoped to one item or capped to a recent-activity panel. `ISittingRecordReads` reads
`order_event`, scoped to one sitting or one order. Every one of them takes a subject the caller already
names, which is correct for the screens they serve — a person's management page, an item's history, a
sitting's record — and useless for the question §11.4 poses, which is the opposite shape: *what happened,
anywhere, in this window*.

Answering that by calling the three readers and merging in memory would mean fetching every event in the
restaurant in order to render the fifty most recent. The interleaving, the ordering and the cap have to
happen in one statement, so the statement had to exist.

---

#### One statement, sixteen columns, three branches

`DapperEventExplorerReads` is a `UNION ALL` over the three tables inside a subquery, with the filter, the
ordering and the `LIMIT` applied once to the union's output.

```sql
FROM (
    SELECT 'security'::text AS event_stream, … FROM security_event …  WHERE @IncludeSecurityEvents::boolean
    UNION ALL
    SELECT 'order'::text    AS event_stream, … FROM order_event …     WHERE @IncludeOrderEvents::boolean
    UNION ALL
    SELECT 'menu'::text     AS event_stream, … FROM menu_item_event … WHERE @IncludeMenuEvents::boolean
) AS event_row
WHERE (@SubjectPattern IS NULL OR event_row.subject_search ILIKE @SubjectPattern ESCAPE '\')
  AND …
ORDER BY event_row.occurred_at DESC, event_row.event_identifier DESC
LIMIT @MaximumCount;
```

The stream flags sit *inside* the branches so a stream that is switched off is not scanned at all; the
other four bounds sit outside, written once rather than three times, because PostgreSQL pushes qualifiers
down through a `UNION ALL` on its own and three copies of a WHERE clause is how one copy ends up fixed and
the other two wrong. That is the same failure `DapperSittingRecordReads`' shared `const` fragments exist to
prevent, reached from the other side.

**Every column is aliased in every branch**, which `DapperSittingRecordReads`' five-way union deliberately
does not do. PostgreSQL takes the names from the first branch and ignores the rest, so the aliases below
the first are documentation — and with sixteen columns drawn from three unrelated tables, documentation is
exactly what stops a future edit inserting a column into one branch and silently shifting five others.

**Every missing column is cast** (`NULL::uuid`, `NULL::bigint`, `NULL::text`, `NULL::numeric(10,2)`) and
every `citext` is cast to `text` before it meets a `text` from another branch, so no column's type depends
on how the planner resolves `citext` against `text` in a union.

---

#### The security branch's LEFT JOIN is the load-bearing line

`security_event.actor_person_identifier` is the only nullable actor column in the three tables (§8.2:
NULL means the subject acted on themselves, or the system did). Its join to `person` is therefore the only
`LEFT` one in the statement.

An `INNER` there would compile, run, return rows, and silently hide every lockout and every failed sign-in
from the one screen an administrator opens to look for them. It does not throw and it does not look
wrong. `Security_WithNoActor_KeepsTheRowAndReportsNoActor` is the test that notices.

The two search expressions are `concat_ws` rather than `||` for the neighbouring reason: `||` annihilates
on NULL, so a person with no display name would become unfindable by username. For an actorless security
event `concat_ws` yields the empty string, which matches no actor filter — correct, and quieter than a
NULL needing a `COALESCE` at every use.

---

#### Six decisions worth knowing before you read the diff

**The type filter is exact, and that is only sound because the three vocabularies do not overlap.**
`created` is the menu's word; `account_created` is security's, and contains it. A substring match would
answer "when was this item created" with a list of accounts. One flat `event_type = @EventType` across the
union works because no word appears in two streams — a property of the schema rather than a coincidence,
so `Catalogue_TheThreeVocabularies_DoNotOverlap` asserts it and `Catalogue_SecurityEventTypes_AreExactly…`
pins the biggest list to `SecurityEventType.All`.

**Three streams, not four.** §6.8's `order_visibility_event` is deliberately absent. §11.4 names security,
order and menu, and gives visibility its own screen — the hidden-records view, where its log sits beside
the order it is about and next to the Unhide button that is its only counterpart. Folding it in here would
put "somebody tidied their history" in the same list as the meal, without the one control that answers it.

**Nothing is projected, including on the way out.** `event_type`, `actor_role` and the stream word all
travel as strings, and the reader cannot throw on a value it does not recognise. The surface renders a
friendly label for the words §8.2's CHECKs admit and falls back to the raw string — the same rule
`ManageSitting` and `HiddenRecords` already follow, for §11.4's reason: an enum is a projection with a
failure mode, and the one screen whose job is to show what is stored is the last place a word may be
mapped to something it is not.

**The subject is not the same kind of thing in each stream, and the filter reaches all three.** A security
event's subject is a person, searchable by username or display name; an order event's is the order,
searchable by its owner *or by its table label* — which is what an administrator actually remembers; a
menu event's is the item, searchable by name. `SubjectFilter_ReachesPeopleTablesAndItems` covers all four
routes.

**Wildcard escaping is copied from `DapperOrderHistoryReads`, character for character.** `%`, `_` and `\`
are escaped so searching for the username `a_b` does not also find `axb`. Two search boxes on two
administration screens that escaped differently would be a bug nobody could see.

**The page is read-only, and that is a design constraint rather than a stage.** There is no control on
`/administration/events` that changes anything. Every row links to the screen that owns the thing it is
about, and that screen has the buttons. An audit log with edit affordances on it is a different and much
worse object.

---

#### Why `Program.cs` was edited this time

The four existing extensions each resisted being split for a real reason: a host that wired ordering
without the reminder loop would alert and never remind, and one that wired ordering without the menu would
have a staging area that could list nothing. Both are silent half-failures, so both stay welded together —
and that is why M5 Slices 2 and 4 both went out of their way to avoid a startup edit.

The explorer is not like that. It reads identity's audit log, ordering's event log and the menu's, and
belongs to none of them; putting it in `AddRestaurantOrders()` would make the ordering extension the
registrar of a reader of `security_event`. And its failure mode if omitted is not silent — one
administration route throws on resolve, loudly, in front of the person who asked for it. So it gets
`AddRestaurantEventExplorer()`, one scoped registration whose only dependency is the connection factory.
`EventsWiringTests` asserts that last part directly: a registration that grew a clock, an identifier
factory, a broadcaster or a metric would mean the explorer had acquired a write path or a notification,
and it must never have either.

---

#### `EventExplorerQuery`, and why the URL logic left the page

`HiddenRecords.razor` holds the same logic inline — parse two dates, notice a reversed range, rebuild the
path with every bound preserved — and none of it is reachable by a test, because reaching it means
rendering a static-SSR component with an `HttpContext`. It is also the part most likely to be quietly
wrong: every narrowing affordance on the page rebuilds the URL from the current selection, so a bound that
survives parsing but not the rebuild silently changes the administrator's question the moment they click
anything, and nothing looks broken while it happens.

Lifting it into one immutable class in the web layer costs a file and buys twenty-five container-free
facts, including the round trip (`ReparsingItsOwnPath_YieldsTheSameFilter`) and the restaurant-zone date
conversion asserted against two different zones. The same treatment is available to `HiddenRecords` later;
this slice does not touch it, because a refactor of a green page is not this slice's business.

Three parsing rules are worth stating out loud:

- **No stream named means all three.** An unchecked checkbox submits nothing, so "cleared every box and
  pressed the button" and "opened the page fresh" arrive as byte-identical requests. They cannot be told
  apart, so they must mean the same thing, and the only defensible meaning is §11.4's default: everything.
  The page then re-checks all three boxes, which is how it says so.
- **Nothing is ever refused.** An unreadable date, a reversed range, a misspelled stream, a type this
  build does not know — each is ignored or passed through, and each adds a sentence to `Problems` that the
  page prints. A filter returning a wider answer is still a filter; a filter that throws is a blank page in
  front of somebody trying to find out what happened.
- **Dates are the restaurant's** (§8.1), converted through `RestaurantTime.StartOfDay`/`StartOfNextDay`
  into a half-open UTC range. The mirror image of the rendering rule, and the reason the conversion is not
  an `AT TIME ZONE` in the query — §8.1 wants exactly one type performing it.

---

#### Files

**New (7)**

| Path | What |
| --- | --- |
| `src/MyRestaurant.DataAccess/Events/EventExplorerReads.cs` | `EventStream`, `EventTypeCatalogue`, `ExplorerEvent`, `EventExplorerFilter`, `IEventExplorerReads`/`DapperEventExplorerReads` |
| `src/MyRestaurant.WebApplication/Events/EventExplorerQuery.cs` | query string ↔ filter ↔ canonical URL |
| `src/MyRestaurant.WebApplication/Events/EventsServiceCollectionExtensions.cs` | `AddRestaurantEventExplorer()` |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/EventExplorer.razor` | `/administration/events` |
| `tests/MyRestaurant.DataAccess.Tests/Events/EventExplorerReadsTests.cs` | 21 facts (19 Testcontainers, 2 pure) |
| `tests/MyRestaurant.WebApplication.Tests/Events/EventExplorerQueryTests.cs` | 25 facts, no container |
| `tests/MyRestaurant.WebApplication.Tests/Events/EventsWiringTests.cs` | 3 facts, no container |

**Edited (7)**

| Path | What |
| --- | --- |
| `src/MyRestaurant.WebApplication/Program.cs` | one `using`, one `AddRestaurantEventExplorer()` |
| `…/Administration/AdministrationHome.razor` | one `<a>` — Events in the header actions |
| `…/Administration/AdministrationTables.razor` | one `<a>` |
| `…/Administration/AdministrationMenu.razor` | one `<a>` |
| `…/Administration/AdministrationSittings.razor` | one `<a>` |
| `…/Administration/HiddenRecords.razor` | one `<a>` |
| `tests/MyRestaurant.DataAccess.Tests/Orders/OrderTestWorld.cs` | `AddSecurityEventAsync`, `AddMenuItemEventAsync`, two statements |

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §11.4
already specifies, in the words it already uses.

---

#### Where to look if the build breaks

**`EventExplorer.razor`**, as always. Three things in it have no exact precedent in the tree:

1. `[SupplyParameterFromQuery(Name = …)] private string[]? Streams { get; set; }` — the first array-valued
   query parameter in the project. Repeated `?stream=a&stream=b` binds to it; the attribute's `Name` is a
   `const` on `EventExplorerQuery`, so the form field and the parser cannot drift.
2. `<fieldset>`/`<legend>` and `<optgroup>` — ordinary HTML, but the first of each here.
3. `class="event-stream-badge event-stream-@entry.Stream"` — an implicit expression finishing an attribute
   value. Common Razor, first use in this tree.

The one thing deliberately avoided: no locals are declared directly inside a markup `@foreach`, matching
`HiddenRecords`. Pattern variables inside an `@if` within a `@foreach` *are* used, which that file already
does.

Then `EventExplorerReads.cs`, and specifically the union's column list. Two things I could not check
without a compiler, both deliberate:

1. `WHERE @IncludeSecurityEvents::boolean` as an entire WHERE clause — a bare boolean parameter, cast for
   the same belt-and-braces reason `DapperSecurityEventLog` casts `@ActorPersonIdentifier::uuid`.
2. `concat_ws(' ', subject.username::text, subject.display_name)` — first use of `concat_ws` in the tree.
   It returns `text`, skips NULLs, and yields `''` (not NULL) when every argument is NULL, which is the
   behaviour the actorless-security-event case depends on.

`ESCAPE '\'` inside a C# raw interpolated string is one literal backslash on both sides — raw strings do
not process escapes, and `standard_conforming_strings` makes the SQL string a single backslash. Same as
`DapperOrderHistoryReads`.

---

#### Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — the Razor page is the likely compiler-catch home, as always.
3. `dotnet test` — expect **+49 facts** (21 + 25 + 3). Taking M5 Slice 4's projected 877 as the baseline:
   926 total, 911 passing, 15 still skipped with a container engine present. Without one, the 19
   container-dependent facts here skip as well.
4. `bash run.sh --smoke` — should be unaffected, but `Program.cs` changed, so this one is not a formality
   this time.
5. `bash run.sh --containers-only`, then as an administrator:
   - `/administration/events` — three streams interleaved, newest first.
   - Untick Orders and Menu — only security events; the URL says `?stream=security`.
   - Type a username into Subject; then the same word into Actor — different answers.
   - Pick a type from each of the three optgroups.
   - Set a date range, then reverse it — the range is dropped and the page says why.
   - Click a stream badge, then "only this subject" — the other bounds survive.
   - Follow a security row to its person, an order row to its sitting, a menu row to its item.

---

#### What is left in M5

**Nothing.** §19's M5 line is closed: bills, price adjustment, close & settle, end-of-day, counter fallback
QR, menu management + events, event explorer, hide/unhide, post-close corrections.

M6 is hardening: the Playwright matrix (fifteen skips today), the backup/restore drill, and CI.
