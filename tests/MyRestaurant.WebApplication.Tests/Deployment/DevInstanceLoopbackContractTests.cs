using System.Globalization;
using Xunit;

namespace MyRestaurant.WebApplication.Tests.Deployment;

/// <summary>
/// Every helper that dials the web application dials the address <c>compose.yaml</c> actually
/// publishes, written as an address literal (TECHNICAL_SPECIFICATION §14.3a, §16.4, <b>F-56</b>).
///
/// <para><b>Why this exists.</b> <c>compose.yaml</c> publishes <c>web</c> as
/// <c>127.0.0.1:8080:8080</c> — one address, IPv4, and nothing listening on <c>::1</c>. Both tunnel
/// helpers defaulted <c>TUNNEL_TARGET</c> to <c>http://localhost:8080</c>, and that single value is
/// dialled by three separate clients: <c>cloudflared</c>, and then whichever of <c>curl</c> or
/// <c>wget</c> the host has. <c>curl</c> and GNU <c>wget</c> try the next address when the first
/// refuses; BusyBox <c>wget</c> does not — and it is the second entry in the probe chain of a script
/// whose entire premise is a host that may not have <c>curl</c>. The visible cost is worse than the
/// risk and is what made the finding reachable: <c>cloudflared</c> reports the address it failed on,
/// so a tunnel log fills with <c>dial tcp [::1]:8080: connect: connection refused</c> and an operator
/// goes looking for an IPv6 misconfiguration that does not exist.</para>
///
/// <para><b>Why the subject is derived and not listed.</b> This is F-50's pattern rather than a grep
/// for a hostname: <c>compose.yaml</c>'s published port is the authoritative statement of where the
/// application can be reached on this host, and each helper's <c>TUNNEL_TARGET</c> default is a
/// restatement of it. So the expected host and port are read out of the compose file and the
/// restatements are checked against them. Changing the published port and forgetting a helper fails
/// this file by name.</para>
///
/// <para><b>Scope, stated so the gaps are deliberate.</b> This asserts what a <em>program</em> dials,
/// which in each helper is exactly one variable's default. It deliberately does <em>not</em> assert
/// that no script mentions <c>localhost</c>: <c>run.sh</c> prints <c>http://localhost:8080</c> in a
/// sentence telling a human what to open in a browser, which is correct — browsers resolve both
/// addresses and try both — and failing on it would be reporting a finding on a correct tree (F-41).
/// It also says nothing about whether the port is free or whether the stack starts; those are
/// behavioural questions about a container engine and belong to a CI job on a Podman host.</para>
///
/// <para><b>What a shape change does here, deliberately.</b> The compose scan reads the block form
/// this file uses; rewriting <c>ports:</c> as a flow sequence, or renaming the helpers' variable,
/// fails the non-vacuity fact rather than passing quietly. That is the intended behaviour and the
/// reason that fact runs first: this test is teachable and its silence is not evidence. It differs
/// from <c>ComposeDependencyContractTests</c> accepting the list form of <c>depends_on</c> — there,
/// both engines normalise the two forms to the same meaning, so failing would report a finding on a
/// correct file; here, an unread port mapping means the address was never checked at all.</para>
///
/// <para>Pure: reads three files off the disk it was built from. No server, no container, no engine.</para>
/// </summary>
public sealed class DevInstanceLoopbackContractTests
{
    private const string SolutionFileName = "MyRestaurant.slnx";
    private const string ComposeRelativePath = "compose.yaml";

    /// <summary>The helpers whose <c>TUNNEL_TARGET</c> default must agree with the published port.</summary>
    private static readonly string[] HelperRelativePaths =
    [
        "scripts/dev_instance.sh",
        "scripts/quick_tunnel.sh",
    ];

    /// <summary>
    /// The scan read the published port and both helpers' defaults. Asserted first and on its own,
    /// because every assertion below it is satisfied by an empty set (<b>F-41</b>) — a renamed script
    /// or a re-indented <c>ports:</c> block would otherwise turn this whole file green by finding
    /// nothing at all.
    /// </summary>
    [Fact]
    public void TheScanFindsThePublishedPortAndEveryHelperDefault()
    {
        PublishedAddress published = ReadPublishedWebAddress();

        Assert.False(
            string.IsNullOrEmpty(published.Host),
            $"No published host was read out of {ComposeRelativePath}'s web service. §14.1 publishes"
            + " the web port on loopback; if that block moved, this scan has to follow it rather than"
            + " be deleted.");

        List<HelperTarget> targets = ReadHelperTargets();

        Assert.Equal(HelperRelativePaths.Length, targets.Count);

        foreach (HelperTarget target in targets)
        {
            Assert.False(
                string.IsNullOrEmpty(target.Host),
                $"No TUNNEL_TARGET default was read out of {target.Path}. The assignment this test"
                + " reads is the only place that script decides what to dial, so a shape it no longer"
                + " matches is a change this test has to be taught, not one it may ignore.");
        }
    }

