using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

public sealed class DevInstanceLoopbackContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComposeRelativePath = "compose.yaml";

    private static readonly string[] HelperRelativePaths =
    [
        "scripts/dev_instance.sh",
        "scripts/quick_tunnel.sh",
    ];

    [Fact]
    public void TheScanFindsThePublishedPortAndEveryHelperDefault()
    {
        PublishedAddress published = ReadPublishedWebAddress();

        Assert.True(
            published.Host.Length > 0,
            $"No published host was read out of {ComposeRelativePath}'s web service. §14.1 publishes"
            + $" the web port on loopback; if that block moved, this scan has to follow it rather than"
            + $" be deleted.");

        List<HelperTarget> targets = ReadHelperTargets();

        Assert.True(
            targets.Count == HelperRelativePaths.Length,
            $"Read {targets.Count} helper(s) where {HelperRelativePaths.Length} are expected.");

        foreach (HelperTarget target in targets)
        {
            Assert.True(
                target.Host.Length > 0,
                $"No TUNNEL_TARGET default was read out of {target.Path}. The assignment this test"
                + $" reads is the only place that script decides what to dial, so a shape it no longer"
                + $" matches is a change this test has to be taught, not one it may ignore.");
        }
    }

    [Fact]
    public void EveryHelperDialsThePublishedAddress()
    {
        PublishedAddress published = ReadPublishedWebAddress();

        foreach (HelperTarget target in ReadHelperTargets())
        {
            Assert.True(
                string.Equals(target.Host, published.Host, StringComparison.Ordinal)
                && string.Equals(target.Port, published.Port, StringComparison.Ordinal),
                $"{target.Path} dials '{target.Host}:{target.Port}' by default, and"
                + $" {ComposeRelativePath} publishes the web port on '{published.Host}:{published.Port}'."
                + $" Those have to be the same address: the published port is the only one that exists"
                + $" on the host, and a helper that dials anything else gets a connection refused"
                + $" naming an address nobody configured.");
        }
    }

    [Fact]
    public void EveryHelperDialsAnAddressLiteralRatherThanAName()
    {
        foreach (HelperTarget target in ReadHelperTargets())
        {
            Assert.True(
                System.Net.IPAddress.TryParse(target.Host, out _),
                $"{target.Path} dials the host '{target.Host}', which is a name rather than an address."
                + $" {ComposeRelativePath} publishes the web port on one address and nothing listens on"
                + " ::1, so a name that resolves to ::1 first is a dependency on every client falling"
                + " back — curl and GNU wget do, BusyBox wget does not — and cloudflared reports the"
                + " failed address, which sends an operator after an IPv6 problem that is not there."
                + " That is F-56. run.sh has dialled the literal since M1.");
        }
    }

    [Fact]
    public void ThePublishedAddressIsStillLoopback()
    {
        PublishedAddress published = ReadPublishedWebAddress();

        Assert.True(
            System.Net.IPAddress.TryParse(published.Host, out System.Net.IPAddress? address)
            && System.Net.IPAddress.IsLoopback(address),
            $"{ComposeRelativePath} publishes the web port on '{published.Host}', which is not a"
            + " loopback address. §14.1 publishes it on loopback only, because the application trusts"
            + " X-Forwarded-* headers and is meant to be reached through the proxy — and §14.3a's"
            + " address-literal rule is argued from there being exactly one published address. If this"
            + " changed deliberately, both statements need revisiting; if it did not, this is the"
            + " finding.");
    }

    private sealed record PublishedAddress(string Host, string Port);

    private sealed record HelperTarget(string Path, string Host, string Port);

    private static PublishedAddress ReadPublishedWebAddress()
    {
        string[] lines = ReadRepositoryFile(ComposeRelativePath).Split('\n');

        int servicesStart = IndexOfLine(lines, "services:", 0);
        if (servicesStart < 0)
        {
            throw new InvalidOperationException(
                $"{ComposeRelativePath} has no line 'services:'. Everything this test reads is a child"
                + " of it.");
        }

        int servicesEnd = IndexOfIndent(lines, servicesStart + 1, 0);

        string currentService = "";
        bool insidePorts = false;

        for (int index = servicesStart + 1; index < servicesEnd; index++)
        {
            string line = lines[index].TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int indent = IndentOf(line);
            string content = line[indent..];

            if (indent == 2)
            {
                currentService = NameBeforeColon(content);
                insidePorts = false;
                continue;
            }

            if (indent == 4)
            {
                insidePorts = string.Equals(content, "ports:", StringComparison.Ordinal);
                continue;
            }

            if (!insidePorts
                || !string.Equals(currentService, "web", StringComparison.Ordinal)
                || !content.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            string mapping = content[2..].Trim().Trim('"', '\'');
            string[] parts = mapping.Split(':');
            if (parts.Length == 3)
            {
                return new PublishedAddress(parts[0], parts[2]);
            }

            if (parts.Length == 2)
            {
                return new PublishedAddress("0.0.0.0", parts[1]);
            }
        }

        return new PublishedAddress(string.Empty, string.Empty);
    }

    private static List<HelperTarget> ReadHelperTargets()
    {
        const string targetAnchor = "${TUNNEL_TARGET:-";
        List<HelperTarget> targets = [];

        foreach (string relativePath in HelperRelativePaths)
        {
            string host = string.Empty;
            string port = string.Empty;

            foreach (string rawLine in ReadRepositoryFile(relativePath).Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.StartsWith('#'))
                {
                    continue;
                }

                int anchorIndex = line.IndexOf(targetAnchor, StringComparison.Ordinal);
                if (anchorIndex < 0)
                {
                    continue;
                }

                string remainder = line[(anchorIndex + targetAnchor.Length)..];
                int close = remainder.IndexOf('}', StringComparison.Ordinal);
                if (close <= 0)
                {
                    continue;
                }

                string url = remainder[..close];
                const string scheme = "http://";
                if (!url.StartsWith(scheme, StringComparison.Ordinal))
                {
                    continue;
                }

                string authority = url[scheme.Length..].TrimEnd('/');
                int colon = authority.LastIndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                host = authority[..colon];
                port = authority[(colon + 1)..];
                break;
            }

            targets.Add(new HelperTarget(relativePath, host, port));
        }

        return targets;
    }

    private static int IndentOf(string line)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent;
    }

    private static string NameBeforeColon(string content)
    {
        int colon = content.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? string.Empty : content[..colon].Trim();
    }

    private static int IndexOfLine(string[] lines, string value, int from)
    {
        for (int index = from; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].TrimEnd('\r'), value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfIndent(string[] lines, int from, int indent)
    {
        for (int index = from; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (line.Length <= indent)
            {
                continue;
            }

            if (indent > 0 && !line.StartsWith(new string(' ', indent), StringComparison.Ordinal))
            {
                continue;
            }

            if (line[indent] != ' ' && line[indent] != '#')
            {
                return index;
            }
        }

        return lines.Length;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' does not exist. The repository root was found but its layout is not the one"
                + " §2 describes.");
        }

        return File.ReadAllText(path);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Walked up from '{AppContext.BaseDirectory}' without finding {SolutionFileName}.");
    }
}
