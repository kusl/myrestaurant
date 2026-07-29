using System.Text.Json;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

/// <summary>
/// A software WebAuthn authenticator attached to one page over the Chrome DevTools Protocol, so the
/// §3.3 passkey ceremonies run for real in the browser with no human thumb involved. This is what
/// §16.3 means by "passkey via virtual authenticator".
///
/// <para>The configuration is chosen to match the credential the product actually wants. It presents
/// as a platform authenticator (<c>internal</c> transport) with resident-key support, because
/// <c>IdentityPasskeyOptions.ResidentKeyRequirement</c> is <c>"preferred"</c> and a discoverable
/// credential is what makes the username-less sign-in path work. It reports the user as verified,
/// because <c>UserVerificationRequirement</c> is also <c>"preferred"</c> and a passkey sign-in that
/// skipped verification would be a weaker credential than the one a real device produces. And it
/// simulates presence automatically, because there is no gesture to simulate in a headless run.</para>
///
/// <para><c>defaultBackupEligibility</c> and <c>defaultBackupState</c> arrived in Chromium's 13x line
/// and became <em>required</em> in the same change, which means a single fixed argument list is wrong
/// on one side of that boundary or the other. Rather than pinning a browser build, the call is
/// attempted with them and retried without. They matter here beyond mere acceptance: the §3.3 store
/// persists <c>is_backup_eligible</c> and <c>is_backed_up</c>, and the assertion handler reads them
/// back, so a scenario should exercise credentials that carry the bits set rather than absent.</para>
/// </summary>
internal sealed class VirtualAuthenticator : IAsyncDisposable
{
    private readonly ICDPSession _session;

    private VirtualAuthenticator(ICDPSession session, string authenticatorId)
    {
        _session = session;
        AuthenticatorId = authenticatorId;
    }

    /// <summary>The CDP identifier of the authenticator, for later credential inspection if needed.</summary>
    internal string AuthenticatorId { get; }

    internal static async Task<VirtualAuthenticator> AttachAsync(IBrowserContext context, IPage page)
    {
        ICDPSession session = await context.NewCDPSessionAsync(page);

        await session.SendAsync("WebAuthn.enable", new Dictionary<string, object>
        {
            ["enableUI"] = false,
        });

        JsonElement? added;

        try
        {
            added = await session.SendAsync(
                "WebAuthn.addVirtualAuthenticator", BuildParameters(includeBackupDefaults: true));
        }
        catch (PlaywrightException)
        {
            added = await session.SendAsync(
                "WebAuthn.addVirtualAuthenticator", BuildParameters(includeBackupDefaults: false));
        }

        string authenticatorId =
            added is { } result && result.TryGetProperty("authenticatorId", out JsonElement identifier)
                ? identifier.GetString() ?? string.Empty
                : string.Empty;

        return new VirtualAuthenticator(session, authenticatorId);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.DetachAsync();
        }
        catch (PlaywrightException)
        {
            // The page or its context may already be closed; there is nothing left to detach from.
        }
    }

    private static Dictionary<string, object> BuildParameters(bool includeBackupDefaults)
    {
        Dictionary<string, object> options = new()
        {
            ["protocol"] = "ctap2",
            ["ctap2Version"] = "ctap2_1",
            ["transport"] = "internal",
            ["hasResidentKey"] = true,
            ["hasUserVerification"] = true,
            ["isUserVerified"] = true,
            ["automaticPresenceSimulation"] = true,
        };

        if (includeBackupDefaults)
        {
            options["defaultBackupEligibility"] = true;
            options["defaultBackupState"] = true;
        }

        return new Dictionary<string, object> { ["options"] = options };
    }
}
