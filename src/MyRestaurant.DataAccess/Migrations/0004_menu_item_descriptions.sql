-- =============================================================================
-- 0004_menu_item_descriptions.sql
--
-- The item half of TECHNICAL_SPECIFICATION §7's menu enhancement: a menu item
-- gains a description and an explicit position, and menu_item_event gains the
-- two verbs that move them. Applied at startup by DbUp (ADR-0012). ADR-0014
-- carries the rationale; docs/MENU_AND_HANDHELD_PLAN.md Stage 2 carries the
-- staging.
--
-- WHAT THIS SCRIPT DELIBERATELY DOES NOT DO
--
-- It does not add menu_item.menu_section_identifier. That column is NOT NULL
-- from birth (§7's ruling: an item under no heading is an item nobody decided
-- about), and the moment it exists CreateMenuItem.razor cannot write a row
-- without naming a section -- a form that six of the sixteen §16.3 scenarios
-- drive through AdministrationJourneys.CreateMenuItemAsync. So it lands in 0005
-- together with the surfaces that satisfy it. Slice 37 cut Stage 2 between the
-- two tables for the same reason and by the same test: a script that touches
-- nothing existing leaves the suite green BY CONSTRUCTION rather than by
-- inspection. The cost is one more migration script, and DbUp journals by
-- script name, so that is not a cost.
--
-- Every column added here carries a DEFAULT, so no backfill is needed and no
-- existing row, query, form or scenario changes meaning.
--
-- 0001, 0002 and 0003 are not edited. DbUp journals by script name, so a change
-- to an applied script is a change that never runs (F-34).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. menu_item gains two columns
-- -----------------------------------------------------------------------------

-- description is NOT NULL DEFAULT '' rather than nullable, and '' means "none",
-- exactly as menu_section.description is and for the same reason: an optional
-- payload column cannot be tied to its event type by an equality, because
-- clearing a description would have to write NULL and break the CHECK. With ''
-- as the empty value the constraint below stays total. Every existing row gets
-- '' without the table being rewritten -- PostgreSQL 11 and later store a
-- non-volatile ADD COLUMN ... DEFAULT in the catalogue rather than in the heap.
--
-- No length ceiling, and that is a ruling rather than an omission. menu_item.name
-- has none either; a ceiling here would be a number invented in a migration and
-- then re-invented in a form and in a write service, which is one fact written
-- three times.
ALTER TABLE menu_item
    ADD COLUMN description text NOT NULL DEFAULT '';

-- display_order is NOT NULL DEFAULT 0 and NOT UNIQUE, on the terms §8.2 already
-- states for menu_section.display_order: a unique ordering column has to be
-- rewritten in two phases or with a deferred constraint, for a menu with perhaps
-- sixty items on it. Reads order by (display_order, name, menu_item_identifier),
-- so a tie is stable rather than arbitrary.
--
-- DEFAULT 0 rather than an appended MAX + 1, and this is the decision that keeps
-- the suite green. Every existing item -- and every item created until 0005 --
-- sits at 0, so ORDER BY (display_order, name, identifier) is exactly the
-- ORDER BY (name, identifier) this table has always been read in. It is also the
-- honest answer: "the end of the menu" is not a defined place until an item is
-- under a heading, and §7 puts the position WITHIN its section. Appending
-- globally now would hand out numbers 0005 would have to undo.
ALTER TABLE menu_item
    ADD COLUMN display_order integer NOT NULL DEFAULT 0;

ALTER TABLE menu_item
    ADD CONSTRAINT menu_item_display_order_non_negative
    CHECK (display_order >= 0);

-- -----------------------------------------------------------------------------
-- 2. menu_item_event's CHECK constraints are replaced by name
-- -----------------------------------------------------------------------------
--
-- 0001 declared all four of them inline, so PostgreSQL generated the names:
-- menu_item_event_event_type_check, menu_item_event_new_price_amount_check,
-- menu_item_event_check and menu_item_event_check1. Those are deterministic and
-- undocumented, and depending on them in a script that runs at startup on
-- somebody else's box is depending on an implementation detail of a version of
-- PostgreSQL nobody here chose. So every CHECK on the table is dropped by
-- QUERYING for it, and named ones are added back -- after which 0005, which has
-- to widen the vocabulary once more for 'section_changed', can drop by name on a
-- tree that knows the name.
--
-- contype = 'c' selects CHECK constraints only. NOT NULL is an attribute
-- (pg_attribute.attnotnull) in PostgreSQL 17 and a contype = 'n' row in 18, so
-- neither version's NOT NULL can be caught by this loop.
--
-- The dollar-quoted block is safe in an embedded DbUp script: dbup-postgresql's
-- PostgresqlQueryParser has a DollarQuoted state that consumes a whole tagged
-- block, so the semicolons inside this body do not split the statement. This is
-- the first script in this tree to rely on that, which is stated so that a
-- failure here is read as what it is.
DO $migrate_menu_item_event_checks$
DECLARE
    doomed_constraint text;
