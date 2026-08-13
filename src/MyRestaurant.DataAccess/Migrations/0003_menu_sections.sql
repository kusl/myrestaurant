-- =============================================================================
-- 0003_menu_sections.sql
--
-- Menu sections: the schema of record for TECHNICAL_SPECIFICATION §7's headings
-- and §8.2's two new tables, applied at startup by DbUp (ADR-0012). ADR-0014
-- carries the rationale; docs/MENU_AND_HANDHELD_PLAN.md Stage 2 carries the
-- staging.
--
-- This script adds two tables and touches NOTHING that already exists. That is
-- the whole point of where the stage was cut: menu_item gains its
-- menu_section_identifier, description and display_order in 0004, and that
-- column is NOT NULL from the moment it exists, so no reading surface ever sees
-- an item under no heading. A section is a row nobody is required to have yet.
--
-- 0001 and 0002 are not edited. DbUp journals by script name, so a change to an
-- applied script is a change that never runs (F-34).
-- =============================================================================

-- The heading a menu item is filed under (§7). citext, and UNIQUE, because two
-- sections called "Drinks" is never a real menu — it is a mis-tap, and the guest
-- sees it as one heading printed twice with the items split arbitrarily between
-- them. menu_item.name is deliberately neither (§7: a kitchen runs "Soup" as a
-- rotating special), and the asymmetry is the ruling rather than an oversight.
--
-- description is NOT NULL DEFAULT '' rather than nullable, and '' means "none".
-- An optional payload column cannot be tied to its event type by an equality —
-- clearing a description would have to write NULL and break the CHECK below — so
-- with '' as the empty value the constraint stays total.
--
-- display_order is NOT NULL and NOT UNIQUE. A unique ordering column has to be
-- rewritten in two phases or with a deferred constraint, for a menu with perhaps
-- eight headings on it. Reads order by (display_order, name,
-- menu_section_identifier), so a tie is stable rather than arbitrary.
CREATE TABLE menu_section (
    menu_section_identifier uuid PRIMARY KEY,
    name                    citext NOT NULL UNIQUE
                            CHECK (char_length(name) BETWEEN 1 AND 80),
    description             text NOT NULL DEFAULT '',
    display_order           integer NOT NULL CHECK (display_order >= 0),
    is_active               boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL
);

-- Append-only, mirroring every mutation in the same transaction, exactly as
-- menu_item_event does (R§6.8 · S§7 · ADR-0002).
--
-- The three payload CHECKs are named rather than inline. 0001 declared
-- menu_item_event's inline, so PostgreSQL generated menu_item_event_check and
-- menu_item_event_check1 — deterministic, undocumented, and not a thing for a
-- later migration to depend on. Naming them here means 0004, which has to widen
-- the menu_item_event equalities, can drop by name on a tree that knows the name.
--
-- Every equality is total: a 'created' event carries all three payloads, because
-- a section is created with a name, a description (possibly ''), and the order it
-- was appended at. Each later verb carries exactly the one column it moved.
CREATE TABLE menu_section_event (
    menu_section_event_identifier uuid PRIMARY KEY,
    menu_section_identifier       uuid NOT NULL
                                  REFERENCES menu_section (menu_section_identifier),
    actor_person_identifier       uuid NOT NULL REFERENCES person (person_identifier),
    event_type                    text NOT NULL CHECK (event_type IN
                                  ('created', 'renamed', 'described', 'reordered',
                                   'activated', 'deactivated')),
    new_name                      citext NULL
                                  CHECK (new_name IS NULL
                                         OR char_length(new_name) BETWEEN 1 AND 80),
    new_description               text NULL,
    new_display_order             integer NULL
                                  CHECK (new_display_order IS NULL OR new_display_order >= 0),
    occurred_at                   timestamptz NOT NULL,
    CONSTRAINT menu_section_event_name_payload
        CHECK ((new_name IS NOT NULL) = (event_type IN ('created', 'renamed'))),
    CONSTRAINT menu_section_event_description_payload
        CHECK ((new_description IS NOT NULL) = (event_type IN ('created', 'described'))),
    CONSTRAINT menu_section_event_display_order_payload
        CHECK ((new_display_order IS NOT NULL) = (event_type IN ('created', 'reordered')))
);

-- The one index worth having. §11.4 renders a section's complete uncapped event
-- history, which is this exact key. No index is added on menu_section's own
-- ordering columns: the UNIQUE on name is already an index, and a sequential scan
-- over single-digit cardinality is faster than reading one.
CREATE INDEX menu_section_event_section_index
    ON menu_section_event (menu_section_identifier, occurred_at);
