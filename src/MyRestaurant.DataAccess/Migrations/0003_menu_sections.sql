CREATE TABLE menu_section (
    menu_section_identifier uuid PRIMARY KEY,
    name                    citext NOT NULL UNIQUE
                            CHECK (char_length(name) BETWEEN 1 AND 80),
    description             text NOT NULL DEFAULT '',
    display_order           integer NOT NULL CHECK (display_order >= 0),
    is_active               boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL
);

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

CREATE INDEX menu_section_event_section_index
    ON menu_section_event (menu_section_identifier, occurred_at);
