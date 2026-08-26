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

CREATE INDEX menu_item_image_event_item_index
    ON menu_item_image_event (menu_item_identifier, occurred_at);
