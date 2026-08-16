-- =============================================================================
-- 0005_menu_item_sections.sql
--
-- The last of TECHNICAL_SPECIFICATION §7's menu enhancement: every menu item is
-- filed under exactly one heading. Applied at startup by DbUp (ADR-0012).
-- ADR-0014 carries the rationale; docs/MENU_AND_HANDHELD_PLAN.md Stage 2 carries
-- the staging, and this script closes it.
--
-- THIS IS THE EXPENSIVE ONE, AND IT WAS ALWAYS GOING TO BE
--
-- 0003 and 0004 were each cut so that they touched nothing existing, and both
-- said in their own header that the coupling between a NOT NULL reference and
-- the surfaces it breaks would be paid here on its own. It is:
-- menu_section_identifier is NOT NULL from the moment it exists (§7's ruling --
-- an item under no heading is an item nobody decided about), so
-- CreateMenuItem.razor cannot write a row without naming a section, and
-- AdministrationJourneys.CreateMenuItemAsync drives that real form in six of the
-- sixteen §16.3 scenarios. The surfaces ship in the same slice as this script.
-- There is no version of this migration that is green on its own.
--
-- THE REJECTED ALTERNATIVE IS STILL REJECTED. Making the column nullable here
-- and tightening it later would put an "Uncategorized" state into the schema for
-- exactly one slice, and every reading surface written during that slice would
-- acquire a code path for it that then has to be removed. The column goes
-- straight from non-existent to NOT NULL.
--
-- NO DOLLAR-QUOTED BLOCK, AND THAT IS DELIBERATE (F-78)
--
-- 0004 needed a DO block because it had to drop CHECK constraints whose names
-- PostgreSQL generated, so it had to query pg_constraint for them. That block
-- collided with dbup-core's variable substitution, which reads $name$ the same
-- way PostgreSQL reads a dollar-quoted body, and took the whole suite red.
-- This script needs no such block: 0004 replaced every generated name with a
-- chosen one, so the single constraint that has to move here is dropped BY NAME.
-- That was the stated reason 0004 did the renaming, and this is the script that
-- collects on it. SchemaMigrationRunner.WithVariablesDisabled() remains the
-- repair and 0004's tagged body remains the gate on it; nothing here relies on
-- either, which is the point.
--
-- 0001, 0002, 0003 and 0004 are not edited. DbUp journals by script name, so a
-- change to an applied script is a change that never runs (F-34).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. A section to hold what is already on the menu, and only if there is any
-- -----------------------------------------------------------------------------
--
-- A fresh database has no menu items, so it gets no section and the
-- administrator names their own -- which is §7's intent: "Drinks", "Entrees" and
-- "Breakfast" are decisions a restaurant makes, and a migration that invented
-- them would be this script having an opinion about somebody's menu.
--
-- An existing database has items that are about to acquire a NOT NULL reference,
-- so it gets exactly one section to hold them. The name is deliberately flat:
-- "Menu" is obviously a placeholder an administrator will rename, where
-- "Starters" would be a guess that reads as a decision somebody made.
--
-- THE IDENTIFIER IS A LITERAL RATHER THAN gen_random_uuid(). ADR-0011 puts
-- identifier generation in the application, and a migration is the one place
-- with no application to ask. A literal is at least auditable and is identical
-- on every host that ever runs this script, so two restored copies of one
-- database agree about which row this is. It is a real UUIDv7: 01987000-0000 is
-- the 48-bit millisecond timestamp, the version nibble is 7 and the variant
-- nibble is 8.
--
-- WRITTEN AS INSERT ... SELECT ... WHERE rather than as a conditional block.
-- Two guards, and both are load-bearing. EXISTS (menu_item) is the "only if
-- there is something to hold" rule. NOT EXISTS (menu_section) is the one that
-- makes this script safe on a database where somebody already created sections
-- through IMenuSectionAdministration -- which 0003 shipped and no surface has
-- called, but "no surface calls it" is not the same claim as "no row exists".
-- Without that guard this INSERT would trip menu_section.name's UNIQUE
-- constraint on any database that happened to hold a section called "Menu", and
-- a migration that fails at startup takes the whole application down.
INSERT INTO menu_section (
    menu_section_identifier, name, description, display_order, is_active, created_at)
SELECT
    '01987000-0000-7005-8000-000000000005'::uuid,
    'Menu',
    'Everything that was on the menu before it had headings. Rename this section, or make others and move items into them.',
    0,
    true,
    now()
WHERE EXISTS (SELECT 1 FROM menu_item)
  AND NOT EXISTS (SELECT 1 FROM menu_section);

-- -----------------------------------------------------------------------------
-- 2. The column, nullable for exactly three statements
-- -----------------------------------------------------------------------------
--
-- Steps 2-4 are the standard safe sequence for adding a NOT NULL foreign key to
-- a populated table, and the order matters: SET NOT NULL before the backfill
-- fails on every existing row. The window in which the column is nullable is
-- three statements inside one DbUp transaction, so no application ever observes
-- it -- which is the difference between this and the "nullable now, tighten
-- later" alternative §7 refuses.
ALTER TABLE menu_item
    ADD COLUMN menu_section_identifier uuid NULL;

