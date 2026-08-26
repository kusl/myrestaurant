ALTER TABLE menu_item_image
    ADD COLUMN alt_text text NOT NULL DEFAULT '';

ALTER TABLE menu_item_image_event
    ADD COLUMN new_alt_text text NULL;

ALTER TABLE menu_item_image_event
    DROP CONSTRAINT menu_item_image_event_type_vocabulary;

ALTER TABLE menu_item_image_event
    ADD CONSTRAINT menu_item_image_event_type_vocabulary
    CHECK (event_type IN ('attached', 'replaced', 'removed', 'alt_text_changed'));

ALTER TABLE menu_item_image_event
    ADD CONSTRAINT menu_item_image_event_alt_text_payload
    CHECK ((new_alt_text IS NOT NULL) = (event_type = 'alt_text_changed'));
