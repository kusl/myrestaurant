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

CREATE INDEX menu_item_reaction_event_item_person_index
    ON menu_item_reaction_event (menu_item_identifier, person_identifier, occurred_at);

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
