namespace MyRestaurant.WebApplication.Identity;

public enum PasskeyOperation
{
    Create,
    Request,
}

public sealed class PasskeyInputModel
{
    public string? CredentialJson { get; set; }

    public string? Error { get; set; }
}