BEGIN
    FOR doomed_constraint IN
        SELECT conname
        FROM pg_constraint
        WHERE conrelid = 'menu_item_event'::regclass
          AND contype = 'c'
        ORDER BY conname
    LOOP
        EXECUTE format(
            'ALTER TABLE menu_item_event DROP CONSTRAINT %I',
            doomed_constraint);
    END LOOP;
END
$migrate_menu_item_event_checks$;

-- -----------------------------------------------------------------------------
-- 3. menu_item_event gains two payload columns
-- -----------------------------------------------------------------------------
--
-- Both nullable, because each is carried by exactly one event type and must be
-- absent on every other -- which is what the paired CHECKs below assert. This is
-- the same shape menu_section_event has; what differs is which types carry what,
-- and that difference is deliberate (see 4).
ALTER TABLE menu_item_event
    ADD COLUMN new_description text NULL;

ALTER TABLE menu_item_event
    ADD COLUMN new_display_order integer NULL;

-- -----------------------------------------------------------------------------
-- 4. The named constraints
-- -----------------------------------------------------------------------------
--
-- The vocabulary gains 'description_changed' and 'reordered'.
--
-- The spelling is 'description_changed' rather than menu_section_event's
-- 'described', and that asymmetry is a decision rather than a slip. Each table's
-- vocabulary is internally consistent: menu_item_event has said 'name_changed'
-- and 'price_changed' since 0001, and menu_section_event says 'renamed' and
-- 'described'. Making the two tables agree would mean rewriting a vocabulary
-- already written into applied history and into rows in somebody's database, to
-- buy nothing a reader of either table needs.
ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_type_vocabulary
    CHECK (event_type IN
        ('created', 'name_changed', 'price_changed', 'description_changed',
         'reordered', 'activated', 'deactivated'));

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_new_price_non_negative
    CHECK (new_price_amount IS NULL OR new_price_amount >= 0);

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_new_display_order_non_negative
    CHECK (new_display_order IS NULL OR new_display_order >= 0);

-- The two biconditionals 0001 carried, unchanged in meaning and now named. They
-- did not need widening: neither of the new types carries a name or a price, so
-- both equalities were already true of them. They are restated because the loop
-- above dropped them, and restating them here is what makes the whole set of
-- constraints on this table readable in one place.
ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_name_payload
    CHECK ((new_name IS NOT NULL) = (event_type IN ('created', 'name_changed')));

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_price_payload
    CHECK ((new_price_amount IS NOT NULL) = (event_type IN ('created', 'price_changed')));

-- 'created' deliberately keeps carrying the name and the price ONLY. An item
-- created with a description writes 'created' and then 'description_changed', in
-- one transaction. Widening 'created' to carry all four payloads is the obvious
-- alternative and it is refused for a concrete reason: a description is
-- optional, so the equality would have to be relaxed to an implication, and
-- every 'created' row already in a database was written without one. The log
-- reads "Created as "Soup" at 4.50 / Description set", which is two lines where
-- one would do and is honest about it.
ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_description_payload
    CHECK ((new_description IS NOT NULL) = (event_type = 'description_changed'));

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_display_order_payload
    CHECK ((new_display_order IS NOT NULL) = (event_type = 'reordered'));

-- -----------------------------------------------------------------------------
-- 5. No index, and no projection view changes
-- -----------------------------------------------------------------------------
--
-- No index on menu_item's new ordering columns: §11.1's guest menu and §11.4's
-- index both read the whole table, and a sequential scan over the cardinality of
-- a restaurant menu is faster than reading an index. menu_item_event's one index
-- is already (menu_item_identifier, occurred_at), which is §11.4's per-item
-- history unchanged.
--
-- And §8.3 does not move. order_current_line joins menu_item for its name and
-- needs nothing else; the kitchen groups tickets by table and person. That is
-- worth stating because it is the surprising half: the schema of record grows
-- four columns and the projection views grow none.
