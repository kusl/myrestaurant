-- =============================================================================
-- 0001_initial_schema.sql
--
-- The schema of record, verbatim from TECHNICAL_SPECIFICATION §8.2 (tables) and
-- §8.3 (projection views). Applied at startup by DbUp (ADR-0012). PostgreSQL,
-- current major; the citext extension gives case-insensitive usernames/e-mail.
--
-- All identifiers are snake_case and unabbreviated (carve-out for TOTP/HMAC/QR/
-- URL/SQL/TLS per REQUIREMENTS §8). Primary keys are application-generated
-- UUIDv7 (ADR-0011) — there are deliberately NO database defaults for
-- identifiers. Timestamps are timestamptz in UTC. Money is numeric(10,2).
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS citext;

CREATE TABLE person (
    person_identifier        uuid PRIMARY KEY,
    username                 citext NOT NULL UNIQUE
                             CHECK (char_length(username) BETWEEN 3 AND 64),
    display_name             text NULL,
    email_address            citext NULL,        -- optional; manual escalation only (§11.1)
    phone_number             text NULL,          -- optional; manual escalation only (§11.1)
    password_hash            text NULL,          -- PHC argon2id string (§3.2)
    totp_secret_protected    text NULL,          -- Data-Protection-encrypted; NULL = not enrolled
    must_change_password     boolean NOT NULL DEFAULT false,
    must_enroll_totp         boolean NOT NULL DEFAULT false,
    security_stamp           uuid NOT NULL,
    failed_access_count      integer NOT NULL DEFAULT 0,
    lockout_end_at           timestamptz NULL,
    is_active                boolean NOT NULL DEFAULT true,
    created_at               timestamptz NOT NULL
);

CREATE TABLE person_role (
    person_role_identifier       uuid PRIMARY KEY,
    person_identifier            uuid NOT NULL REFERENCES person (person_identifier),
    role_name                    text NOT NULL
                                 CHECK (role_name IN ('administrator', 'kitchen', 'counter')),
    granted_by_person_identifier uuid NOT NULL REFERENCES person (person_identifier),
    granted_at                   timestamptz NOT NULL,
    UNIQUE (person_identifier, role_name)
);

CREATE TABLE passkey_credential (
    passkey_credential_identifier uuid PRIMARY KEY,
    person_identifier             uuid NOT NULL REFERENCES person (person_identifier),
    credential_id                 bytea NOT NULL UNIQUE,
    public_key                    bytea NOT NULL,
    signature_counter             bigint NOT NULL DEFAULT 0,
    transports                    text NULL,
    credential_display_name       text NULL,
    created_at                    timestamptz NOT NULL
);

CREATE TABLE totp_recovery_code (
    totp_recovery_code_identifier uuid PRIMARY KEY,
    person_identifier             uuid NOT NULL REFERENCES person (person_identifier),
    code_hash                     bytea NOT NULL,       -- sha256
    used_at                       timestamptz NULL,
    created_at                    timestamptz NOT NULL
);
CREATE INDEX totp_recovery_code_person_index ON totp_recovery_code (person_identifier);

CREATE TABLE person_address (
    person_address_identifier uuid PRIMARY KEY,
    person_identifier         uuid NOT NULL REFERENCES person (person_identifier),
    label                     text NOT NULL,      -- always free text, chosen by the user ("Home", "Work")
    street_line_one           text NULL,
    street_line_two           text NULL,
    city                      text NULL,
    region                    text NULL,
    postal_code               text NULL,
    country                   text NULL,
    created_at                timestamptz NOT NULL
);
CREATE INDEX person_address_person_index ON person_address (person_identifier);
-- Deliberate scaffolding for a possible future delivery/takeout feature (REQUIREMENTS §4.6):
-- consumed by nothing in version 1, and not to be removed as dead weight.

