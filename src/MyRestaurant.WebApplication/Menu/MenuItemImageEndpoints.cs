using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.Menu;

namespace MyRestaurant.WebApplication.Menu;

/// <summary>
/// The route a stored menu picture is served on (TECHNICAL_SPECIFICATION §7, §11.1, §11.4). One place,
/// so the endpoint, the <c>&lt;img&gt;</c> that points at it and the tests cannot drift apart — the same
/// role <see cref="Identity.AccountRoutes"/>, <see cref="Displays.DisplayRoutes"/> and
/// <c>RestaurantClockRoutes</c> already play for their areas.
/// </summary>
public static class MenuImageRoutes
{
    /// <summary>The path prefix, without a trailing slash.</summary>
    public const string Prefix = "/menu/image";

    /// <summary>
    /// The routing pattern. <b>Keyed on the <em>image</em> identifier and not on the item's</b>, which
    /// is the whole of ADR-0015's second decision and is what makes
    /// <see cref="ImmutableCacheControl"/> a true statement rather than a hope: a replace mints a new
    /// <c>menu_item_image_identifier</c> and deletes the old row (§7), so the URL changes exactly when
    /// the bytes do. Keying on the item would have bought an <c>ETag</c> and a revalidation round trip
    /// per image per page load, on phones, forever, to avoid a cost paid once per upload.
    /// </summary>
    public const string Pattern = "/menu/image/{menuItemImageIdentifier:guid}";

    /// <summary>
    /// A year, and <c>immutable</c>, because the URL is a content address. Declared here rather than
    /// inline at the endpoint so the fact and the reason above sit in one place.
    /// </summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary>The URL for one stored picture. The only way anything in this tree builds one.</summary>
    public static string ForImage(Guid menuItemImageIdentifier)
        => $"{Prefix}/{menuItemImageIdentifier:D}";
}

/// <summary>
/// The two names the picture form and the code that reads it must agree on (§11.4).
///
/// <para><b>Why a type rather than two string literals in a Razor file.</b> The upload is a plain
/// multipart <c>&lt;form&gt;</c> read back through <c>HttpContext.Request.Form.Files</c>, and that
/// lookup is by <em>name</em>. A field renamed in the markup and not in the handler produces no
/// compiler error, no validation message and no exception: the handler finds nothing, reports the
/// upload as empty, and the operator retries the same file forever. So the name is one constant that
/// both sides read, and <c>MenuItemImageSurfaceContractTests</c> asserts the markup still reads
/// it.</para>
///
/// <para><b><see cref="AcceptAttribute"/> is derived rather than written.</b> It is the third place the
/// media-type vocabulary would otherwise be declared — after §8.2's CHECK and
/// <see cref="ImageFormat.RecognisedContentTypes"/> — which is <b>F-80's</b> shape, so it is computed
/// from the second of those and never spelled out. An <c>accept</c> attribute is a hint to the file
/// picker and refuses nothing on its own; that is precisely why it is the copy most likely to drift
/// unnoticed, since a wrong one costs an operator a file dialog that hides the photograph they
/// want.</para>
/// </summary>
public static class MenuItemImageUpload
{
    /// <summary>The <c>name</c> of the file input, and the key the handler looks the file up by.</summary>
    public const string FileFieldName = "picture";

    /// <summary>
    /// The <c>accept</c> attribute for that input: every media type this application recognises,
    /// comma-separated, computed from the domain's own census.
    /// </summary>
    public static string AcceptAttribute { get; } =
        string.Join(",", ImageFormat.RecognisedContentTypes);

    /// <summary>
    /// The attribute carrying §8.2's cap to the browser, in bytes (Stage 4e).
    ///
    /// <para><b>Its presence is what switches the downscaler on</b>, which is the whole of the
    /// arrangement's safety: <c>wwwroot/js/menu-picture.js</c> does nothing at all to an input that does
    /// not carry this, so a surface that cannot read the cap — see
    /// <c>IMenuItemImageDirectory.ReadDeclaredByteCapAsync</c>, which may answer <c>null</c> — renders no
    /// budget and gets the behaviour this feature had before Stage 4e rather than a downscaler guessing.
    /// The number is never written in C#, in Razor or in JavaScript; it travels from the migration,
    /// through the constraint, to the attribute, and no copy of it exists anywhere in this tree.</para>
    /// </summary>
    public const string ByteBudgetAttributeName = "data-picture-byte-budget";

    /// <summary>
    /// The attribute carrying the longest edge, in pixels, that a re-encoded picture may have.
    ///
    /// <para><b>This is a genuinely new number and it is declared once, here.</b> It is not a second copy
    /// of anything: §8.2 stores no <c>pixel_width</c> and no <c>pixel_height</c> on purpose (F-101,
    /// because nothing in this stack can measure one), so no dimension has ever been written down in this
    /// repository and there is nothing for it to drift from.</para>
    /// </summary>
    public const string LongestEdgeAttributeName = "data-picture-longest-edge";

