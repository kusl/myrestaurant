### M3 Slice 3 — join grant, `/table/{id}` routing, sitting open + membership (landed)

The join flow is now end-to-end (§4.4, §5.1, §9). New `ISittingDirectory`/`DapperSittingDirectory`
(DataAccess/Sittings) answers the two questions the table surface asks — "is this person already a
member of this table's open sitting?" (`GetOpenSittingForMemberAsync`, one round trip, the query
§4.4's "members bypass tokens entirely" rule turns on), "who else is here?" (`ListMembersAsync`,
join-order roster with a username fallback when no display name is set) — plus `GetOpenSittingAsync`
and `ListOpenSittingsForPersonAsync` for the confirmation copy and the `/table` index. Every column
is table-qualified: `table_sitting`, `restaurant_table`, and `table_sitting_member` all carry
same-named identifier columns, and an unqualified reference across the join is exactly how error
42702 bites (the `DapperUserStore` lesson). Rows are read into internal row types with `DateTime`
members and projected to `DateTimeOffset` records, the same Npgsql/Dapper constructor-binding fix
`TableDirectory` and `PersonDirectory` carry.

New `ISittingMembership`/`DapperSittingMembership` is the single write path a consumed grant flows
into. One connection, one transaction, one `IClock.UtcNow` instant, UUIDv7 keys from
`IIdentifierFactory`. §5.1's "atomically" is taken literally: the transaction first takes
`pg_advisory_xact_lock(hashtext('myrestaurant_table_sitting:{table}'))` — keyed per table, not
globally, so two tables never block each other — then re-checks `is_active` and re-reads the open
sitting **under the lock**, so two guests scanning the same display in the same second do not both
find "no open sitting" and race the `table_sitting_one_open_per_table` partial unique index; the
loser joins the winner's sitting. Four outcomes: `SittingOpened` (sitting row + first membership),
`JoinedOpenSitting`, `AlreadyMember` (nothing written — the `UNIQUE (table_sitting_identifier,
person_identifier)` constraint's promise, so a re-scan or a double submit is a no-op), and
`TableUnavailable` (unknown or deactivated table, §4.1). `JoinTableResult.MembershipInserted` is the
exact predicate §9 attaches the broadcast to ("fired on: membership insert"), so an idempotent
re-join announces nothing. Sittings are not in the person-scoped `security_event` vocabulary (§8.2),
so nothing is audited here.

New `JoinGrant`/`JoinGrantProtector`/`JoinGrantCookie` (WebApplication/Tables) is the §4.4 grant,
built exactly like the setup ticket: the payload is the specification's `{table_identifier,
issued_at}` and nothing more, serialized to JSON, Data-Protection-encrypted under its own purpose
(`MyRestaurant.Tables.JoinGrant.v1`, distinct from every other protector so a value from one context
can never be unprotected as another), and carried in a Secure/HttpOnly/SameSite=Lax cookie
(`myrestaurant.join`) whose `MaxAge` is `TABLE_JOIN_GRANT_MINUTES`. The server never trusts that
`MaxAge`: the authoritative expiry is the protected `issued_at`, which a guest cannot edit. The
protector is a singleton (unlike the setup ticket's, which one page constructs ad hoc) because the
table surface reads it on every request in the flow and the display surface will too.

New static-SSR page `/table/{TableId:guid}` (`Components/Pages/Table/TableJoin.razor`), deliberately
**anonymous** — §4.4 requires the grant to be issued *before* the detour through sign-in, and a guest
scanning an unfamiliar table has no account yet. It resolves to one of four states in §4.4's order:
a member sees the table surface with no query string consulted at all (and a table deactivated
mid-sitting does not evict them — §4.1 stops *new* tokens and display rendering, not a sitting in
progress); a signed-in non-member holding a live grant gets the join confirmation; an anonymous
holder of a live grant is redirected to `/sign-in` with this page as the return URL, grant cookie
already written; everything else renders the friendly "that code has expired — scan again" page at
HTTP 200 with one wording for every failure, so an unknown table, a deactivated table, a stale token
and a forged grant are indistinguishable to a prober. A presented token is validated on GET only —
the join POST re-posts to the same URL, query string and all, and re-validating there would
double-count one scan in `table_join_tokens_validated_total`. The join itself is post/redirect/get:
consume the grant (cleared whatever the outcome, so it cannot be replayed), open-or-join in one
transaction, publish `SittingMemberJoined` only when a row was inserted, redirect back.

`/table` (`TableArea.razor`) becomes the index it should be: the open sittings this person is a
member of, oldest first, each linking to its `/table/{id}`. §5.1 allows memberships in several open
sittings at once, so picking one is a real task. It stays interactive-server (it sets no cookie and
issues no redirect) and reads the principal through the cascading `Task<AuthenticationState>`, which
works identically in the prerender pass and on the circuit. New `PersonPrincipal` helper
(WebApplication/Identity) is the one place the person identifier is read off a principal —
`ClaimTypes.NameIdentifier`, the default `ClaimsIdentityOptions.UserIdClaimType` this application
never reconfigures — returning `null` for anonymous, missing, malformed, and all-zero values so every
caller reads a bad claim as "not signed in".

All four services plus the protector are registered by the existing `AddRestaurantTables()`; the
`Program.cs` change is its comment. No migration (`table_sitting` and `table_sitting_member` ship in
0001), no packages, no spec edit — this realizes already-specified behaviour. Tests:
`SittingMembershipTests` (Testcontainers, 9 facts — first join opens the sitting and inserts the
first membership with one instant, a second person joins the same sitting, a repeat join is
idempotent, deactivated and unknown tables write nothing, a new sitting opens after the previous one
closes, the member/non-member split, the closed-sitting predicates, oldest-first listing scoped to
one person, and the roster's display-name fallback), `JoinGrantTests` (8 facts — round trip, missing,
tampered, foreign key ring, foreign purpose, the expiry boundary, the wrong-table refusal, and
cookie/purpose distinctness from the setup flow), `PersonPrincipalTests` (6 facts), and three added
resolvability facts in `TablesWiringTests`.

**Known consequence — obligations outrank scanning.** A staff account with an outstanding
`must_change_password` / `must_enroll_totp` flag that scans a table code is redirected to the
obligation page before `/table/{id}` runs, so no grant is issued on that request (§3.5: nothing else
is reachable). The `ReturnUrl` carries the full URL including the token, so clearing the obligation
lands them back on the scan — and the token still validates if they were quick (worst case ~2× the
rotation, §4.3). This is the pipeline behaving as specified, not a defect; guests, who have no
obligations, never meet it.

Deferred to the next M3 slice: display pairing, device auth, and the `/display/{table}` surface with
its window-aligned QR refresh and party-size chip (§4.2/§11.5) — the chip consumes the
`SittingMemberJoined` broadcast this slice now publishes. Guest self-registration on the join path
(§11.1's "sign-in/registration, passkey-first") is still to come; today an anonymous scanner is sent
to `/sign-in` and needs an existing account.

### Build/test checklist for this slice

- `dotnet build` — green (Razor is the likely site of any compiler catch: `TableJoin.razor` and
  `TableArea.razor` are the two new/changed components).
- `dotnet test` — `SittingMembershipTests` needs a container engine; on rootless Podman,
  `systemctl --user enable --now podman.socket` once. The other three test files never touch one.
- Manual, end to end: `bash scripts/quick_tunnel.sh`, create a table in `/administration/tables`,
  open its `/administration/tables/{id}/join-code`, scan it with a phone that is **not** signed in →
  expect the sign-in page, sign in → expect the join confirmation → Join → expect the table surface
  with your name on the roster. Then hit `/table/{id}` with no query string at all → still the table
  surface (the members-bypass rule). Then sign in as a second person and scan the same code → expect
  the roster to show two people and the sitting count to stay at one.
- Manual, refusals: scan, wait past `TABLE_JOIN_GRANT_MINUTES`, then Join → the expired page, not an
  error. Deactivate the table and scan → the expired page. Edit one character of the
  `myrestaurant.join` cookie → the expired page.
