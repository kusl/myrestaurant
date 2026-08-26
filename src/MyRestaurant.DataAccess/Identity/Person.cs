namespace MyRestaurant.DataAccess.Identity;

public sealed class Person
{
    public Guid PersonIdentifier { get; set; }

    public string Username { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? EmailAddress { get; set; }

    public string? PhoneNumber { get; set; }

    public string? PasswordHash { get; set; }

    public string? TotpSecretProtected { get; set; }

    public bool MustChangePassword { get; set; }

    public bool MustEnrollTotp { get; set; }

    public Guid SecurityStamp { get; set; }

    public int FailedAccessCount { get; set; }

    public DateTimeOffset? LockoutEndAt { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