    /// <summary>
    /// The <c>id</c> of the element the downscaler writes its one sentence into, and the value the file
    /// input's <c>aria-describedby</c> carries.
    ///
    /// <para><b>One association doing two jobs.</b> The script resolves the element it reports into from
    /// the input's own <c>aria-describedby</c> rather than from a marker of its own, which means the
    /// sentence a sighted operator reads under the control is the same sentence a screen reader
    /// announces as that control's description — with no second attribute for somebody to rename half
    /// of. It is a constant for <see cref="FileFieldName"/>'s reason: both ends are strings, a rename
    /// applied to one side produces no compiler error, and the symptom is a downscaler that silently
    /// stops reporting.</para>
    /// </summary>
    public const string StatusElementId = "picture-status";

    /// <summary>
    /// The longest edge a re-encoded picture may have, in pixels.
    ///
    /// <para>Sized for the two surfaces that render one and no larger: §11.1's guest card is a thumbnail
    /// on a handset and §11.4's panel is capped by <c>.manage-picture-image</c>'s own width, so anything
    /// past this is detail no screen in this building will show. It is also the bound that does most of
    /// the work — a phone camera's four megabytes are mostly pixels rather than quality, and shrinking
    /// the raster is what turns them into something §8.2 will store.</para>
    /// </summary>
    public const int LongestEdgePixels = 1600;
}

/// <summary>
/// Serves menu item pictures (TECHNICAL_SPECIFICATION §7, §11.1, §11.4; Stage 4b of
/// <c>docs/MENU_AND_HANDHELD_PLAN.md</c>).
///
/// <para><b>A minimal API endpoint rather than a page, because the response is not a document.</b>
/// Everything a Razor component endpoint does — render a layout, apply the obligations pipeline,
/// negotiate a circuit — is wrong for a response whose body is a <c>bytea</c> and whose
/// <c>Content-Type</c> comes out of a column. It sits beside
/// <c>MapRestaurantAccountEndpoints</c> and <c>MapRestaurantClock</c> for the same reason both of
/// those do.</para>
///
/// <para><b>Anonymous, and that is a decision rather than an oversight.</b> §11.1's guest menu is the
/// surface this exists for, and a guest reading a menu at a table may not have signed in yet (§4.3
/// places registration at the moment of joining). A picture of a dish is also what a menu is *for* —
/// it is not a secret, it is the thing a restaurant would print. The identifier is a UUIDv7 rather
/// than a filename, so nothing about the URL enumerates a menu, and the endpoint returns 404 for
/// anything it does not hold.</para>
///
/// <para><b>It needs no §3.5 obligations exemption, unlike the clock and the source offer.</b> Those
/// two are asked for by a page a locked-down principal is looking at. This one is a subresource of a
/// page — the guest's menu, or an administrator's item page — and a principal with an outstanding
/// obligation cannot reach either, because the middleware redirected them before the page rendered.
/// An exemption would therefore protect nothing and would widen the set of paths that answer during
/// a forced password change.</para>
///
/// <para><b>What it deliberately does not do: resize, re-encode, or negotiate a format.</b> §7 stores
/// what it was given and this hands that back. The <c>Content-Type</c> is the stored column, which is
/// safe to write into a response header on this origin only because the write checked it against the
/// bytes' own signature before storing it (<see cref="ImageFormat"/>) — the two halves are one
/// decision and neither is sound without the other.</para>
/// </summary>
public static class MenuItemImageEndpoints
{
    public static IEndpointRouteBuilder MapRestaurantMenuImages(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            MenuImageRoutes.Pattern,
            async (
                Guid menuItemImageIdentifier,
                IMenuItemImageDirectory images,
                HttpContext context) =>
            {
                MenuItemImageContent? content = await images
                    .ReadContentAsync(menuItemImageIdentifier, context.RequestAborted)
                    .ConfigureAwait(false);

                if (content is null)
                {
                    // A URL naming a picture since replaced or removed. 404 rather than a placeholder:
                    // the caller is an <img> whose alternative text is already on the page.
                    return Results.NotFound();
                }

                // Set before the result executes. A result writes the body and the content headers; this
                // is a caching header and nothing downstream touches it.
                context.Response.Headers.CacheControl = MenuImageRoutes.ImmutableCacheControl;

                return Results.Bytes(content.Bytes, content.ContentType);
            })
            .AllowAnonymous()
            .WithName("MenuItemImage");

        return endpoints;
    }
}
