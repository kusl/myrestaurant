-- =============================================================================
-- 0007_menu_item_image_alt_text.sql
--
-- A stored picture gains the sentence a screen reader reads instead of it, and
-- menu_item_image_event gains the one verb that changes it. Applied at startup
-- by DbUp (ADR-0012). TECHNICAL_SPECIFICATION §7 and §8.2 carry the mechanism;
-- docs/MENU_AND_HANDHELD_PLAN.md Stage 4c carries the staging, and ADR-0015
-- carries the reason the bytes are in this database at all.
--
-- 0001 through 0006 are not edited. DbUp journals by script name, so a change to
-- an applied script is a change that never runs (F-34).
--
-- EVERY COLUMN ADDED HERE CARRIES A DEFAULT OR IS NULLABLE, so no backfill is
-- needed and no existing row, query, form or scenario changes meaning. That is
-- 0004's property and it is the reason this is a script of its own rather than a
-- widening of 0006: a script that touches nothing existing leaves the suite green
-- BY CONSTRUCTION rather than by inspection.
--
-- WHY THE CAPTION IS NOT A COLUMN ON menu_item, WHICH IS THE ALTERNATIVE THIS
-- SCRIPT DECLINED. Alternative text describes a photograph, not a dish: "served
-- on a bed of wilted greens with a lemon wedge" is true of one picture and false
-- of the next one somebody takes. A column on menu_item would survive a picture
-- it no longer describes, and nothing could tell that it had stopped being true.
-- It lives on the picture, and the write carries it FORWARD across a replace
-- rather than resetting it — see §7 and DapperMenuItemImageAdministration, where
-- that carry is the one line of behaviour this migration exists to permit.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. menu_item_image gains the caption
-- -----------------------------------------------------------------------------
--
-- NOT NULL DEFAULT '' rather than nullable, and '' means "none" — the third time
-- this project has made that choice, after menu_section.description (0003) and
-- menu_item.description (0004), and for the same reason each time: an optional
-- payload column cannot be tied to its event type by an equality if clearing it
-- has to write NULL. With '' as the empty value the biconditional in 4 stays
-- total, and a surface tests length rather than for null.
--
-- Every existing row gets '' without the table being rewritten: PostgreSQL 11 and
-- later store a non-volatile ADD COLUMN ... DEFAULT in the catalogue rather than
-- in the heap. On a table whose rows are half a megabyte each that is not a
-- micro-optimisation — it is the difference between a startup migration and a
-- rewrite of every photograph in the restaurant.
--
-- NO LENGTH CEILING, on 0004's ruling rather than as an omission. menu_item.name
-- has none and menu_item.description has none; a ceiling here would be a number
-- invented in a migration and then re-invented in a form and in a write service,
-- which is one fact written three times.
ALTER TABLE menu_item_image
    ADD COLUMN alt_text text NOT NULL DEFAULT '';

-- -----------------------------------------------------------------------------
-- 2. menu_item_image_event gains its payload column
-- -----------------------------------------------------------------------------
--
-- Nullable, because it is carried by exactly one event type and must be absent on
-- every other -- which is what the paired CHECK in 4 asserts. Same shape as
-- new_content_type and new_byte_length beside it; what differs is which type
-- carries it.
ALTER TABLE menu_item_image_event
    ADD COLUMN new_alt_text text NULL;

-- -----------------------------------------------------------------------------
-- 3. The vocabulary is widened BY NAME
-- -----------------------------------------------------------------------------
--
-- Two ordinary statements with nothing to query and nothing to dollar-quote,
-- because 0006 declared every constraint on this table with a name of its own.
-- That is the same return 0005 collected on 0004's DO block, and it is the whole
-- argument for naming a constraint in the script that creates it: 0001 left four
-- CHECKs on menu_item_event unnamed and 0004 had to write a PL/pgSQL loop, a
-- dollar-quoted body, and — because dbup-core substitutes $variables$ before
-- PostgreSQL ever sees the text — the F-78 fix in the runner's configuration.
--
-- 'alt_text_changed' rather than 'described', and the asymmetry with
-- menu_section_event's spelling is deliberate on 0004's ruling: each table's
-- vocabulary is internally consistent, and this one already says 'attached',
-- 'replaced' and 'removed' — past participles of things done to a picture.
-- 'alt_text_changed' names the column it moves, which is what menu_item_event's
-- own 'description_changed' does.
ALTER TABLE menu_item_image_event
    DROP CONSTRAINT menu_item_image_event_type_vocabulary;

ALTER TABLE menu_item_image_event
    ADD CONSTRAINT menu_item_image_event_type_vocabulary
    CHECK (event_type IN ('attached', 'replaced', 'removed', 'alt_text_changed'));

-- -----------------------------------------------------------------------------
-- 4. The third paired biconditional
-- -----------------------------------------------------------------------------
--
-- THE TWO CONSTRAINTS 0006 DECLARED ARE NOT RESTATED, and that is a decision
-- rather than an oversight. 0004 restated menu_item_event's two because its DO
-- block had dropped them; nothing here drops anything but the vocabulary, so both
-- still stand and both are still total: 'alt_text_changed' carries neither a
-- content type nor a byte length, so it is outside the sets on the right-hand
-- side of both equalities and passes each of them with NULL.
--
-- That is worth stating because it is the surprising half. A new event type on a
-- table with two total biconditionals would normally need both widened; this one
-- needs neither, because a caption is not a fact about the file.
ALTER TABLE menu_item_image_event
    ADD CONSTRAINT menu_item_image_event_alt_text_payload
    CHECK ((new_alt_text IS NOT NULL) = (event_type = 'alt_text_changed'));

-- -----------------------------------------------------------------------------
-- 5. No index, and no projection view changes
-- -----------------------------------------------------------------------------
--
-- No index on alt_text: nothing searches it, and the two reads that select it are
-- a lookup by menu_item_identifier -- already the table's UNIQUE -- and a full
-- scan of a table with one row per decorated dish.
--
-- menu_item_image_event's one index is still (menu_item_identifier, occurred_at),
-- which is §11.4's per-item history and is unchanged by a fourth event type.
--
-- And §8.3 does not move, which is the same sentence 0004 ended on: the schema of
-- record grows two columns and the projection views grow none. A caption is read
-- by the guest's menu and by the administrator's item page, both of which read
-- menu_item_image directly.
