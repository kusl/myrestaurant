# Slice 68 — Stage 6c: guest comments, the schema and the data access

Extract at the repository root. Spec goes to **v1.53**. Findings floor **F-122**.

## Files in this archive

| Path | State |
|---|---|
| `src/MyRestaurant.DataAccess/Migrations/0009_menu_item_comments.sql` | **new — needs `git add`** |
| `src/MyRestaurant.DataAccess/Menu/MenuItemComments.cs` | **new — needs `git add`** |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemCommentTests.cs` | **new — needs `git add`** |
| `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs` | changed |
| `tests/MyRestaurant.DataAccess.Tests/SchemaMigrationRunnerTests.cs` | changed |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | changed |
| `docs/TECHNICAL_SPECIFICATION.md` | changed |
| `docs/DOCUMENTATION_REVIEW.md` | changed |
| `docs/MENU_AND_HANDHELD_PLAN.md` | changed |
| `docs/BUILD_PROGRESS.md` | changed |

**Nothing is deleted in this slice.**

The migration is picked up by the existing `<EmbeddedResource Include="Migrations/*.sql" />` glob, so `MyRestaurant.DataAccess.csproj` is unchanged.

## Expected test count

**1320.** 1304 + 13 (new `MenuItemCommentTests`) + 1 (`MenuWiringTests`) + 2 (two rows added to `SchemaMigrationRunnerTests.KeyRelations`, a `[MemberData]` theory, so the file's attribute count does not move and §16.4's seven for it stays).

Any other number is worth investigating before reading the assertion text. The likeliest cause is an analyzer diagnostic promoted to an error by `ContinuousIntegrationBuild=true` rather than a failed assertion.

## Gates emulated against the edited tree

| Gate | Result |
|---|---|
| §16.4 counted-class census | 46 counted, floor 37, no disagreement, no ambiguity, no unresolved citation |
| `SpecificationVersionTests` | header 1.53 = newest entry 1.53, history descends |
| `MenuEventVocabularyContractTests` | four named vocabularies, spec set = migration set, values equal both directions |
| `MarkdownTableContractTests` | no run of table lines is malformed in any of the four edited documents |
| Byte hygiene | LF only, one final newline, no trailing whitespace, on all ten files |
| No comment in authored source | the migration and both C# files carry none |

## Veto points

**1. One standing comment per person per dish.** The fold partitions on `(menu_item_identifier, person_identifier)`, so resubmitting replaces. To allow several comments per dish instead, add a `menu_item_comment_identifier uuid NOT NULL` column to `0009`, partition the view on it, and take the withdrawal verb an identifier rather than a pair. Reversing this later is a new migration, not an edit to `0009`.

**2. The cap is 1000 characters.** It is stated once, in `menu_item_comment_event_body_within_cap`. Changing it is a one-line `ALTER` in a new migration; nothing in C#, in the tests, or in the documents repeats the number.

**3. Staff-facing only.** `IMenuItemCommentDirectory.ListAsync` has no visibility argument, because §7 rules that no guest reads another guest's words. Showing one guest another's comment reopens the moderation question — the plan's Stage 6 table names that row as the one to reopen first.

**4. The body is trimmed before storage.** If verbatim storage is wanted (as for the picture's caption), drop the `.Trim()` in `SubmitAsync` and the `NoChange` comparison stops being reliable across whitespace-only edits; `menu_item_comment_event_body_not_blank` still refuses an all-whitespace body.

## What was not verified

Nothing was compiled and no test was run: no .NET SDK and no container engine in the authoring environment. `MenuItemCommentTests` has never executed against a real PostgreSQL, so the four constraint names, the `pg_get_constraintdef` cap read, and the `COALESCE(NULLIF(btrim(display_name), ''), username)` projection over a `citext` username rest on reading the DDL and on the identical expression already working in `DapperMenuSectionEventLog`. The migration has not been applied. No browser ran, and no §16.3 scenario touches comments, because no surface exists yet.