-- -----------------------------------------------------------------------------
-- 3. Backfill
-- -----------------------------------------------------------------------------
--
-- To the FIRST section in display order rather than to the seed's literal
-- identifier, and that is the more careful of the two spellings. If step 1 ran,
-- the first section IS the seed and the two are the same thing. If step 1 was
-- skipped because sections already existed, this puts the orphans under the
-- earliest of them instead of failing -- and the tie-break is the same
-- (display_order, name, identifier) every read of this table uses, so it is the
-- section an administrator would call first.
--
-- The scalar subquery is evaluated once. On a database with no menu items the
-- UPDATE matches nothing and the subquery's NULL never reaches a row.
UPDATE menu_item
SET menu_section_identifier = (
        SELECT menu_section.menu_section_identifier
        FROM menu_section
        ORDER BY menu_section.display_order,
                 menu_section.name,
                 menu_section.menu_section_identifier
        LIMIT 1)
WHERE menu_item.menu_section_identifier IS NULL;

-- -----------------------------------------------------------------------------
-- 4. The constraint and the reference
-- -----------------------------------------------------------------------------

ALTER TABLE menu_item
    ALTER COLUMN menu_section_identifier SET NOT NULL;

-- Named rather than left to PostgreSQL, on the lesson 0004 paid for: a
-- generated name is deterministic, undocumented, and not a thing for a later
-- migration to depend on.
--
-- No ON DELETE clause, which means NO ACTION. §6.8 and ADR-0002 make this
-- system's whole answer to "get rid of it" a hiding flag rather than a DELETE,
-- and menu_section has an is_active for exactly that -- so a section with items
-- under it cannot be deleted, and the error a hypothetical DELETE would raise is
-- the correct outcome rather than a cascade that silently removed a menu.
ALTER TABLE menu_item
    ADD CONSTRAINT menu_item_menu_section_reference
    FOREIGN KEY (menu_section_identifier)
    REFERENCES menu_section (menu_section_identifier);

-- -----------------------------------------------------------------------------
-- 5. menu_item_event gains 'section_changed' and its payload column
-- -----------------------------------------------------------------------------
--
-- Dropped BY NAME. This is the whole return on 0004's DO block: the vocabulary
-- CHECK is menu_item_event_type_vocabulary because 0004 named it, so widening it
-- is two ordinary statements with nothing to query and nothing to dollar-quote.
ALTER TABLE menu_item_event
    DROP CONSTRAINT menu_item_event_type_vocabulary;

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_type_vocabulary
    CHECK (event_type IN
        ('created', 'name_changed', 'price_changed', 'description_changed',
         'section_changed', 'reordered', 'activated', 'deactivated'));

-- The payload column is a real foreign key rather than a bare uuid. An event log
-- is the record §11.4 renders to a person, and a section identifier that names
-- nothing renders as a blank where a heading should be. The reference costs one
-- index lookup per event write, on a table written a handful of times a service.
ALTER TABLE menu_item_event
    ADD COLUMN new_menu_section_identifier uuid NULL
    REFERENCES menu_section (menu_section_identifier);

-- The fifth paired CHECK, on the same terms as the other four.
--
-- 'created' DELIBERATELY DOES NOT CARRY THE SECTION, although the menu_item row
-- is inserted with one -- exactly as it does not carry the description. Widening
-- it would break the equality against every 'created' row already written, which
-- is the reason 0004 gave and it has not changed. An item created under a
-- heading writes 'created', then 'section_changed', and then
-- 'description_changed' if it has a description, in one transaction. The log
-- reads "Created as “Soup” at 4.50 / Filed under Starters / Description set",
-- which is three lines where one would do and is honest about it.
ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_section_payload
    CHECK ((new_menu_section_identifier IS NOT NULL) = (event_type = 'section_changed'));

-- -----------------------------------------------------------------------------
-- 6. One index, and it is not the one 0004 declined
-- -----------------------------------------------------------------------------
--
-- 0004 added no index on menu_item's ordering columns and that still holds: a
-- sequential scan over the cardinality of a restaurant menu beats reading an
-- index, and §11.1 and §11.4 both read the whole table anyway.
--
-- This index is a different thing. menu_item.menu_section_identifier is now a
-- foreign key, and PostgreSQL does NOT index the referencing side of one
-- automatically -- so every statement that touches a menu_section row has to
-- scan menu_item to check the constraint. That is cheap today and it is the kind
-- of cheap that stops being cheap silently. The columns after it are the tail of
-- §11.1's ORDER BY, so the same index answers the guest menu's grouping read.
CREATE INDEX menu_item_section_index
    ON menu_item (menu_section_identifier, display_order, name);

-- -----------------------------------------------------------------------------
-- 7. Still no projection view changes
-- -----------------------------------------------------------------------------
--
-- §8.3 does not move, for the third migration running. order_current_line joins
-- menu_item for its name and needs nothing else; the kitchen groups tickets by
-- table and person, not by menu section. Worth restating because it is the
-- surprising half: the schema of record has grown two tables and five columns
-- across 0003, 0004 and 0005, and the projection views have grown none.