CREATE TABLE security_event (
    security_event_identifier uuid PRIMARY KEY,
    subject_person_identifier uuid NOT NULL REFERENCES person (person_identifier),
    actor_person_identifier   uuid NULL REFERENCES person (person_identifier), -- NULL = the subject themselves / system
    event_type                text NOT NULL CHECK (event_type IN (
        'account_created', 'account_deactivated', 'account_reactivated',
        'password_changed', 'password_reset_by_administrator',
        'forced_password_change_completed',
        'totp_enrolled', 'totp_removed', 'totp_cleared_by_administrator',
        'forced_totp_enrollment_completed',
        'recovery_code_used', 'recovery_codes_regenerated',
        'passkey_registered', 'passkey_removed',
        'role_granted', 'role_revoked',
        'sign_in_succeeded', 'sign_in_failed', 'account_locked_out')),
    occurred_at               timestamptz NOT NULL
);
CREATE INDEX security_event_subject_index ON security_event (subject_person_identifier, occurred_at);

CREATE TABLE restaurant_table (
    restaurant_table_identifier uuid PRIMARY KEY,
    label                       text NOT NULL UNIQUE,
    join_secret                 bytea NOT NULL CHECK (octet_length(join_secret) = 32),
    join_secret_rotated_at      timestamptz NULL,
    is_active                   boolean NOT NULL DEFAULT true,
    created_at                  timestamptz NOT NULL
);

CREATE TABLE table_display_device (
    table_display_device_identifier uuid PRIMARY KEY,
    restaurant_table_identifier     uuid NOT NULL REFERENCES restaurant_table (restaurant_table_identifier),
    device_label                    text NOT NULL,
    device_secret_hash              bytea NOT NULL CHECK (octet_length(device_secret_hash) = 32), -- sha256
    paired_by_person_identifier     uuid NOT NULL REFERENCES person (person_identifier),
    paired_at                       timestamptz NOT NULL,
    revoked_at                      timestamptz NULL,
    revoked_by_person_identifier    uuid NULL REFERENCES person (person_identifier),
    last_seen_at                    timestamptz NULL,
    CHECK ((revoked_at IS NULL) = (revoked_by_person_identifier IS NULL))
);
CREATE INDEX table_display_device_table_index ON table_display_device (restaurant_table_identifier);

CREATE TABLE table_display_pairing_code (
    table_display_pairing_code_identifier uuid PRIMARY KEY,
    restaurant_table_identifier           uuid NOT NULL REFERENCES restaurant_table (restaurant_table_identifier),
    code_hash                             bytea NOT NULL CHECK (octet_length(code_hash) = 32), -- sha256
    created_by_person_identifier          uuid NOT NULL REFERENCES person (person_identifier),
    created_at                            timestamptz NOT NULL,
    expires_at                            timestamptz NOT NULL,
    used_at                               timestamptz NULL
);

CREATE TABLE table_sitting (
    table_sitting_identifier    uuid PRIMARY KEY,
    restaurant_table_identifier uuid NOT NULL REFERENCES restaurant_table (restaurant_table_identifier),
    opened_at                   timestamptz NOT NULL,
    closed_at                   timestamptz NULL,
    closed_by_person_identifier uuid NULL REFERENCES person (person_identifier),
    settled_total_amount        numeric(10,2) NULL,
    CHECK ((closed_at IS NULL) = (closed_by_person_identifier IS NULL)),
    CHECK ((closed_at IS NULL) = (settled_total_amount IS NULL))
);
-- at most one open sitting per table:
CREATE UNIQUE INDEX table_sitting_one_open_per_table
    ON table_sitting (restaurant_table_identifier) WHERE closed_at IS NULL;
CREATE INDEX table_sitting_table_index ON table_sitting (restaurant_table_identifier, opened_at);

CREATE TABLE table_sitting_member (
    table_sitting_member_identifier uuid PRIMARY KEY,
    table_sitting_identifier        uuid NOT NULL REFERENCES table_sitting (table_sitting_identifier),
    person_identifier               uuid NOT NULL REFERENCES person (person_identifier),
    joined_at                       timestamptz NOT NULL,
    UNIQUE (table_sitting_identifier, person_identifier)
);

CREATE TABLE menu_item (
    menu_item_identifier uuid PRIMARY KEY,
    name                 text NOT NULL,
    price_amount         numeric(10,2) NOT NULL CHECK (price_amount >= 0),
    is_active            boolean NOT NULL DEFAULT true,
    created_at           timestamptz NOT NULL
);

