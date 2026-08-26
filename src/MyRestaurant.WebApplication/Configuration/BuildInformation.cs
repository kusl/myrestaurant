using System.Reflection;

namespace MyRestaurant.WebApplication.Configuration;

public sealed class BuildInformation
{
    public const string UnknownVersion = "unknown";

    private const int ShortRevisionLength = 7;

    public static BuildInformation Current { get; } = FromAssembly(typeof(BuildInformation).Assembly);

    public required string Version { get; init; }

    public string? SourceRevision { get; init; }

    public bool HasSourceRevision => !string.IsNullOrEmpty(SourceRevision);

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

    public string InformationalVersion => HasSourceRevision ? $"{Version}+{SourceRevision}" : Version;

    public string Display => HasSourceRevision ? $"{Version} ({ShortSourceRevision})" : Version;

    public static BuildInformation FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return FromInformationalVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

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
