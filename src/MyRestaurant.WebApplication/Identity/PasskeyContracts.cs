namespace MyRestaurant.WebApplication.Identity;

/// <summary>
/// Which WebAuthn ceremony the browser-side <c>passkey-submit</c> element should drive. The names are
/// significant: they are rendered verbatim into the element's <c>operation</c> attribute and compared
/// as strings ("Create" / "Request") by passkey.js, so they must not be renamed independently.
/// </summary>
public enum PasskeyOperation
{
    /// <summary><c>navigator.credentials.create()</c> — register a new passkey (attestation).</summary>
    Create,

    /// <summary><c>navigator.credentials.get()</c> — sign in with an existing passkey (assertion).</summary>
    Request,
}

/// <summary>
/// The two form fields the <c>passkey-submit</c> element writes back before it submits the surrounding
/// form: the JSON-serialized <c>PublicKeyCredential</c> on success, or a human-readable message when the
/// browser ceremony could not complete (cancelled, no authenticator, and so on). Reused by the sign-in
/// page (assertion, nested under the login model) and the passkey-management page (attestation).
/// </summary>
public sealed class PasskeyInputModel
{
    /// <summary>The serialized credential the server hands to <c>PerformPasskey*Async</c>.</summary>
    public string? CredentialJson { get; set; }

    /// <summary>Set instead of <see cref="CredentialJson"/> when the browser ceremony failed.</summary>
    public string? Error { get; set; }
}
