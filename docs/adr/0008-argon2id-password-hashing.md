# ADR-0008 — Argon2id password hashing via a custom IPasswordHasher

**Status:** Accepted (2026-07-17) — F-06 ruling ("robust Argon2" directive)
**Finding trail:** F-06 (ruling text)
**Requirements:** `REQUIREMENTS.md` §2 (stack table), §4.1

## Context

ASP.NET Core Identity's default hasher is PBKDF2 ("PasswordHasher v3"). The owner directed a robust Argon2 configuration instead. Argon2id is the current OWASP-recommended password hashing function; it is memory-hard, which PBKDF2 is not.

## Decision

Register a custom `IPasswordHasher<Person>` (in `MyRestaurant.WebApplication`, algorithm code in `MyRestaurant.Domain`-adjacent utility or `DataAccess` — implementation's choice, interface-driven) implementing **Argon2id** using **Konscious.Security.Cryptography.Argon2** (MIT license — satisfies the no-paid-dependency rule).

Parameters (all configurable via environment, defaults chosen per ruling):

| Parameter | Environment key | Default |
|---|---|---|
| Memory | `ARGON2_MEMORY_KIBIBYTES` | `65536` (64 MiB) |
| Iterations | `ARGON2_ITERATIONS` | `3` |
| Parallelism | `ARGON2_PARALLELISM` | `1` |
| Concurrent hash cap | `ARGON2_MAX_CONCURRENT_HASHES` | `4` |

Fixed choices: salt = 16 bytes CSPRNG per hash; output tag = 32 bytes; no secret key; no associated data.

**Storage format** is the standard PHC string, stored in `person.password_hash`:

```
$argon2id$v=19$m=65536,t=3,p=1$<base64(salt)>$<base64(tag)>
```

(standard Base64 without padding, per the PHC string specification). The format is self-describing, so verification always recomputes with the **stored** parameters and compares with `CryptographicOperations.FixedTimeEquals`.

**Rehash-on-verify:** when a verification succeeds but the stored parameters differ from the currently configured ones, the hasher returns `PasswordVerificationResult.SuccessRehashNeeded`; ASP.NET Core Identity then transparently rehashes at sign-in. Parameter upgrades therefore roll out passively.

**Denial-of-service guard:** each hash computation costs ~64 MiB. A process-wide `SemaphoreSlim(ARGON2_MAX_CONCURRENT_HASHES)` bounds concurrent computations (default worst case ≈ 256 MiB); excess sign-in attempts queue. This sits behind the existing authentication-endpoint rate limiting and lockout (5 failures / 5 minutes).

**Floor guard:** at startup the application refuses to run (fail fast, non-zero exit) if configured below the OWASP minimum for this construction: `ARGON2_MEMORY_KIBIBYTES < 19456`, `ARGON2_ITERATIONS < 2`, or `ARGON2_PARALLELISM < 1`.

## Consequences

- Password verification takes tens of milliseconds and real memory by design; interactive sign-in remains comfortably fast, offline cracking does not.
- Memory pressure is bounded and observable (`password_hash_duration_milliseconds` histogram, technical specification §12).
- The PHC format means a future migration (parameter change or even algorithm change with a new PHC identifier) needs no schema change and no forced resets.
- Konscious is a small, focused dependency; if it were ever abandoned, the PHC strings remain verifiable by any conforming Argon2 implementation.

## History

- 2026-07-17 — created and accepted per the F-06 ruling; replaces the drafted PBKDF2 default.