    /// <summary>
    /// <b>This is F-56.</b> Each helper dials the host and port <c>compose.yaml</c> publishes, and
    /// nothing else — because a helper pointed at an address the stack does not publish fails in a
    /// vocabulary that names neither file.
    /// </summary>
    [Fact]
    public void EveryHelperDialsThePublishedAddress()
    {
        PublishedAddress published = ReadPublishedWebAddress();

        foreach (HelperTarget target in ReadHelperTargets())
        {
            Assert.True(
                string.Equals(target.Host, published.Host, StringComparison.Ordinal)
                && string.Equals(target.Port, published.Port, StringComparison.Ordinal),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{target.Path} dials '{target.Host}:{target.Port}' by default, and"
                    + $" {ComposeRelativePath} publishes the web port on '{published.Host}:{published.Port}'."
                    + " Those have to be the same address: the published port is the only one that"
                    + " exists on the host, and a helper that dials anything else gets a connection"
                    + " refused naming an address nobody configured."));
        }
    }

    /// <summary>
    /// Each default names an address literal rather than a hostname. The finding itself: a name that
    /// resolves to <c>::1</c> first works only if every client dialling it falls back to the second
    /// address, and BusyBox <c>wget</c> — the second entry in the helper's own probe chain — does not.
    /// </summary>
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

    /// <summary>
    /// What is published is a loopback address. This is the reason the rule above exists — one
    /// address, no listener on <c>::1</c> — so if the port is ever published on <c>0.0.0.0</c> the
    /// justification is gone and the rule should be re-argued rather than silently kept. A test that
    /// stayed green through that change would be asserting a coincidence.
    /// </summary>
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

    // ---------------------------------------------------------------------------------------------
    // Reading the three files. Plain string work, no parser and no regular expressions — the same
    // choice ConfigurationSurfaceTests and ComposeDependencyContractTests make about this same
    // compose file, and for the same reason: a YAML package in the unit test project would be a
    // dependency taken on to read indentation.
    // ---------------------------------------------------------------------------------------------

    private sealed record PublishedAddress(string Host, string Port);

    private sealed record HelperTarget(string Path, string Host, string Port);

    /// <summary>
    /// The host and container-side port of the <c>web</c> service's published port. The shape being
    /// read, which is the whole of compose's schema that matters here:
    /// <code>
    /// services:
    ///   web:
    ///     ports:
    ///       - "127.0.0.1:8080:8080"
    /// </code>
    /// </summary>
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
                // "8080:8080" — no host component, so every interface. Reported as such rather than
                // guessed at; ThePublishedAddressIsStillLoopback is the assertion that fails on it.
                return new PublishedAddress("0.0.0.0", parts[1]);
            }
        }

        return new PublishedAddress(string.Empty, string.Empty);
    }

    /// <summary>
    /// The host and port each helper dials by default, read out of its single
    /// <c>TARGET="${TUNNEL_TARGET:-http://host:port}"</c>-shaped assignment. The variable name differs
    /// between the two scripts, so the anchor is the parameter expansion rather than the assignment's
    /// left-hand side.
    /// </summary>
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
                    // A comment restating the default is documentation, not the thing that is dialled.
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

    /// <summary>The text before the first colon, or empty when the line is not 'name:'.</summary>
    private static string NameBeforeColon(string content)
    {
        int colon = content.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? string.Empty : content[..colon].Trim();
    }

    /// <summary>The first line equal to <paramref name="value"/> at or after <paramref name="from"/>.</summary>
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

    /// <summary>
    /// The first line at or after <paramref name="from"/> whose indentation is exactly
    /// <paramref name="indent"/> spaces and which carries content — i.e. where the enclosing block
    /// ends. Returns the line count when the block runs to the end of the file. The same walk
    /// <c>ComposeDependencyContractTests</c> uses on this file, deliberately.
    /// </summary>
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

    /// <summary>
    /// The same walk up to <c>MyRestaurant.slnx</c> the other contract tests use, and it fails
    /// rather than skips for the same reason: a check that quietly declines to run is worse than none.
    /// </summary>
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
