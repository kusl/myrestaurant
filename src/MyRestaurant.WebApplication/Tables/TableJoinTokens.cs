using System.Text.Encodings.Web;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;
using Net.Codecrete.QrCodeGenerator;

namespace MyRestaurant.WebApplication.Tables;

public sealed record TableJoinQrCode(
    Guid TableIdentifier,
    string Token,
    string JoinUrl,
    string QrCodeSvg,
    DateTimeOffset GeneratedAt,
    DateTimeOffset NextRotationAt);

public interface ITableJoinTokens
{
    Task<TableJoinQrCode?> DescribeCurrentAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);

    Task<JoinTokenValidationResult> ValidateAsync(
        Guid tableIdentifier,
        string presentedToken,
        CancellationToken cancellationToken = default);
}

public sealed class TableJoinTokens : ITableJoinTokens
{
    private const string ResultValid = "valid";
    private const string ResultExpired = "expired";
    private const string ResultInvalid = "invalid";

    private readonly ITableJoinSecretReader _joinSecrets;
    private readonly RestaurantMetrics _metrics;
    private readonly IClock _clock;
    private readonly int _rotationSeconds;
    private readonly string _publicOrigin;

    public TableJoinTokens(
        ITableJoinSecretReader joinSecrets,
        RestaurantMetrics metrics,
        IClock clock,
        RestaurantOptions options)
    {
        ArgumentNullException.ThrowIfNull(joinSecrets);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _joinSecrets = joinSecrets;
        _metrics = metrics;
        _clock = clock;
        _rotationSeconds = options.TableJoinTokenRotationSeconds;
        _publicOrigin = options.PublicOrigin;
    }

    public async Task<TableJoinQrCode?> DescribeCurrentAsync(Guid tableIdentifier, CancellationToken cancellationToken = default)
    {
        byte[]? joinSecret = await _joinSecrets
            .ReadActiveJoinSecretAsync(tableIdentifier, cancellationToken).ConfigureAwait(false);
        if (joinSecret is null)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        string token = JoinTokenService.ComputeCurrentToken(joinSecret, tableIdentifier, now, _rotationSeconds);
        string joinUrl = JoinTokenService.BuildJoinUrl(_publicOrigin, tableIdentifier, token);

        return new TableJoinQrCode(
            TableIdentifier: tableIdentifier,
            Token: token,
            JoinUrl: joinUrl,
            QrCodeSvg: RenderJoinQrSvg(joinUrl),
            GeneratedAt: now,
            NextRotationAt: JoinTokenService.NextRotationInstant(now, _rotationSeconds));
    }

    public async Task<JoinTokenValidationResult> ValidateAsync(
        Guid tableIdentifier,
        string presentedToken,
        CancellationToken cancellationToken = default)
    {
        byte[]? joinSecret = await _joinSecrets
            .ReadActiveJoinSecretAsync(tableIdentifier, cancellationToken).ConfigureAwait(false);

        JoinTokenValidationResult result = joinSecret is null
            ? JoinTokenValidationResult.Invalid
            : JoinTokenService.Validate(
                joinSecret,
                tableIdentifier,
                presentedToken ?? string.Empty,
                _clock.UtcNow,
                _rotationSeconds);

        _metrics.RecordTableJoinTokenValidated(MetricLabelFor(result));
        return result;
    }

    private static string MetricLabelFor(JoinTokenValidationResult result) => result switch
    {
        JoinTokenValidationResult.Valid => ResultValid,
        JoinTokenValidationResult.Expired => ResultExpired,
        _ => ResultInvalid,
    };

    private static string RenderJoinQrSvg(string joinUrl)
    {
        const int quietZoneModules = 4;
        const string darkColor = "#16202b";
        const string lightColor = "#ffffff";

        QrCode qr = QrCode.EncodeText(joinUrl, QrCode.Ecc.Medium);
        int dimension = qr.Size + (quietZoneModules * 2);
        string path = qr.ToGraphicsPath(quietZoneModules);
        string label = HtmlEncoder.Default.Encode("Table join QR code");

        return
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {dimension} {dimension}\" "
            + $"role=\"img\" aria-label=\"{label}\" class=\"join-qr-svg\" shape-rendering=\"crispEdges\">"
            + $"<rect width=\"{dimension}\" height=\"{dimension}\" fill=\"{lightColor}\"/>"
            + $"<path d=\"{path}\" fill=\"{darkColor}\"/>"
            + "</svg>";
    }
}
