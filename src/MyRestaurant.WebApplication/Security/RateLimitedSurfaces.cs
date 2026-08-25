using System.Threading.RateLimiting;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Displays;

namespace MyRestaurant.WebApplication.Security;

/// <summary>
/// One rate-limited endpoint: the policy name the middleware selects it by, the sentence a refused
/// caller reads, and how the budget is partitioned (TECHNICAL_SPECIFICATION §4.2, §11.8, §17).
///
/// <para><b>All three are required, and that is the point of the type.</b> §17 recorded for eleven
/// slices that adding a second policy was not a two-line change, because
/// <c>RateLimiterOptions.OnRejected</c> is single-valued: the handler registered by whichever
/// extension method ran last answers <em>every</em> refusal on every policy. So the second surface to
/// acquire a limit would have been refused in the first surface's words — §4.2's <em>"too many pairing
/// attempts"</em> answering a guest who was creating an account. A construction of this record missing
/// its <see cref="Refusal"/> does not compile, which is the smallest available mechanism that makes
/// <em>a policy with no sentence of its own</em> unrepresentable rather than merely discouraged.</para>
/// </summary>
public sealed class RateLimitedSurface
{
    /// <summary>
    /// The policy name. The page opts in with <c>[EnableRateLimiting(…)]</c> naming this value, the
    /// middleware resolves the policy by it, and <see cref="RateLimitedSurfaces.RefusalFor"/> reads the
    /// sentence back out by it — three readers of one string, which is why it is declared once.
    /// </summary>
    public required string PolicyName { get; init; }

    /// <summary>
    /// What a refused caller reads. Plain text, one sentence, no detail about what was attempted: both
    /// surfaces this applies to are anonymous, and an anonymous endpoint that explains itself is an
    /// oracle (§4.2).
    /// </summary>
    public required string Refusal { get; init; }

    /// <summary>
    /// The partition this surface's budget is counted in. A delegate rather than a permit count and a
    /// window, because the two surfaces do not agree about where their numbers come from: §4.2 states
    /// pairing's normatively and <see cref="RestaurantOptions"/> carries registration's, and flattening
    /// that into two integers here would force one of them to be restated.
    /// </summary>
    public required Func<HttpContext, RateLimitPartition<string>> Partition { get; init; }
}

/// <summary>
/// Every rate-limited endpoint in this application, in one list (§4.2, §11.8, §13, §17).
///
/// <para><b>Why this exists (F-115).</b> The limiter used to be configured inside
/// <c>AddRestaurantDisplays</c>, which is where the only policy happened to belong. §17 wrote down what
/// that cost — <c>OnRejected</c> and <c>RejectionStatusCode</c> are properties of the whole limiter, not
/// of a policy, so the display extension owned the refusal wording for every surface that might ever
/// acquire a limit — and wrote down the fix in the same paragraph: <em>"doing it properly means
/// <c>OnRejected</c> dispatching on the endpoint."</em> That paragraph sat in an accepted-risks section
/// for eleven slices while `/register` had no limit at all, because the wall was real and nobody
/// wanted to answer a refused registration in the pairing surface's words.</para>
///
/// <para><b>The dispatch reads the same metadata the middleware just read.</b>
/// <c>OnRejectedContext</c> carries the <see cref="HttpContext"/> and the lease and nothing else, so the
/// only way to learn which policy refused is to ask the endpoint — which is precisely how the
/// middleware chose that policy one instant earlier. That makes the lookup as reliable as the refusal
/// itself: if the attribute could not be found, no policy could have been selected, and the request
/// would not be being refused. <b>The fallback is therefore about honesty rather than about
/// coverage</b> — see <see cref="GenericRefusal"/>.</para>
///
/// <para><b>Why the two budgets are asymmetric, which is the ruling most likely to be "tidied" away.</b>
/// Pairing's is a compile-time constant and registration's is configuration, and that is deliberate.
/// §4.2 states 5 attempts per minute normatively for a surface a member of staff touches when a tablet
/// is being installed; there is no operator decision in it. Registration's right number is a property
/// of the <em>room</em> — see the note on <see cref="GuestRegistrationPolicy"/> — which this repository
/// cannot know and must not guess on an operator's behalf.</para>
///
/// <para><b>Every public string literal on this type is a policy name</b>, and
/// <see cref="GenericRefusal"/> is <c>static readonly</c> rather than <c>const</c> for exactly that
/// reason: it keeps the set of literal constants equal to the set of policy names, so
/// <c>RateLimitingContractTests</c> can enumerate one by reflection and hold it against
/// <see cref="All"/> without keying on a naming convention. A convention would have been a gate about
/// spelling (F-67's distinction, one register down); this is a gate about the type.</para>
/// </summary>
public static class RateLimitedSurfaces
{
    /// <summary>
    /// §4.2's anonymous pairing surface. Moved here from <c>DisplayRoutes</c> in Slice 62: a policy name
    /// is neither a route nor a limit, it is the key three readers agree on, and once there were two of
    /// them the list is the only honest place for either.
    /// </summary>
    public const string PairingPolicy = "display-pairing";