CREATE TABLE menu_item_event (
    menu_item_event_identifier uuid PRIMARY KEY,
    menu_item_identifier       uuid NOT NULL REFERENCES menu_item (menu_item_identifier),
    actor_person_identifier    uuid NOT NULL REFERENCES person (person_identifier),
    event_type                 text NOT NULL CHECK (event_type IN
                               ('created', 'name_changed', 'price_changed', 'activated', 'deactivated')),
    new_name                   text NULL,
    new_price_amount           numeric(10,2) NULL CHECK (new_price_amount IS NULL OR new_price_amount >= 0),
    occurred_at                timestamptz NOT NULL,
    CHECK ((new_name IS NOT NULL)         = (event_type IN ('created', 'name_changed'))),
    CHECK ((new_price_amount IS NOT NULL) = (event_type IN ('created', 'price_changed')))
);
CREATE INDEX menu_item_event_item_index ON menu_item_event (menu_item_identifier, occurred_at);

CREATE TABLE guest_order (
    guest_order_identifier   uuid PRIMARY KEY,
    table_sitting_identifier uuid NOT NULL REFERENCES table_sitting (table_sitting_identifier),
    person_identifier        uuid NOT NULL REFERENCES person (person_identifier),
    created_at               timestamptz NOT NULL,
    UNIQUE (table_sitting_identifier, person_identifier)
);

CREATE TABLE order_event (
    order_event_identifier  uuid PRIMARY KEY,
    guest_order_identifier  uuid NOT NULL REFERENCES guest_order (guest_order_identifier),
    sequence_number         bigint NOT NULL CHECK (sequence_number >= 1),
    event_type              text NOT NULL CHECK (event_type IN
        ('guest_submission', 'staff_edit', 'price_adjustment', 'fulfillment', 'fulfillment_reversal')),
    actor_person_identifier uuid NOT NULL REFERENCES person (person_identifier),
    actor_role              text NOT NULL CHECK (actor_role IN
        ('guest', 'kitchen', 'counter', 'administrator')),
    occurred_at             timestamptz NOT NULL,
    UNIQUE (guest_order_identifier, sequence_number),
    UNIQUE (order_event_identifier, event_type),   -- composite-FK target for subtype enforcement
    CHECK (event_type <> 'guest_submission'    OR actor_role = 'guest'),
    CHECK (event_type <> 'staff_edit'          OR actor_role IN ('kitchen', 'counter', 'administrator')),
    CHECK (event_type <> 'price_adjustment'    OR actor_role IN ('counter', 'administrator')),
    CHECK (event_type <> 'fulfillment'         OR actor_role IN ('kitchen', 'administrator')),
    CHECK (event_type <> 'fulfillment_reversal' OR actor_role IN ('kitchen', 'administrator'))
);
CREATE INDEX order_event_order_index ON order_event (guest_order_identifier, sequence_number);

