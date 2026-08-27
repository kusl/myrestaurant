CREATE TABLE menu_item_comment_event (
    menu_item_comment_event_identifier uuid PRIMARY KEY,
    menu_item_identifier               uuid NOT NULL
                                       REFERENCES menu_item (menu_item_identifier),
    person_identifier                  uuid NOT NULL
                                       REFERENCES person (person_identifier),
    event_type                         text NOT NULL,
    body                               text,
    occurred_at                        timestamptz NOT NULL,
    CONSTRAINT menu_item_comment_event_type_vocabulary CHECK (event_type IN
        ('submitted', 'withdrawn')),
    CONSTRAINT menu_item_comment_event_body_payload
        CHECK ((event_type = 'submitted') = (body IS NOT NULL)),
    CONSTRAINT menu_item_comment_event_body_not_blank
        CHECK (body IS NULL OR btrim(body) <> ''),
    CONSTRAINT menu_item_comment_event_body_within_cap
        CHECK (body IS NULL OR length(body) <= 1000)
);

CREATE INDEX menu_item_comment_event_item_person_index
    ON menu_item_comment_event (menu_item_identifier, person_identifier, occurred_at);

CREATE VIEW menu_item_comment_current AS
SELECT DISTINCT ON (menu_item_identifier, person_identifier)
    menu_item_comment_event_identifier,
    menu_item_identifier,
    person_identifier,
    body,
    occurred_at
FROM menu_item_comment_event
ORDER BY menu_item_identifier,
         person_identifier,
         occurred_at DESC,
         menu_item_comment_event_identifier DESC;
