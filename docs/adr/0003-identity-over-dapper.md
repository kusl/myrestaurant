# ADR-0003 — ASP.NET Core Identity primitives over custom Dapper stores

**Status:** Accepted (2026-07-17)
**Finding trail:** F-08 (superseded flag model), Q2/Q3 rulings (ADR-0010)
**Requirements:** `REQUIREMENTS.md` §2 (no Entity Framework), §4

## Context

The stack mandates Dapper and forbids Entity Framework, while the identity requirements (passwords, passkeys, TOTP, recovery codes, lockout, forced credential changes) are exactly the territory where hand-rolled code goes wrong. ASP.NET Core Identity's *core services* are storage-agnostic: the hardened logic (token verification, lockout counting, security stamps, the .NET 10 built-in WebAuthn/passkey ceremony handling) sits above replaceable store interfaces.

## Decision

Use ASP.NET Core Identity **core services** with custom Dapper-backed store implementations in `MyRestaurant.DataAccess` (`IUserStore`, `IUserPasswordStore`, `IUserTwoFactorStore`, `IUserLockoutStore`, `IUserSecurityStampStore`, `IUserPasskeyStore` and companions) over the `person` tables defined in the technical specification §8. No Entity Framework anywhere.

Specifics:

- **Password hashing** is supplied by a custom `IPasswordHasher<Person>` implementing Argon2id — see ADR-0008. Identity's default PBKDF2 hasher is not registered.
- **Passkeys** use the built-in WebAuthn support ASP.NET Core Identity ships in .NET 10. If a functional gap is found in practice, `fido2-net-lib` (MIT) is the approved fallback; the decision to fall back must be recorded here by editing this ADR.
- **TOTP** uses the framework's RFC 6238 implementation (6 digits, 30-second step, ±1 step skew). TOTP secrets are stored encrypted with ASP.NET Data Protection (`totp_secret_protected`); the Data Protection key ring persists to a named volume (`DATA_PROTECTION_KEYS_DIRECTORY`) so protected secrets and cookies survive container restarts.
- **Cookies:** secure, HttpOnly, SameSite=Lax, 24-hour sliding expiry, security-stamp revalidation every 5 minutes so administrative actions (role revocation, password reset, deactivation) bite within minutes on live sessions.
- **Lockout:** 5 consecutive failures locks the account for 5 minutes (F-19). Failed TOTP and recovery-code attempts count as failed sign-ins.

## Consequences

- The project owns SQL and mapping code for the stores (finite, testable surface) and inherits framework-quality credential ceremonies instead of reimplementing them.
- Identity's abstractions dictate a few shapes (security stamp as opaque value, normalized username handling); the schema accommodates them (`security_stamp uuid`, `citext` usernames).
- Upgrades of ASP.NET Core Identity must be smoke-tested against the custom stores; the integration test suite covers every store method.

## History

- 2026-07-16 — drafted.
- 2026-07-17 — accepted; password hasher carved out to ADR-0008; TOTP policy narrowed by ADR-0010.
