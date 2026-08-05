using System.Reflection;
using MyRestaurant.WebApplication.Configuration;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// The build stamp (TECHNICAL_SPECIFICATION §11.9, §12). <see cref="BuildInformation"/> is the last
/// link in a chain that starts at a container build argument and ends on the <c>/source</c> page and
/// in OpenTelemetry's <c>service.version</c>, and every earlier link is MSBuild — which cannot be
/// asserted on from here. What can be asserted on is the parse, exhaustively, including the shapes
/// nobody intends to produce: a build that was told nothing must say so rather than invent a
/// revision, because "not recorded" is a reading somebody can act on and a wrong hash is not.
///
/// <para>The chain above this is gated in CI instead: <c>boot-smoke</c> curls <c>/source</c> on the
/// booted image and fails unless the response contains the commit it was built from (§16.4).</para>
/// </summary>
public sealed class BuildInformationTests
{
    [Fact]
    public void FromInformationalVersion_PlainVersion_HasNoRevision()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("1.0.0");

        Assert.Equal("1.0.0", build.Version);
        Assert.Null(build.SourceRevision);
        Assert.False(build.HasSourceRevision);
        Assert.Equal(string.Empty, build.ShortSourceRevision);
        Assert.Equal("1.0.0", build.InformationalVersion);
        Assert.Equal("1.0.0", build.Display);
    }

    [Fact]
    public void FromInformationalVersion_VersionWithRevision_SplitsAtThePlus()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion(
            "1.2.3+3f2a9c1e4b7d8a6f5c3e2d1b0a9f8e7d6c5b4a39");

        Assert.Equal("1.2.3", build.Version);
        Assert.Equal("3f2a9c1e4b7d8a6f5c3e2d1b0a9f8e7d6c5b4a39", build.SourceRevision);
        Assert.True(build.HasSourceRevision);
        Assert.Equal("3f2a9c1", build.ShortSourceRevision);
        Assert.Equal("1.2.3 (3f2a9c1)", build.Display);
    }

    /// <summary>
    /// A prerelease label contains a hyphen and belongs to the version, not to the metadata. Splitting
    /// on the wrong character would silently turn <c>1.1.0-rc.1</c> into <c>1.1.0</c> and publish a
    /// release candidate that claimed to be the release.
    /// </summary>
    [Fact]
    public void FromInformationalVersion_PrereleaseLabel_StaysPartOfTheVersion()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("1.1.0-rc.1+abcdef1234");

        Assert.Equal("1.1.0-rc.1", build.Version);
        Assert.Equal("abcdef1234", build.SourceRevision);
        Assert.Equal("abcdef1", build.ShortSourceRevision);
        Assert.Equal("1.1.0-rc.1 (abcdef1)", build.Display);
    }

    /// <summary>
    /// SemVer allows dot-separated build metadata, and the SDK's own
    /// <c>AddSourceRevisionToInformationalVersion</c> appends <c>.$(SourceRevisionId)</c> when a
    /// <c>+</c> is already present. Everything after the first <c>+</c> is therefore kept: two
    /// segments means somebody stamped two facts, and dropping either would be this parse forming an
    /// opinion about which one mattered.
    /// </summary>
    [Fact]
    public void FromInformationalVersion_MultipleMetadataSegments_KeepsAllOfThem()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("1.0.0+build.42.abcdef1");

        Assert.Equal("1.0.0", build.Version);
        Assert.Equal("build.42.abcdef1", build.SourceRevision);
    }

    /// <summary>A non-hexadecimal revision is not abbreviated — a fork may stamp a tag or a build number.</summary>
    [Fact]
    public void ShortSourceRevision_NonHexadecimalRevision_IsNotTruncated()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("1.0.0+nightly-2026-08-04");

        Assert.Equal("nightly-2026-08-04", build.ShortSourceRevision);
    }

    /// <summary>A hash at or under the abbreviation length is already short enough to leave alone.</summary>
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("abcdef1", "abcdef1")]
    [InlineData("abcdef12", "abcdef1")]
    public void ShortSourceRevision_TruncatesOnlyWhatIsLongerThanSeven(string revision, string expected)
    {
        BuildInformation build = BuildInformation.FromInformationalVersion($"1.0.0+{revision}");

        Assert.Equal(expected, build.ShortSourceRevision);
    }

    /// <summary>
    /// The shapes an unstamped or half-stamped build can produce. None of them may yield a revision:
    /// a trailing <c>+</c> is exactly what a container build with an empty <c>SOURCE_REVISION</c>
    /// argument would leave behind if the Containerfile's conditional expansion were ever broken, and
    /// it must read as "not recorded" rather than as an empty-string revision that renders as a blank
    /// <c>&lt;code&gt;</c> element on the source page.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromInformationalVersion_Missing_ReportsUnknownWithNoRevision(string? informationalVersion)
    {
        BuildInformation build = BuildInformation.FromInformationalVersion(informationalVersion);

        Assert.Equal(BuildInformation.UnknownVersion, build.Version);
        Assert.Null(build.SourceRevision);
        Assert.False(build.HasSourceRevision);
    }

    [Theory]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+   ")]
    public void FromInformationalVersion_EmptyMetadata_IsNotARevision(string informationalVersion)
    {
        BuildInformation build = BuildInformation.FromInformationalVersion(informationalVersion);

        Assert.Equal("1.0.0", build.Version);
        Assert.Null(build.SourceRevision);
        Assert.Equal("1.0.0", build.InformationalVersion);
    }

    [Fact]
    public void FromInformationalVersion_MetadataWithNoVersion_ReportsUnknown()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("+abcdef1");

        Assert.Equal(BuildInformation.UnknownVersion, build.Version);
        Assert.Equal("abcdef1", build.SourceRevision);
    }

    /// <summary>
    /// The real assembly, read the real way. This does not assert a particular version — the whole
    /// point is that the value comes from the build — only that the attribute is present and parses,
    /// which is the failure mode that would leave every surface reading "unknown" in production while
    /// every other test here passed.
    /// </summary>
    [Fact]
    public void FromAssembly_ReadsTheStampOffARealAssembly()
    {
        Assembly assembly = typeof(RestaurantOptions).Assembly;

        BuildInformation build = BuildInformation.FromAssembly(assembly);

        Assert.NotEqual(BuildInformation.UnknownVersion, build.Version);
        Assert.False(string.IsNullOrWhiteSpace(build.Display));
    }

    [Fact]
    public void Current_IsTheWebApplicationAssembly()
    {
        Assert.Equal(
            BuildInformation.FromAssembly(typeof(RestaurantOptions).Assembly).InformationalVersion,
            BuildInformation.Current.InformationalVersion);
    }
}
