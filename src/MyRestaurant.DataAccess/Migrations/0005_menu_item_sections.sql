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

ALTER TABLE menu_item
    ADD COLUMN menu_section_identifier uuid NULL;

UPDATE menu_item
SET menu_section_identifier = (
        SELECT menu_section.menu_section_identifier
        FROM menu_section
        ORDER BY menu_section.display_order,
                 menu_section.name,
                 menu_section.menu_section_identifier
        LIMIT 1)
WHERE menu_item.menu_section_identifier IS NULL;

ALTER TABLE menu_item
    ALTER COLUMN menu_section_identifier SET NOT NULL;

ALTER TABLE menu_item
    ADD CONSTRAINT menu_item_menu_section_reference
    FOREIGN KEY (menu_section_identifier)
    REFERENCES menu_section (menu_section_identifier);

ALTER TABLE menu_item_event
    DROP CONSTRAINT menu_item_event_type_vocabulary;

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_type_vocabulary
    CHECK (event_type IN
        ('created', 'name_changed', 'price_changed', 'description_changed',
         'section_changed', 'reordered', 'activated', 'deactivated'));

ALTER TABLE menu_item_event
    ADD COLUMN new_menu_section_identifier uuid NULL
    REFERENCES menu_section (menu_section_identifier);

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_section_payload
    CHECK ((new_menu_section_identifier IS NOT NULL) = (event_type = 'section_changed'));

CREATE INDEX menu_item_section_index
    ON menu_item (menu_section_identifier, display_order, name);