    /// <summary>
    /// §11.8's anonymous registration surface.
    ///
    /// <para><b>The partition is an address and the address is the whole dining room</b>, which is the
    /// fact that decides the budget. Guests reach this page over the Cloudflare tunnel from the
    /// restaurant's own wifi, so <c>UseForwardedHeaders</c> faithfully reports one public address for
    /// every one of them: a per-address limit here is a per-<em>venue</em> limit, not a per-person one.
    /// §4.2's surface has no such problem — pairing is a staff action, one tablet at a time.</para>
    ///
    /// <para><b>So this is a volume bound rather than a credential bound.</b> There is no secret to
    /// guess at this endpoint; F-37 already ruled that the worst outcome of an unlimited one is
    /// <em>rows</em> rather than access. What the limit buys is a ceiling on how fast an anonymous
    /// caller can mint <c>person</c> rows and ask for Argon2id work, and it is sized for a full room
    /// rather than against an attacker — because the two ways of getting it wrong do not cost the same.
    /// Too loose costs spam rows, which F-37 accepted on the record. Too tight costs a seated guest who
    /// cannot create the account they need in order to order dinner, at a table where staff have no
    /// remedy and no diagnosis. That asymmetry, and not a threat model, is why the default is
    /// generous.</para>
    /// </summary>
    public const string GuestRegistrationPolicy = "guest-registration";

    /// <summary>
    /// What a caller reads when the refused policy cannot be identified.
    ///
    /// <para>Unreachable while the framework behaves, per the type's own summary, and kept anyway
    /// because the alternative is a lie. <c>EnableRateLimitingAttribute.PolicyName</c> is nullable — the
    /// framework has a constructor taking a policy <em>instance</em> — and a global limiter, which this
    /// application deliberately does not register, would refuse with no attribute in scope at all. In
    /// either case the failure mode of the dispatch is a sentence that is <b>vague</b>, never one that
    /// is <b>wrong</b>, and that is the whole property §17 was waiting for: naming the wrong surface is
    /// worse than naming none, because it looks deliberate.</para>
    ///
    /// <para><c>static readonly</c> rather than <c>const</c> on purpose — see the type's summary.</para>
    /// </summary>
    public static readonly string GenericRefusal =
        "Too many attempts from this device. Wait a few minutes, then try again.";

