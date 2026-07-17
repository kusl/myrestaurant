# ADR-0010 — TOTP challenged on password sign-ins only; reset wipes and re-enrolls

**Status:** Accepted (2026-07-17) — F-08 / Q2 / Q3 rulings
**Finding trail:** F-08; supersedes the F-09 draft resolution
**Requirements:** `REQUIREMENTS.md` §4.2, §4.5

## Context

The draft carried a per-user "administrator may require TOTP" toggle and (in the F-09 draft resolution) challenged TOTP even after a passkey sign-in. The owner ruled: a passkey-capable authenticator is already a second factor, so challenging TOTP on top of it is ceremony without security. The per-user toggle disappears.

## Decision

1. **Challenge rule.** TOTP is challenged on the **password sign-in path only**, and only when the account has TOTP **enrolled**. The passkey sign-in path **never** challenges TOTP, regardless of enrollment. Requiredness collapses to enrollment: there is no `totp_required` flag and no administrative toggle. (`person.totp_required` is removed from the schema; enrollment state is `totp_secret_protected IS NOT NULL`.)
2. **Recovery codes** substitute for a TOTP code on the password path only. Failed TOTP and recovery-code attempts count toward the 5-failures/5-minutes lockout.
3. **Administrators are always enrolled.** Enrollment happens at grant time (and in the bootstrap wizard) and an administrator cannot remove their own TOTP. This keeps the password path — if the administrator has one — always second-factored. A passkey-only administrator (no password set) is allowed; the enrollment then simply has no path on which to be challenged, which is acceptable because there is no password to phish.
4. **Administrative reset wipes both credentials.** Resetting a user sets a temporary password, clears the password requirement state, and — **if TOTP was enrolled at reset time** — deletes the protected secret and all recovery codes, setting `must_enroll_totp`. It always sets `must_change_password`. Reset never forces TOTP onto an account that never had it.
5. **Post-authentication obligations pipeline.** After *any* successful sign-in (password or passkey), before reaching any destination: if `must_change_password`, force a password change; then if `must_enroll_totp`, force TOTP enrollment (QR + confirm code + fresh recovery codes); then continue. Running the pipeline on the passkey path too closes the gap where a user with a known temporary password could dodge the forced change by signing in with a passkey.
6. **Audit.** Each transition writes a `security_event`: `password_reset_by_administrator`, `totp_cleared_by_administrator`, `forced_password_change_completed`, `forced_totp_enrollment_completed`, plus the ordinary `totp_enrolled` / `totp_removed` / `recovery_code_used` / `recovery_codes_regenerated`.

## Consequences

- Sign-in ceremonies match their real security value: password+TOTP for shared-terminal/kitchen-style use, single-gesture passkey for everyone who has one.
- The F-09 draft resolution ("TOTP still challenged after passkey") is **superseded** by this ADR; the documentation review ledger records the reconciliation.
- One asymmetry is accepted knowingly: an enrolled user signing in by passkey is never asked for TOTP. This is by design, not an oversight.

## History

- 2026-07-16 — drafted with `totp_required` flag, admin toggle, and TOTP-after-passkey (F-09 draft).
- 2026-07-17 — **superseded in place** by the Q2/Q3 rulings as recorded above.
