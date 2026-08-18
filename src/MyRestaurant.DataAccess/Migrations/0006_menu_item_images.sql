-- =============================================================================
-- 0006_menu_item_images.sql
--
-- One picture per menu item: the schema of record for TECHNICAL_SPECIFICATION
-- §7's images and §8.2's two new tables, applied at startup by DbUp (ADR-0012).
-- ADR-0015 carries the rationale; docs/MENU_AND_HANDHELD_PLAN.md Stage 4a
-- carries the staging.
--
-- This script adds two tables and touches NOTHING that already exists, which is
-- the same cut 0003 made and for the same reason: an image is a row nobody is
-- required to have, so every existing read, every existing write and every
-- existing test means exactly what it meant before this ran. The surfaces that
-- put a picture on a screen are Stage 4b's and arrive with the route that serves
-- the bytes.
--
-- 0001 through 0005 are not edited. DbUp journals by script name, so a change to
-- an applied script is a change that never runs (F-34).
--
-- WHY THE BYTES ARE HERE AT ALL, which is the decision ADR-0015 records: §15
-- DEFINES a recovery set as exactly two files — the database dump and the Data
-- Protection key ring — and scripts/restore_drill.sh gates both on every push. A
-- volume of image files would be a third artefact, which means editing that
-- definition, backup.sh, restore.sh, the drill and the runbook; and an operator
-- who took one more backup the old way afterwards would hold a set that restores
-- an application whose menu has no pictures in it. That is F-38's mechanism, and
-- it is the whole argument.
-- =============================================================================

-- The picture a guest sees beside a dish (§7, §11.1).
--
-- ONE image per item in version 1, expressed as UNIQUE on the referencing column
-- rather than as a column on menu_item. Two consequences, both wanted. The
-- picture can be replaced without menu_item being written at all, so a new photo
-- is not a menu_item_event; and the UNIQUE is itself the index the lookup needs,
-- so no CREATE INDEX is required on this table. A gallery would drop the UNIQUE
-- and add a display_order, which is a migration this one does not foreclose.
--
-- NO ON DELETE clause, which means NO ACTION — the same reading 0005 made of
-- §6.8: this system's answer to "get rid of it" is a flag, so a menu_item with a
-- picture must not become deletable by virtue of having one.
--
-- content_type is CHECK-bound to the three formats every current browser decodes
-- and this application is willing to serve. The set is stated here AND derived in
-- MyRestaurant.Domain.Menu.ImageFormat, which is F-80's shape — a vocabulary in a
-- CHECK with a copy in C# — so the agreement is asserted, and it is asserted
-- BEHAVIOURALLY rather than by reading this text: MenuItemImageTests attaches a
-- real file of every format ImageFormat can identify and requires the database to
-- accept it. That is strictly stronger than comparing two lists, because it also
-- fails when the two agree and neither can actually be stored.
--
-- THERE IS DELIBERATELY NO byte_length COLUMN. octet_length(bytes) is the length,
-- computed from the only copy of the bytes there is, and a stored integer beside
-- it would be one fact written twice — F-65's mechanism, which this project has
-- now met six times, in a place where the two copies can be made to disagree by a
-- single UPDATE. The plan's own DDL sketch carried that column, and carried
-- pixel_width and pixel_height beside it; all three are dropped here and the
-- reason is F-101.
--
-- The cap is 512 KiB and IS STATED ONLY HERE. Nothing in C# repeats the number:
-- DapperMenuItemImageAdministration reports a refusal by reading the CONSTRAINT
-- NAME off the PostgreSQL error, so this line is the one place the policy lives
-- and moving it moves it. Two constraints rather than one bounded BETWEEN,
-- because an operator who chose an empty file and an operator whose phone
-- produced a four-megabyte photograph need different sentences, and the
-- constraint name is what carries the difference back up.
--
-- Storage: PostgreSQL TOASTs a bytea this size out of line and compresses it, so
-- a scan of menu_item_image for its other columns does not touch the pictures at
-- all. What that does NOT buy is a free length: octet_length() detoasts the value
-- it measures, so the metadata read pays for the images it counts. At the
-- cardinality of one restaurant's menu that is the right trade against a stored
-- integer that can drift from the bytes it describes — and it is written down
-- here rather than left implied, because a claim beside a computation is the one
-- shape this project has twice had to make true after the fact (F-100).
CREATE TABLE menu_item_image (
    menu_item_image_identifier uuid PRIMARY KEY,
    menu_item_identifier       uuid NOT NULL UNIQUE
                               REFERENCES menu_item (menu_item_identifier),
    content_type               text NOT NULL,
    bytes                      bytea NOT NULL,
    uploaded_at                timestamptz NOT NULL,
    CONSTRAINT menu_item_image_content_type_vocabulary CHECK (content_type IN
        ('image/jpeg', 'image/png', 'image/webp')),
    CONSTRAINT menu_item_image_bytes_not_empty
        CHECK (octet_length(bytes) >= 1),
    CONSTRAINT menu_item_image_bytes_within_cap
        CHECK (octet_length(bytes) <= 524288)
);

