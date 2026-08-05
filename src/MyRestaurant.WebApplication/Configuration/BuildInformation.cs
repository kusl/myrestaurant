using System.Reflection;

namespace MyRestaurant.WebApplication.Configuration;

/// <summary>
/// What this build is (TECHNICAL_SPECIFICATION §11.9, §12). One parsed reading of the entry
/// assembly's <see cref="AssemblyInformationalVersionAttribute"/>, exposed as a version and — when
/// the build was told one — the source revision it was produced from.
///
/// <para><b>Why this exists.</b> Until the first tag, "which build is this box running?" had no
/// answer an operator could obtain from the running instance: the image carries whatever tag was
/// typed at deploy time, the assembly reported the SDK's default <c>1.0.0</c>, and every trace and
/// metric leaving the process was unversioned. That is tolerable while one person deploys by hand
/// and remembers; it stops being tolerable the moment images are published for other people to run.
/// It also matters for a second, licence-shaped reason: AGPL §13 offers the Corresponding Source
/// <em>of the version being interacted with</em>, and an offer that cannot name the revision is
/// approximate (§11.9, F-39).</para>
///
/// <para><b>How the stamp gets here.</b> The .NET SDK writes
/// <see cref="AssemblyInformationalVersionAttribute"/> from the MSBuild <c>InformationalVersion</c>
/// property, which defaults to <c>Version</c>, which defaults to <c>VersionPrefix</c> —
/// <c>Directory.Build.props</c> sets that. The SDK also appends <c>+$(SourceRevisionId)</c>
/// automatically, but <em>only</em> when <c>SourceControlInformationFeatureSupported</c> is true,
/// which is set by SourceLink and by nothing else in the SDK. Rather than take a package dependency
/// for one string, the <c>Containerfile</c> passes <c>InformationalVersion</c> explicitly from its
/// <c>VERSION</c> and <c>SOURCE_REVISION</c> build arguments, and the workflows fill those in. A
/// build nobody told is honest about it rather than guessing: <see cref="SourceRevision"/> is null
/// and the surfaces say so.</para>
/// </summary>
public sealed class BuildInformation
{
    /// <summary>What <see cref="Version"/> reads when the assembly carries no informational version at all.</summary>
    public const string UnknownVersion = "unknown";

    /// <summary>The number of leading characters of a commit hash shown in the short form.</summary>
    private const int ShortRevisionLength = 7;

    /// <summary>
    /// This build, read once from the assembly this type is compiled into. A static rather than a
    /// registered service because it cannot change while the process lives and because the layout
    /// components that render it are not the sort of thing that should need a constructor injection
    /// to say what version they are.
    /// </summary>
    public static BuildInformation Current { get; } = FromAssembly(typeof(BuildInformation).Assembly);

    /// <summary>The version without build metadata — <c>1.0.0</c>, or <c>1.1.0-rc.1</c>.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// The source revision this build was produced from, or <see langword="null"/> when the build was
    /// not told one. Null is a real answer and is rendered as such; a fabricated hash would be worse
    /// than an admitted gap, because it is the one field somebody would act on.
    /// </summary>
    public string? SourceRevision { get; init; }

    /// <summary>True when this build knows the revision it came from.</summary>
    public bool HasSourceRevision => !string.IsNullOrEmpty(SourceRevision);

    /// <summary>
    /// The revision abbreviated for display: the first seven characters of a hexadecimal hash, or the
    /// value unchanged when it is not one (a tag name, a build number — a fork may stamp anything).
    /// Empty when there is no revision.
    /// </summary>
    public string ShortSourceRevision
    {
        get
        {
            if (!HasSourceRevision)
            {
                return string.Empty;
            }

            string revision = SourceRevision!;
            bool looksLikeHash = revision.Length > ShortRevisionLength && revision.All(char.IsAsciiHexDigit);
            return looksLikeHash ? revision[..ShortRevisionLength] : revision;
        }
    }

    /// <summary>
    /// The full informational version as stamped — version plus <c>+revision</c> when there is one.
    /// This is what goes into the OpenTelemetry <c>service.version</c> resource attribute (§12), so a
    /// collector can attribute a latency change to a deployment rather than to the weather.
    /// </summary>
    public string InformationalVersion => HasSourceRevision ? $"{Version}+{SourceRevision}" : Version;

    /// <summary>
    /// One line for a person: <c>1.0.0 (3f2a9c1)</c>, or just <c>1.0.0</c> when the revision is unknown.
    /// </summary>
    public string Display => HasSourceRevision ? $"{Version} ({ShortSourceRevision})" : Version;

    /// <summary>Reads the stamp off <paramref name="assembly"/>.</summary>
    public static BuildInformation FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return FromInformationalVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <summary>
    /// Splits a SemVer 2.0 informational version at its build-metadata separator. Pure, so the
    /// parsing rules are unit-tested without a build that has to be stamped a particular way.
    ///
    /// <para>Everything after the <em>first</em> <c>+</c> is the revision. That is deliberate rather
    /// than lazy: SemVer allows dot-separated metadata identifiers, and the SDK's own
    /// <c>AddSourceRevisionToInformationalVersion</c> appends <c>.$(SourceRevisionId)</c> when a
    /// <c>+</c> is already present — so a second segment means somebody stamped two facts, and
    /// discarding either one would be this method inventing an opinion about which mattered.</para>
    /// </summary>
    public static BuildInformation FromInformationalVersion(string? informationalVersion)
    {
        string value = (informationalVersion ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return new BuildInformation { Version = UnknownVersion, SourceRevision = null };
        }

        int separator = value.IndexOf('+', StringComparison.Ordinal);
        if (separator < 0)
        {
            return new BuildInformation { Version = value, SourceRevision = null };
        }

        string version = value[..separator].Trim();
        string revision = value[(separator + 1)..].Trim();

        return new BuildInformation
        {
            Version = version.Length == 0 ? UnknownVersion : version,
            SourceRevision = revision.Length == 0 ? null : revision,
        };
    }
}
