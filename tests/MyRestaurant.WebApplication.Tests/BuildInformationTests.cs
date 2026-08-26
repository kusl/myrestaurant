using System.Reflection;
using MyRestaurant.WebApplication.Configuration;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

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

    [Fact]
    public void FromInformationalVersion_PrereleaseLabel_StaysPartOfTheVersion()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("1.1.0-rc.1+abcdef1234");

        Assert.Equal("1.1.0-rc.1", build.Version);
        Assert.Equal("abcdef1234", build.SourceRevision);
        Assert.Equal("abcdef1", build.ShortSourceRevision);
        Assert.Equal("1.1.0-rc.1 (abcdef1)", build.Display);
    }

    [Fact]
    public void FromInformationalVersion_MultipleMetadataSegments_KeepsAllOfThem()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("1.0.0+build.42.abcdef1");

        Assert.Equal("1.0.0", build.Version);
        Assert.Equal("build.42.abcdef1", build.SourceRevision);
    }

    [Fact]
    public void ShortSourceRevision_NonHexadecimalRevision_IsNotTruncated()
    {
        BuildInformation build = BuildInformation.FromInformationalVersion("1.0.0+nightly-2026-08-04");

        Assert.Equal("nightly-2026-08-04", build.ShortSourceRevision);
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("abcdef1", "abcdef1")]
    [InlineData("abcdef12", "abcdef1")]
    public void ShortSourceRevision_TruncatesOnlyWhatIsLongerThanSeven(string revision, string expected)
    {
        BuildInformation build = BuildInformation.FromInformationalVersion($"1.0.0+{revision}");

        Assert.Equal(expected, build.ShortSourceRevision);
    }

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
