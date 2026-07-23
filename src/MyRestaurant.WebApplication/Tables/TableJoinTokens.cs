using System.Text.Encodings.Web;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.Domain.Security;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Observability;
using Net.Codecrete.QrCodeGenerator;

namespace MyRestaurant.WebApplication.Tables;

/// <summary>
/// Everything a surface needs to render a table's current rotating join code (TECHNICAL_SPECIFICATION
/// §4.3, §4.5, §11.5): the token itself, the scan URL it is embedded in, a ready-to-inline SVG QR, the
/// instant the code was generated, and the instant it next rotates (so a display — or a person — knows
/// when to refresh). The join secret is not here and never is (§4.1); this record carries only the
/// public, scannable artefacts derived from it.
/// </summary>
/// <param name="TableIdentifier">The table the code is for (§4.1).</param>
/// <param name="Token">The Base64Url HMAC token for the current window (§4.3).</param>
/// <param name="JoinUrl">The scan URL <c>{public-origin}/table/{table}?token={token}</c> (§4.3).</param>
/// <param name="QrCodeSvg">A self-contained inline SVG of <see cref="JoinUrl"/>, rendered server-side (§4.3).</param>
/// <param name="GeneratedAt">The UTC instant the token was computed (the current window's clock read).</param>
/// <param name="NextRotationAt">The UTC instant the token rotates — <c>(window_index+1) × rotation</c> (§4.3).</param>
public sealed record TableJoinQrCode(
    Guid TableIdentifier,
    string Token,
    string JoinUrl,
    string QrCodeSvg,
    DateTimeOffset GeneratedAt,
    DateTimeOffset NextRotationAt);

/// <summary>
/// The application-layer table join-token service (TECHNICAL_SPECIFICATION §4.3–§4.5). It is the one
/// place that reaches the server-only join secret (through <see cref="ITableJoinSecretReader"/>) and
/// turns it into the two things the rest of the app needs:
/// <list type="bullet">
///   <item><description><see cref="DescribeCurrentAsync"/> — the current QR for rendering: the counter/admin
///   fallback (§4.5) and, later, the paired display (§11.5).</description></item>
///   <item><description><see cref="ValidateAsync"/> — validation of a presented token for the join flow
///   (§4.4), recording the <c>table_join_tokens_validated_total{result}</c> metric (§12) on every attempt.</description></item>
/// </list>
///
/// <para>The token construction and validation themselves are the pure, vector-tested
/// <see cref="JoinTokenService"/> in the domain; this type only supplies the moving parts a static
/// function cannot: the per-table secret, the configured rotation window and public origin, the clock,
/// the metric, and the server-side QR rendering. Deactivating or removing a table removes its readable
/// secret (§4.1), so <see cref="DescribeCurrentAsync"/> returns <c>null</c> and <see cref="ValidateAsync"/>
/// returns <see cref="JoinTokenValidationResult.Invalid"/> for it — the token cannot be honoured, which
/// is exactly the <c>invalid</c> metric bucket.</para>
///
/// <para>Scoped, like the other table services: it holds no state and depends on
/// <see cref="ITableJoinSecretReader"/> (scoped) plus the singleton options, metrics, and clock.</para>
/// </summary>
public interface ITableJoinTokens
{
    /// <summary>
    /// The current join code for an active table, ready to render (§4.5, §11.5), or <c>null</c> when no
    /// active table has that identifier (§4.1). Reads the secret, computes the current-window token, and
    /// renders the QR — persists nothing.
    /// </summary>
    Task<TableJoinQrCode?> DescribeCurrentAsync(Guid tableIdentifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a presented token against a table's current and previous windows (§4.3, §4.4) and records
    /// the outcome in <c>table_join_tokens_validated_total{result}</c> (§12). A missing/inactive table, a
    /// malformed token, or a token older than the bounded lookback all yield
    /// <see cref="JoinTokenValidationResult.Invalid"/>; a match to a recent-but-older window yields
    /// <see cref="JoinTokenValidationResult.Expired"/> (for the metric label); the current or previous
    /// window yields <see cref="JoinTokenValidationResult.Valid"/>.
    /// </summary>
    Task<JoinTokenValidationResult> ValidateAsync(
        Guid tableIdentifier,
        string presentedToken,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITableJoinTokens" />
public sealed class TableJoinTokens : ITableJoinTokens
{
    // Metric label values for table_join_tokens_validated_total{result} (§4.3/§12). Declared once so the
    // spelling the OTLP pipeline sees is fixed in a single place.
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

        // No active table → no secret to check against → the token cannot be honoured (§4.1). Counted as
        // an invalid validation attempt, keeping the metric's label set to the §4.3 {valid|expired|invalid}.
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

    /// <summary>
    /// Renders a join URL to a self-contained, inline SVG QR (TECHNICAL_SPECIFICATION §4.3: server-side,
    /// no client calls) — the same approach as the authenticator QR (<c>TotpQrCode</c>): the modules come
    /// from <c>Net.Codecrete.QrCodeGenerator</c> and the element is composed by hand rather than via the
    /// library's <c>ToSvgString</c>, which emits an XML prolog/DOCTYPE unsuitable for inlining. A white
    /// background keeps it scannable, a four-module quiet zone is baked into the path and the viewBox, and
    /// the <c>aria-label</c> is fixed text so the URL cannot inject markup.
    /// </summary>
    private static string RenderJoinQrSvg(string joinUrl)
    {
        const int quietZoneModules = 4;
        const string darkColor = "#16202b";  // --ink
        const string lightColor = "#ffffff"; // --surface-raised

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
