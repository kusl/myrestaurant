namespace MyRestaurant.DataAccess.Identity;

/// <summary>
/// The ASP.NET Core Identity user entity — one row of the <c>person</c> table
/// (TECHNICAL_SPECIFICATION §8.2). It is a plain mutable POCO because Identity mutates the
/// entity in place (set password hash, bump failed-access count, …) and then persists it
/// through <see cref="DapperUserStore.UpdateAsync"/>. There is no separate normalized-username
/// or normalized-email column: <c>username</c> and <c>email_address</c> are <c>citext</c>, so
/// case-insensitive uniqueness and lookup are handled by PostgreSQL, not a shadow column (§3.1).
///
/// <para><b>Security stamp is a <c>uuid</c>, not Identity's Base32 string.</b> Identity treats
/// the stamp as an opaque value it only ever compares for equality and regenerates on a
/// credential/role change; the schema stores it as <c>uuid</c>. The store therefore mints a
/// fresh <see cref="Guid"/> whenever Identity asks to set a new stamp
/// (<see cref="DapperUserStore.SetSecurityStampAsync"/>) and returns it as a string — which is
/// exactly what makes resets/deactivations bite live sessions within the revalidation interval
/// (§3.1).</para>
///
/// <para><b>Two-factor state is derived, not stored as a flag.</b> Enrollment ==
/// <c>totp_secret_protected IS NOT NULL</c> (§3.4); there is no <c>totp_required</c> column, and
/// <see cref="DapperUserStore.GetTwoFactorEnabledAsync"/> reads <see cref="TotpSecretProtected"/>
/// rather than an independent boolean.</para>
/// </summary>
public sealed class Person
{
    /// <summary>Primary key — application-generated UUIDv7 (ADR-0011). Set by the store if unset.</summary>
    public Guid PersonIdentifier { get; set; }

    /// <summary>The unique <c>citext</c> username, 3–64 characters (CHECK-enforced, §3.1).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Optional human display name shown on rosters and in the kitchen queue.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional <c>citext</c> email address — manual staff escalation only, never messaged automatically (§11.1).</summary>
    public string? EmailAddress { get; set; }

    /// <summary>Optional phone number — manual staff escalation only (§11.1).</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>The Argon2id PHC string (§3.2), or <c>null</c> for a passkey-only account.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>The Data-Protection-encrypted TOTP secret (§3.4); <c>null</c> means "not enrolled".</summary>
    public string? TotpSecretProtected { get; set; }

    /// <summary>Set by an administrative reset; the obligations pipeline forces a change before any destination (§3.5).</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Set by a reset that cleared an enrolled TOTP secret; the pipeline forces re-enrollment (§3.5).</summary>
    public bool MustEnrollTotp { get; set; }

    /// <summary>Opaque anti-forgery stamp regenerated on every credential/role change (§3.1). See the type remarks.</summary>
    public Guid SecurityStamp { get; set; }

    /// <summary>Consecutive failed sign-in attempts; five locks the account (§3.1).</summary>
    public int FailedAccessCount { get; set; }

    /// <summary>When a lockout ends, or <c>null</c> when not locked out (§3.1).</summary>
    public DateTimeOffset? LockoutEndAt { get; set; }

    /// <summary>Deactivation blocks sign-in and invalidates sessions via the stamp; deletion does not exist (F-10b).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Row creation timestamp (UTC). Set by the store if unset.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
