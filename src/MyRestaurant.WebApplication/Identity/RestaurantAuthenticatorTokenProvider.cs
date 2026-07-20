using Microsoft.AspNetCore.Identity;
using MyRestaurant.DataAccess.Identity;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// The authenticator (TOTP) token provider ASP.NET Core Identity dispatches to when it verifies a
/// two-factor code (<c>UserManager.VerifyTwoFactorTokenAsync</c> →
/// <c>SignInManager.TwoFactorAuthenticatorSignInAsync</c>). It <b>replaces</b> the framework's
/// <c>AuthenticatorTokenProvider</c>, which accepts a ±2-step window; §3.4 mandates <b>±1</b>, so the
/// verification is delegated to the pure <see cref="Rfc6238Totp"/> engine with the spec's skew, and
/// "now" comes from the injected <see cref="IClock"/> (deterministic in tests).
///
/// <para>Registered under <see cref="Microsoft.AspNetCore.Identity.TokenOptions.DefaultAuthenticatorProvider"/>
/// after <c>AddDefaultTokenProviders()</c> in <see cref="IdentityServiceCollectionExtensions"/>;
/// Identity's provider map keeps the last registration under a given name, so this one wins. The key
/// is the Base32 secret the user store round-trips (<c>GetAuthenticatorKeyAsync</c>); this provider
/// never generates or persists anything (TOTP is not a delivered token), so
/// <see cref="GenerateAsync"/> returns the empty string exactly as the framework provider does.</para>
/// </summary>
public sealed class RestaurantAuthenticatorTokenProvider : IUserTwoFactorTokenProvider<Person>
{
    private readonly IClock _clock;

    public RestaurantAuthenticatorTokenProvider(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// TOTP is not a generated/delivered token — the shared secret is provisioned once at
    /// enrollment — so the framework contract is to return the empty string here.
    /// </summary>
    public Task<string> GenerateAsync(string purpose, UserManager<Person> manager, Person user)
        => Task.FromResult(string.Empty);

    /// <summary>
    /// Validates <paramref name="token"/> against the user's Base32 authenticator key at the current
    /// instant within ±1 step. The <paramref name="purpose"/> is ignored (Identity passes
    /// <c>"TwoFactor"</c>), matching the framework provider. Grouped-display spaces and dashes are
    /// tolerated; a missing/blank key or a malformed secret verifies to <c>false</c>.
    /// </summary>
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

    /// <summary>
    /// The provider can be used for two-factor only once the user has an authenticator key (i.e. is
    /// enrolled). Matches the framework provider's gate.
    /// </summary>
    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<Person> manager, Person user)
    {
        ArgumentNullException.ThrowIfNull(manager);

        string? key = await manager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(key);
    }
}
