ALTER TABLE menu_item
    ADD COLUMN description text NOT NULL DEFAULT '';

ALTER TABLE menu_item
    ADD COLUMN display_order integer NOT NULL DEFAULT 0;

ALTER TABLE menu_item
    ADD CONSTRAINT menu_item_display_order_non_negative
    CHECK (display_order >= 0);

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

ALTER TABLE menu_item_event
    ADD COLUMN new_description text NULL;

ALTER TABLE menu_item_event
    ADD COLUMN new_display_order integer NULL;

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

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_name_payload
    CHECK ((new_name IS NOT NULL) = (event_type IN ('created', 'name_changed')));

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_price_payload
    CHECK ((new_price_amount IS NOT NULL) = (event_type IN ('created', 'price_changed')));

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_description_payload
    CHECK ((new_description IS NOT NULL) = (event_type = 'description_changed'));

ALTER TABLE menu_item_event
    ADD CONSTRAINT menu_item_event_display_order_payload
    CHECK ((new_display_order IS NOT NULL) = (event_type = 'reordered'));
