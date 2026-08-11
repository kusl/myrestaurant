using Testcontainers.PostgreSql;
using Xunit;

namespace MyRestaurant.DataAccess.Tests;

/// <summary>
/// Starts one PostgreSQL 17 container for the whole test class (Testcontainers). If the container
/// cannot start, <see cref="SkipReason"/> is set so the tests skip rather than fail
/// (BUILD_PROGRESS: container-dependent tests).
///
/// <para>Endpoint discovery order: explicit configuration (<c>DOCKER_HOST</c>,
/// <c>~/.testcontainers.properties</c>) → the Docker default socket → the rootless Podman user
/// socket, which <see cref="ContainerEngineDiscovery"/> wires up automatically when it exists. On a
/// Podman host where the socket has never been activated, the skip reason below spells out the
/// one-time fix instead of only echoing Testcontainers' Docker-flavoured error.</para>
///
/// <para><b>The image reference is fully qualified, and that is a correctness requirement rather
/// than a style choice</b> (TECHNICAL_SPECIFICATION §14.1, <b>F-60</b>). Testcontainers does not
/// normalise it: <c>MatchImage.Match</c> parses a reference and records a registry only when the
/// first slash-separated segment contains a <c>.</c> or a <c>:</c>, and its own comment says it
/// "does not resolve or set the default domain and repository prefix". So <c>postgres:17-alpine</c>
/// reaches the engine as a short name, and a short name is resolved through
/// <c>unqualified-search-registries</c> — which Fedora's <c>containers-common</c> populates and a
/// stock Debian ships commented out. That is F-51's mechanism exactly, one layer over: on the
/// canonical host the pull fails, this fixture catches it, and every integration test in this
/// assembly skips while the suite reports success.</para>
///
/// <para><b>Why the reference is a constant rather than an argument.</b> Written inline at the
/// builder call it is a reference nothing can audit — the tree-wide check in
/// <c>ContainerImageReferenceContractTests</c> reads YAML <c>image:</c> keys,
/// <c>Containerfile</c>'s <c>FROM</c> operands, and values assigned to a name ending in
/// <c>_IMAGE</c> or <c>Image</c>, and a literal spelled anywhere else is outside every gate this
/// project has. Naming it is what puts it back in scope, and it is the same shape
/// <c>RestaurantHarness</c> has always used.</para>
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    /// <summary>
    /// The same reference <c>compose.yaml</c> gives the <c>postgres</c> service, character for
    /// character. Not a coincidence and not a copy to be maintained by memory: the contract test
    /// asserts that every image name in this repository resolves to exactly one reference, so this
    /// and the canonical stack cannot drift apart in version or in qualification.
    /// </summary>
    private const string PostgreSqlImage = "docker.io/library/postgres:17-alpine";

    /// <summary>
    /// Fragments a container engine uses when it cannot turn an image reference into a registry.
    /// Matched case-insensitively against the whole exception chain, because the wording differs
    /// between Podman's own error and the Docker-compatible API's relay of it.
    /// </summary>
    private static readonly string[] UnresolvableReferenceMarkers =
    [
        "short-name",
        "unqualified-search",
        "did not resolve to an alias",
    ];

    private PostgreSqlContainer? _container;

    /// <summary>The Npgsql connection string once the container is up; otherwise <c>null</c>.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Non-null when the container could not start; the reason to pass to <c>Assert.Skip</c>.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            // Build here rather than in a field initializer: PostgreSqlBuilder.Build() validates
            // container-engine connectivity eagerly, so constructing the container in the field
            // initializer (i.e. the fixture ctor) would throw a DockerUnavailableException BEFORE
            // this try/catch — reporting every test as a "class fixture threw in its constructor"
            // failure instead of the intended skip.
            _container = new PostgreSqlBuilder(PostgreSqlImage)
                .WithDatabase("myrestaurant")
                .WithUsername("myrestaurant")
                .WithPassword("myrestaurant")
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            SkipReason = DescribeFailure(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Names what actually went wrong rather than the most common thing that goes wrong (<b>F-60</b>).
    /// Every unavailability used to be reported as "a container engine was not reachable", with a
    /// remediation about activating the Podman socket — so an operator whose engine was reachable and
    /// whose *image reference* was unresolvable was told to fix something that was not broken,
    /// re-ran, and got the identical sentence. The engine's own text was in there, three clauses
    /// down, and the headline contradicted it.
    ///
    /// <para>Both branches name the image, which the previous message omitted entirely and which is
    /// the single most useful fact when a pull is what failed.</para>
    /// </summary>
    private static string DescribeFailure(Exception exception)
    {
        string detail = Flatten(exception);

        if (MentionsUnresolvableReference(detail))
        {
            return
                $"The image reference '{PostgreSqlImage}' could not be resolved to a registry by this"
                + " container engine: " + detail
                + " — this is not an unreachable engine. A reference without a registry component is"
                + " resolved through `unqualified-search-registries`, which a stock Debian ships"
                + " commented out (F-51, F-60). The reference above is fully qualified, so if this is"
                + " what failed then the engine could not reach the registry it names, or the local"
                + " store has the image under a different name. Check network egress to that"
                + " registry, or pre-pull it by hand.";
        }

        return
            "A container engine (Podman/Docker) was not reachable: " + exception.Message
            + $" — the image this fixture starts is '{PostgreSqlImage}'. On a rootless-Podman host,"
            + " activate the user API socket once with `systemctl --user enable --now podman.socket`"
            + " and re-run; the tests discover it automatically. (Explicit configuration also works:"
            + " `export DOCKER_HOST=unix:///run/user/$(id -u)/podman/podman.sock`.)";
    }

    private static bool MentionsUnresolvableReference(string detail)
    {
        foreach (string marker in UnresolvableReferenceMarkers)
        {
            if (detail.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The whole exception chain's text. Testcontainers wraps the engine's response, so the sentence
    /// that names the cause is routinely on an inner exception rather than on the one thrown.
    /// </summary>
    private static string Flatten(Exception exception)
    {
        List<string> messages = [];

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Length > 0)
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" | ", messages);
    }
}
