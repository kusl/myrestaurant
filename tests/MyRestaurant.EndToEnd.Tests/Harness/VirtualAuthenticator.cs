using System.Text.Json;
using Microsoft.Playwright;

namespace MyRestaurant.EndToEnd.Tests.Harness;

internal sealed class VirtualAuthenticator : IAsyncDisposable
{
    private readonly ICDPSession _session;

    private VirtualAuthenticator(ICDPSession session, string authenticatorId)
    {
        _session = session;
        AuthenticatorId = authenticatorId;
    }

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