CREATE TABLE order_operation_line_added (
    order_operation_line_added_identifier uuid PRIMARY KEY,
    order_event_identifier                uuid NOT NULL,
    event_type                            text NOT NULL
        CHECK (event_type IN ('guest_submission', 'staff_edit')),
    order_line_identifier                 uuid NOT NULL UNIQUE,   -- the line's identity
    menu_item_identifier                  uuid NOT NULL REFERENCES menu_item (menu_item_identifier),
    quantity                              integer NOT NULL CHECK (quantity BETWEEN 1 AND 100),
    unit_price_amount                     numeric(10,2) NOT NULL CHECK (unit_price_amount >= 0),
    customization_note                    text NULL,
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_added_event_index
    ON order_operation_line_added (order_event_identifier);

CREATE TABLE order_operation_line_removed (
    order_operation_line_removed_identifier uuid PRIMARY KEY,
    order_event_identifier                  uuid NOT NULL,
    event_type                              text NOT NULL
        CHECK (event_type IN ('guest_submission', 'staff_edit')),
    order_line_identifier                   uuid NOT NULL UNIQUE   -- removal is terminal
        REFERENCES order_operation_line_added (order_line_identifier),
    reason                                  text NULL,
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_removed_event_index
    ON order_operation_line_removed (order_event_identifier);

CREATE TABLE order_operation_line_price_adjusted (
    order_operation_line_price_adjusted_identifier uuid PRIMARY KEY,
    order_event_identifier                         uuid NOT NULL,
    event_type                                     text NOT NULL
        CHECK (event_type = 'price_adjustment'),
    order_line_identifier                          uuid NOT NULL
        REFERENCES order_operation_line_added (order_line_identifier),
    new_unit_price_amount                          numeric(10,2) NOT NULL CHECK (new_unit_price_amount >= 0),
    reason                                         text NOT NULL CHECK (btrim(reason) <> ''),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_price_adjusted_line_index
    ON order_operation_line_price_adjusted (order_line_identifier);

CREATE TABLE order_operation_line_fulfilled (
    order_operation_line_fulfilled_identifier uuid PRIMARY KEY,
    order_event_identifier                    uuid NOT NULL,
    event_type                                text NOT NULL CHECK (event_type = 'fulfillment'),
    order_line_identifier                     uuid NOT NULL
        REFERENCES order_operation_line_added (order_line_identifier),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_fulfilled_line_index
    ON order_operation_line_fulfilled (order_line_identifier);

CREATE TABLE order_operation_line_fulfillment_reverted (
    order_operation_line_fulfillment_reverted_identifier uuid PRIMARY KEY,
    order_event_identifier                                uuid NOT NULL,
    event_type                                            text NOT NULL
        CHECK (event_type = 'fulfillment_reversal'),
    order_line_identifier                                 uuid NOT NULL
        REFERENCES order_operation_line_added (order_line_identifier),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_fulfillment_reverted_line_index
    ON order_operation_line_fulfillment_reverted (order_line_identifier);

CREATE TABLE kitchen_notification (
    kitchen_notification_identifier uuid PRIMARY KEY,
    order_event_identifier          uuid NOT NULL,
    event_type                      text NOT NULL
        CHECK (event_type IN ('guest_submission', 'staff_edit')),
    kind                            text NOT NULL CHECK (kind IN ('initial', 'reminder')),
    created_at                      timestamptz NOT NULL,
    UNIQUE (order_event_identifier, kind),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);

CREATE TABLE order_visibility_event (
    order_visibility_event_identifier uuid PRIMARY KEY,
    guest_order_identifier            uuid NOT NULL REFERENCES guest_order (guest_order_identifier),
    actor_person_identifier           uuid NOT NULL REFERENCES person (person_identifier),
    event_type                        text NOT NULL CHECK (event_type IN ('hidden', 'unhidden')),
    occurred_at                       timestamptz NOT NULL
);
CREATE INDEX order_visibility_event_order_index
    ON order_visibility_event (guest_order_identifier, occurred_at);

-- =============================================================================
-- Projection views (§8.3). Reads only; the event tables above are the source of
-- truth, and MyRestaurant.Domain.OrderProjection.FromEvents folds to the same
-- result (view ≡ fold, asserted by integration tests — §8.5).
-- =============================================================================

CREATE VIEW order_current_line AS
SELECT
    added_event.guest_order_identifier,
    added.order_line_identifier,
    added.menu_item_identifier,
    menu_item.name AS menu_item_name,
    added.quantity,
    COALESCE(latest_price.new_unit_price_amount, added.unit_price_amount)
        AS current_unit_price_amount,
    added.customization_note,
    COALESCE(latest_flip.is_fulfilled, false) AS is_fulfilled,
    added_event.occurred_at AS added_at,
    added.order_event_identifier AS added_by_order_event_identifier
FROM order_operation_line_added AS added
JOIN order_event AS added_event
    ON added_event.order_event_identifier = added.order_event_identifier
JOIN menu_item
    ON menu_item.menu_item_identifier = added.menu_item_identifier
LEFT JOIN order_operation_line_removed AS removed
    ON removed.order_line_identifier = added.order_line_identifier
LEFT JOIN LATERAL (
    SELECT adjustment.new_unit_price_amount
    FROM order_operation_line_price_adjusted AS adjustment
    JOIN order_event AS adjustment_event
        ON adjustment_event.order_event_identifier = adjustment.order_event_identifier
    WHERE adjustment.order_line_identifier = added.order_line_identifier
    ORDER BY adjustment_event.sequence_number DESC
    LIMIT 1
) AS latest_price ON true
LEFT JOIN LATERAL (
    SELECT flip.was_fulfillment AS is_fulfilled
    FROM (
        SELECT true AS was_fulfillment, fulfilled_event.sequence_number
        FROM order_operation_line_fulfilled AS fulfilled
        JOIN order_event AS fulfilled_event
            ON fulfilled_event.order_event_identifier = fulfilled.order_event_identifier
        WHERE fulfilled.order_line_identifier = added.order_line_identifier
        UNION ALL
        SELECT false, reverted_event.sequence_number
        FROM order_operation_line_fulfillment_reverted AS reverted
        JOIN order_event AS reverted_event
            ON reverted_event.order_event_identifier = reverted.order_event_identifier
        WHERE reverted.order_line_identifier = added.order_line_identifier
    ) AS flip
    ORDER BY flip.sequence_number DESC
    LIMIT 1
) AS latest_flip ON true
WHERE removed.order_line_identifier IS NULL;

CREATE VIEW kitchen_pending_line AS
SELECT
    line.*,
    guest_order.table_sitting_identifier,
    guest_order.person_identifier,
    person.display_name AS person_display_name,
    table_sitting.restaurant_table_identifier,
    restaurant_table.label AS restaurant_table_label
FROM order_current_line AS line
JOIN guest_order       ON guest_order.guest_order_identifier = line.guest_order_identifier
JOIN person            ON person.person_identifier = guest_order.person_identifier
JOIN table_sitting     ON table_sitting.table_sitting_identifier = guest_order.table_sitting_identifier
JOIN restaurant_table  ON restaurant_table.restaurant_table_identifier = table_sitting.restaurant_table_identifier
WHERE table_sitting.closed_at IS NULL
  AND NOT line.is_fulfilled;

CREATE VIEW order_current_state AS
SELECT
    guest_order.guest_order_identifier,
    guest_order.table_sitting_identifier,
    guest_order.person_identifier,
    first_event.first_submitted_at,
    last_event.last_event_at,
    COALESCE(line_summary.pending_line_count, 0)  AS pending_line_count,
    COALESCE(line_summary.fulfilled_line_count, 0) AS fulfilled_line_count,
    COALESCE(line_summary.current_total_amount, 0::numeric(10,2)) AS current_total_amount
FROM guest_order
LEFT JOIN LATERAL (
    SELECT min(occurred_at) AS first_submitted_at
    FROM order_event
    WHERE order_event.guest_order_identifier = guest_order.guest_order_identifier
      AND order_event.event_type = 'guest_submission'
) AS first_event ON true
LEFT JOIN LATERAL (
    SELECT max(occurred_at) AS last_event_at
    FROM order_event
    WHERE order_event.guest_order_identifier = guest_order.guest_order_identifier
) AS last_event ON true
LEFT JOIN LATERAL (
    SELECT
        count(*) FILTER (WHERE NOT line.is_fulfilled) AS pending_line_count,
        count(*) FILTER (WHERE line.is_fulfilled)     AS fulfilled_line_count,
        sum(line.quantity * line.current_unit_price_amount) AS current_total_amount
    FROM order_current_line AS line
    WHERE line.guest_order_identifier = guest_order.guest_order_identifier
) AS line_summary ON true;

CREATE VIEW sitting_bill AS
SELECT
    guest_order.table_sitting_identifier,
    guest_order.person_identifier,
    guest_order.guest_order_identifier,
    COALESCE(sum(line.quantity * line.current_unit_price_amount), 0::numeric(10,2))
        AS person_total_amount
FROM guest_order
LEFT JOIN order_current_line AS line
    ON line.guest_order_identifier = guest_order.guest_order_identifier
GROUP BY guest_order.table_sitting_identifier,
         guest_order.person_identifier,
         guest_order.guest_order_identifier;

CREATE VIEW order_visibility_current AS
SELECT DISTINCT ON (guest_order_identifier)
    guest_order_identifier,
    (event_type = 'hidden') AS is_hidden
FROM order_visibility_event
ORDER BY guest_order_identifier, occurred_at DESC, order_visibility_event_identifier DESC;