-- Append-only, mirroring every mutation in the same transaction, exactly as
-- menu_item_event and menu_section_event do (R§6.8 · S§7 · ADR-0002).
--
-- IT REFERENCES menu_item AND NOT menu_item_image, and that is the load-bearing
-- decision in this script. A replace mints a NEW menu_item_image_identifier and
-- deletes the old row — required, because §7's route is keyed on the image
-- identifier so that Cache-Control: immutable is truthful — and a removal deletes
-- the row outright. So the row an event is about is gone by design, and a foreign
-- key to it would leave exactly two options: forbid the deletion, or cascade the
-- history away with the bytes. The item survives; the log hangs off the item.
--
-- menu_item_image_identifier is therefore a bare uuid with NO reference, which is
-- the opposite of the ruling 0005 made about menu_item_event's section payload
-- (a real key there, because §11.4 renders it and a dangling identifier renders
-- as a blank). Here the identifier is not a pointer to a row a reader can open;
-- it is the evidence that the URL changed, which is the whole of what a history
-- reader wants from it.
--
-- new_byte_length IS a column on this table although it is not one on
-- menu_item_image, and the asymmetry is the point rather than an inconsistency.
-- On the image row the length is derivable from the bytes; here the bytes are
-- gone — a removal is precisely the event whose subject no longer exists — so
-- this is the only place the number can live at all. One fact, one copy, in the
-- one artefact that outlives it.
--
-- Two paired biconditionals, both total. 'attached' and 'replaced' carry the
-- format and the size; 'removed' carries neither, being the one type whose whole
-- payload is its own name.
CREATE TABLE menu_item_image_event (
    menu_item_image_event_identifier uuid PRIMARY KEY,
    menu_item_identifier             uuid NOT NULL
                                     REFERENCES menu_item (menu_item_identifier),
    menu_item_image_identifier       uuid NOT NULL,
    actor_person_identifier          uuid NOT NULL
                                     REFERENCES person (person_identifier),
    event_type                       text NOT NULL,
    new_content_type                 text NULL,
    new_byte_length                  integer NULL,
    occurred_at                      timestamptz NOT NULL,
    CONSTRAINT menu_item_image_event_type_vocabulary CHECK (event_type IN
        ('attached', 'replaced', 'removed')),
    CONSTRAINT menu_item_image_event_content_type_payload
        CHECK ((new_content_type IS NOT NULL) = (event_type IN ('attached', 'replaced'))),
    CONSTRAINT menu_item_image_event_byte_length_payload
        CHECK ((new_byte_length IS NOT NULL) = (event_type IN ('attached', 'replaced'))),
    CONSTRAINT menu_item_image_event_new_content_type_vocabulary
        CHECK (new_content_type IS NULL OR new_content_type IN
            ('image/jpeg', 'image/png', 'image/webp')),
    CONSTRAINT menu_item_image_event_new_byte_length_bounded
        CHECK (new_byte_length IS NULL
               OR new_byte_length BETWEEN 1 AND 524288)
);

-- The one index worth having: §11.4's per-item history is this exact key, and it
-- is the read a person opens to ask when the picture last changed and who
-- changed it. No index on menu_item_image_identifier — nothing looks an event up
-- by the row it describes, because that row is frequently gone.
CREATE INDEX menu_item_image_event_item_index
    ON menu_item_image_event (menu_item_identifier, occurred_at);
