ALTER TABLE passkey_credential
    ADD COLUMN is_user_verified   boolean NOT NULL DEFAULT false,
    ADD COLUMN is_backup_eligible boolean NOT NULL DEFAULT false,
    ADD COLUMN is_backed_up       boolean NOT NULL DEFAULT false;