    /// <summary>
    /// The surfaces, and the only registration path. <c>AddRestaurantRateLimiting</c> walks this list
    /// and adds nothing that is not in it, so a policy the application enforces is a policy this list
    /// describes — which is what makes the reflection assertion above worth anything.
    /// </summary>
    public static IReadOnlyList<RateLimitedSurface> All { get; } =
    [
        new RateLimitedSurface
        {
            PolicyName = PairingPolicy,
            Refusal = "Too many pairing attempts from this device. Wait a minute, then try again.",
            Partition = PartitionPairingByAddress,
        },
        new RateLimitedSurface
        {
            PolicyName = GuestRegistrationPolicy,
            Refusal = "Too many accounts have been created from this network. Wait a few minutes, then"
                + " try again — or ask a member of staff.",
            Partition = PartitionGuestRegistrationByAddress,
        },
    ];

    /// <summary>
    /// The sentence for the policy that refused, or <see cref="GenericRefusal"/> when the caller could
    /// not say which one did. Ordinal comparison: a policy name is a key the framework matches
    /// byte-for-byte, and admitting a case-insensitive match here would make this lookup succeed on a
    /// string the middleware would have failed on.
    /// </summary>
    public static string RefusalFor(string? policyName)
    {
        if (string.IsNullOrEmpty(policyName))
        {
            return GenericRefusal;
        }

        foreach (RateLimitedSurface surface in All)
        {
            if (string.Equals(surface.PolicyName, policyName, StringComparison.Ordinal))
            {
                return surface.Refusal;
            }
        }

        return GenericRefusal;
    }

    /// <summary>
    /// One fixed window per client address (§4.2). The key is the connection's remote address, which is
    /// the real client only because <c>UseForwardedHeaders()</c> has already run — the app is always
    /// behind Caddy or the Cloudflare tunnel, so <c>UseRateLimiter()</c> must stay after it in the
    /// pipeline or every request in the building would share the proxy's single partition.
    ///
    /// <para><c>QueueLimit = 0</c> is the point of a brute-force limit: the sixth attempt in a minute is
    /// refused immediately rather than parked until a permit frees up.</para>
    /// </summary>
    private static RateLimitPartition<string> PartitionPairingByAddress(HttpContext httpContext)
        => RateLimitPartition.GetFixedWindowLimiter(
            PartitionKeyFor(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = DisplayRoutes.PairingAttemptsPerWindow,
                Window = DisplayRoutes.PairingRateLimitWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });

    /// <summary>
    /// One fixed window per client address for §11.8, with the budget read from configuration (§13).
    ///
    /// <para>The options are resolved <em>per request</em> from <c>RequestServices</c> rather than
    /// captured when the policy was registered. That is not indirection for its own sake: capturing
    /// would put the permit count in a closure created during
    /// <c>AddRestaurantRateLimiting</c>, and this method is then a function of when it was registered
    /// rather than of what the process was configured with — the shape F-50 is about, with the two
    /// copies separated by a service-collection call instead of by a file. <c>RestaurantOptions</c> is a
    /// singleton bound once at startup, so the resolution is a dictionary lookup.</para>
    ///
    /// <para>Partitioned on the address like pairing, with the caveat that here the address is a whole
    /// venue — see <see cref="GuestRegistrationPolicy"/>. <c>QueueLimit = 0</c> for pairing's reason:
    /// parking a refused registration behind a permit would hold a browser open on a form.</para>
    /// </summary>
    private static RateLimitPartition<string> PartitionGuestRegistrationByAddress(HttpContext httpContext)
    {
        RestaurantOptions options = httpContext.RequestServices.GetRequiredService<RestaurantOptions>();

        return RateLimitPartition.GetFixedWindowLimiter(
            PartitionKeyFor(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.GuestRegistrationAttemptsPerWindow,
                Window = TimeSpan.FromMinutes(options.GuestRegistrationWindowMinutes),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }

    /// <summary>
    /// The partition key both surfaces use. One method rather than two spellings of
    /// <c>RemoteIpAddress?.ToString() ?? "unknown"</c>, so the two surfaces cannot disagree about what
    /// an unknown address is called — and so that a caller with no address resolvable at all lands in
    /// one shared bucket rather than in a partition per null, which is not a partition.
    /// </summary>
    private static string PartitionKeyFor(HttpContext httpContext)
        => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
