-- =============================================================================
-- 0002_passkey_credential_webauthn_state.sql
--
-- M2 passkey slice (TECHNICAL_SPECIFICATION §3.3). The .NET 10 Identity passkey
-- store round-trips a `UserPasskeyInfo` whose credential record carries WebAuthn
-- backup flags and a user-verification flag. The 0001 `passkey_credential` table
-- (verbatim from §8.2) predates that API and stores only the public key, sign
-- counter, transports, and display name.
--
-- Why this is not optional: WebAuthn assertion (§7.2 of the spec, verifying an
-- authentication assertion, step 19) compares the STORED backup-eligible bit
-- against the authenticator data on every passkey sign-in and fails the ceremony
-- on a mismatch. If that bit is not persisted it cannot be compared faithfully,
-- so sign-in would spuriously accept or reject. These three boolean columns close
-- that framework gap — exactly the "verify the .NET 10 passkey API against the
-- framework source first" check called out in BUILD_PROGRESS.
--
-- What is deliberately NOT added: `UserPasskeyInfo` also exposes the raw
-- attestation object and client-data JSON. Attestation is 'none' (§3.3), nothing
-- in version 1 re-reads either blob, and they are the large fields, so the store
-- reconstructs them as empty on read rather than persisting dead weight.
--
-- Shape: additive and safe. Every column is NOT NULL DEFAULT false, so the
-- (currently empty) table backfills without a rewrite, and DbUp journals this as
-- a distinct script — 0001 is never edited (ADR-0012).
-- =============================================================================

ALTER TABLE passkey_credential
    ADD COLUMN is_user_verified   boolean NOT NULL DEFAULT false,
    ADD COLUMN is_backup_eligible boolean NOT NULL DEFAULT false,
    ADD COLUMN is_backed_up       boolean NOT NULL DEFAULT false;
