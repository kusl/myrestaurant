-- =============================================================================
-- 0008_menu_item_reactions.sql
--
-- A person may like a dish, and may stop. Applied at startup by DbUp (ADR-0012).
-- TECHNICAL_SPECIFICATION §7, §8.2 and §8.3 carry the mechanism; Stage 5a of
-- docs/MENU_AND_HANDHELD_PLAN.md carries the staging.
--
-- 0001 through 0007 are not edited. DbUp journals by script name, so a change to
-- an applied script is a change that never runs (F-34).
--
-- THIS SCRIPT TOUCHES NOTHING THAT ALREADY EXISTS. One new table, one new index,
-- one new view: no ALTER, no backfill, no constraint widened, no existing row,
-- query, form or scenario changed in meaning. That is 0003's cut and 0006's, and
-- it is why the suite is green here BY CONSTRUCTION rather than by inspection.
--
-- WHY THIS IS AN EVENT TABLE AND NOT A ROW PER LIKE. The obvious schema is
-- (menu_item_identifier, person_identifier) UNIQUE with a DELETE for unliking,
-- and it is smaller. It is also the one shape in this repository that would
-- destroy evidence: R§6.8 and ADR-0002 make this system append-only because a
-- record that can be removed is a record nobody can audit, and §6.8's hide-never-
-- delete rule has exactly one stated exception — the image bytes (§7) — granted
-- because the history of those lives in a log beside them. A like has no bytes
-- and no log beside it; the log IS the record. So the fold is a view and the
-- press is a row, which is order_visibility_event's shape one register over.
--
-- WHY THERE IS NO actor_person_identifier, WHICH EVERY OTHER EVENT TABLE HERE
-- HAS. On menu_item_event, menu_section_event and menu_item_image_event the
-- subject and the actor genuinely differ: an administrator renames somebody
-- else's dish, and §11.1 renders "kitchen removed Salmon". A reaction's subject
-- IS the person reacting — nobody likes a dish on another person's behalf, and
-- there is no surface in §11 that could offer to — so an actor column would be a
-- column constrained to equal its neighbour on every row that will ever exist.
-- One fact, one copy (F-65's rule, arriving as a column that was never written).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. The event table
-- -----------------------------------------------------------------------------
--
-- Two types and no payload columns, so there is no paired biconditional to
-- write: 'liked' and 'unliked' each carry their own name and nothing else, which
-- is the shape menu_item_image_event's 'removed' has and the shape
-- order_visibility_event has entirely.
--
-- THE VOCABULARY CONSTRAINT IS NAMED, on 0006's rule rather than 0001's. 0001
-- declared menu_item_event's CHECKs inline, PostgreSQL generated
-- menu_item_event_check and menu_item_event_check1, and 0004 had to write a
-- PL/pgSQL loop over pg_constraint, a dollar-quoted body, and — because dbup-core
-- substitutes $variables$ before PostgreSQL ever sees the text — the F-78 fix in
-- the runner's configuration. 0006 named every constraint it created and 0007
-- collected the return in two ordinary ALTER statements. A third word here costs
-- two lines because of that.
--
-- BOTH FOREIGN KEYS ARE REAL. menu_item_image_event deliberately carries a bare
-- uuid for the picture it names, because a replace deletes the row it points at;
-- nothing here is deleted, so the opposite ruling applies and both references are
-- keys. What that buys is the property this stage was cut for: TRUNCATE ... 
-- CASCADE on menu_item and person reaches this table, so OrderTestWorld needs no
-- edit at all.
CREATE TABLE menu_item_reaction_event (
    menu_item_reaction_event_identifier uuid PRIMARY KEY,
    menu_item_identifier                uuid NOT NULL
                                        REFERENCES menu_item (menu_item_identifier),
    person_identifier                   uuid NOT NULL
                                        REFERENCES person (person_identifier),
    event_type                          text NOT NULL,
    occurred_at                         timestamptz NOT NULL,
    CONSTRAINT menu_item_reaction_event_type_vocabulary CHECK (event_type IN
        ('liked', 'unliked'))
);

-- -----------------------------------------------------------------------------
-- 2. One index, and its column order is the fold's
-- -----------------------------------------------------------------------------
--
-- (menu_item_identifier, person_identifier, occurred_at) rather than
-- menu_item_image_event's (menu_item_identifier, occurred_at), because the read
-- this table exists for is DISTINCT ON (menu_item_identifier,
-- person_identifier) and that is the prefix it wants. The write's own lookup —
-- "what does this person currently think of this dish" — is the same prefix, so
-- one index serves both directions and there is no second one to keep honest.
--
-- Deliberately NOT UNIQUE on any prefix. A person may like, unlike and like again,
-- and each press is a row; a unique index would be the delete-on-unlike schema
-- arriving through the back door.
CREATE INDEX menu_item_reaction_event_item_person_index
    ON menu_item_reaction_event (menu_item_identifier, person_identifier, occurred_at);

-- -----------------------------------------------------------------------------
-- 3. The fold (§8.3)
-- -----------------------------------------------------------------------------
--
-- order_visibility_current's shape with a two-column partition. The last press
-- from each person about each dish is that person's current opinion; everything
-- before it is history and is kept.
--
-- THE IDENTIFIER TIE-BREAK IS LOAD-BEARING HERE IN A WAY IT IS NOT ON
-- order_visibility_current, and this is the sentence worth reading twice. Hiding
-- an order twice in one millisecond is a thing nobody does. Tapping a heart twice
-- in one millisecond is a thing everybody does, and one transaction stamps its
-- rows with one IClock.UtcNow (§8.1), so two presses genuinely share an
-- occurred_at. Without the tie-break DISTINCT ON returns whichever row the scan
-- reached first, which is the OLDEST — so a double-tap would read back as the
-- state before it. The tie-break is only an answer because §8.1 requires
-- IIdentifierFactory to ascend within a millisecond, which is the property F-95
-- found nothing was keeping and made true.
--
-- is_liked is (event_type = 'liked') rather than a NOT IN or a CASE, on
-- order_visibility_current's precedent — and the equality is what makes a third
-- word in the vocabulary a visible change rather than a silent one: a 'loved'
-- added to the CHECK above would fold to FALSE here, which is wrong and which
-- somebody reading this line can see, where "not unliked" would fold to TRUE and
-- look correct.
CREATE VIEW menu_item_reaction_current AS
SELECT DISTINCT ON (menu_item_identifier, person_identifier)
    menu_item_identifier,
    person_identifier,
    (event_type = 'liked') AS is_liked
FROM menu_item_reaction_event
ORDER BY menu_item_identifier,
         person_identifier,
         occurred_at DESC,
         menu_item_reaction_event_identifier DESC;

-- -----------------------------------------------------------------------------
-- 4. No count view, and no reaction column on menu_item
-- -----------------------------------------------------------------------------
--
-- A menu_item_reaction_count view would be one line — SELECT menu_item_identifier,
-- count(*) FROM menu_item_reaction_current WHERE is_liked GROUP BY 1 — and it is
-- deliberately not written. §8.3's views exist to give the application a shape it
-- would otherwise assemble from event tables by hand; an aggregate over a view
-- that already exists is a GROUP BY, and a second view would be a second place
-- for "which of these is popular" to be defined. The reader carries it, and the
-- plausible wrong version of it — counting 'liked' rows in the event table, which
-- counts every press anybody ever made — is asserted against in
-- MenuItemReactionTests rather than prevented by adding DDL.
--
-- And no like_count column on menu_item, for the reason there is no byte_length
-- column on menu_item_image (F-101): a stored total beside the rows it totals is
-- one fact written twice in a place where a single INSERT can make the two
-- disagree, and this is the one table in the schema that grows a row every time
-- somebody's thumb moves.
