using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.WebApplication.Identity;

public sealed class RestaurantAuthenticatorTokenProvider : IUserTwoFactorTokenProvider<Person>
{
    private readonly IClock _clock;

    public RestaurantAuthenticatorTokenProvider(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public Task<string> GenerateAsync(string purpose, UserManager<Person> manager, Person user)
        => Task.FromResult(string.Empty);

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<Person> manager, Person user)
    {
        ArgumentNullException.ThrowIfNull(manager);

        string? key = await manager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!Base32Text.TryDecode(key, out byte[] secret))
        {
            return false;
        }

        string normalized = token.Replace(" ", string.Empty).Replace("-", string.Empty);
        return Rfc6238Totp.ValidateCode(secret, normalized, _clock.UtcNow);
    }

    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<Person> manager, Person user)
    {
        ArgumentNullException.ThrowIfNull(manager);

        string? key = await manager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(key);
    }
}
