using MyRestaurant.DataAccess.Menu;
using MyRestaurant.Domain.Menu;

namespace MyRestaurant.WebApplication.Menu;

public static class MenuImageRoutes
{
    public const string Prefix = "/menu/image";

    public const string Pattern = "/menu/image/{menuItemImageIdentifier:guid}";

    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    public static string ForImage(Guid menuItemImageIdentifier)
        => $"{Prefix}/{menuItemImageIdentifier:D}";
}

public static class MenuItemImageUpload
{
    public const string FileFieldName = "picture";

    public static string AcceptAttribute { get; } =
        string.Join(",", ImageFormat.RecognisedContentTypes);

    public static string RecognisedTypesForOperators { get; } =
        string.Join(", ", ImageFormat.RecognisedContentTypes);

    public const string ByteBudgetAttributeName = "data-picture-byte-budget";

    public const string LongestEdgeAttributeName = "data-picture-longest-edge";

    public const string StatusElementId = "picture-status";

    public const int LongestEdgePixels = 1600;
}

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
                    return Results.NotFound();
                }

                context.Response.Headers.CacheControl = MenuImageRoutes.ImmutableCacheControl;

                return Results.Bytes(content.Bytes, content.ContentType);
            })
            .AllowAnonymous()
            .WithName("MenuItemImage");

        return endpoints;
    }
}
