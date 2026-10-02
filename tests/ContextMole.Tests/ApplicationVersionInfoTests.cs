using System.Reflection;

using ContextMole.App.UI;
using ContextMole.App.UI.ViewModels;

namespace ContextMole.Tests;

public sealed class ApplicationVersionInfoTests
{
    [Theory]
    [InlineData("0.2.9", "Version 0.2.9")]
    [InlineData("0.2.9+9970ef8592e00a2c5efcad802de67b3aa77bc20e", "Version 0.2.9")]
    [InlineData("0.3.0-preview.2+9970ef8592e00a2c5efcad802de67b3aa77bc20e", "Version 0.3.0-preview.2")]
    [InlineData("0.3.0-dev+local", "Version 0.3.0-dev")]
    [InlineData("1.0.0+local", "Version 1.0.0")]
    [InlineData(" 0.3.0-dev.7+branch.commit ", "Version 0.3.0-dev.7")]
    public void InformationalVersionTakesPrecedenceAndHidesBuildMetadata(string version, string expected)
    {
        Assert.Equal(expected, ApplicationVersionInfo.FormatDisplayLabel(version, new Version(9, 8, 7, 6), "Version"));
    }

    [Theory]
    [InlineData("1.0.0", "Development")]
    [InlineData("1.0.0+9970ef8592e00a2c5efcad802de67b3aa77bc20e", "Development")]
    [InlineData("1.0.0-dev+local", "Development")]
    [InlineData("1.0.0-dev+local", "VersionSuffix")]
    [InlineData("1.0.0", null)]
    [InlineData("1.0.0", "")]
    [InlineData("1.0.0", " ")]
    [InlineData("0.2.9+commit", "Unknown")]
    public void SdkDefaultOrUnprovenVersionIsClearlyADevelopmentBuild(string version, string? source)
    {
        Assert.Equal("Development build", ApplicationVersionInfo.FormatDisplayLabel(version, new Version(1, 0, 0, 0), source));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("VersionPrefix")]
    [InlineData("InformationalVersion")]
    public void ExplicitOnePointZeroIsNotMistakenForTheSdkDefault(string source)
    {
        Assert.Equal("Version 1.0.0", ApplicationVersionInfo.FormatDisplayLabel("1.0.0+local", new Version(1, 0, 0, 0), source));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("VersionPrefix")]
    [InlineData("InformationalVersion")]
    public void ExplicitPrereleaseVersionKeepsItsLabel(string source)
    {
        Assert.Equal("Version 0.3.0-dev.7", ApplicationVersionInfo.FormatDisplayLabel("0.3.0-dev.7+local", new Version(0, 3, 0, 0), source));
    }

    [Fact]
    public void TaggedBuildDisplaysTheRunningBinariesStampedVersion()
    {
        Assert.Equal("Version 0.2.9", ApplicationVersionInfo.FormatDisplayLabel(
            "0.2.9+9970ef8592e00a2c5efcad802de67b3aa77bc20e", new Version(0, 2, 9, 0), "Version"));
    }

    [Fact]
    public void ExplicitAssemblyVersionDoesNotUseDefaultInformationalVersion()
    {
        Assert.Equal("Version 0.2.9", ApplicationVersionInfo.FormatDisplayLabel("1.0.0+local", new Version(0, 2, 9, 0), "AssemblyVersion"));
        Assert.Equal("Version 1.0.0", ApplicationVersionInfo.FormatDisplayLabel("1.0.0+local", new Version(1, 0, 0, 0), "AssemblyVersion"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+commit-only")]
    public void MissingInformationalVersionFallsBackToAssemblyVersion(string? version)
    {
        Assert.Equal("Version 0.2.9", ApplicationVersionInfo.FormatDisplayLabel(version, new Version(0, 2, 9, 0), "Version"));
    }

    [Theory]
    [InlineData(2, 3, -1, -1, "Version 2.3")]
    [InlineData(2, 3, 4, -1, "Version 2.3.4")]
    [InlineData(2, 3, 4, 0, "Version 2.3.4")]
    [InlineData(2, 3, 4, 5, "Version 2.3.4.5")]
    public void AssemblyFallbackPreservesMeaningfulVersionComponents(
        int major, int minor, int build, int revision, string expected)
    {
        var version = build < 0 ? new Version(major, minor)
            : revision < 0 ? new Version(major, minor, build)
            : new Version(major, minor, build, revision);

        Assert.Equal(expected, ApplicationVersionInfo.FormatDisplayLabel(null, version, "AssemblyVersion"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+local")]
    public void UnversionedDevelopmentAssemblyGetsReadableFallback(string? version)
    {
        Assert.Equal("Development build", ApplicationVersionInfo.FormatDisplayLabel(version, null, "Development"));
        Assert.Equal("Development build", ApplicationVersionInfo.FormatDisplayLabel(version, null, "Version"));
    }

    [Fact]
    public void DisplayedVersionComesFromTheDesktopUiAssembly()
    {
        var uiAssembly = typeof(MainViewModel).Assembly;
        Assert.Same(uiAssembly, ApplicationVersionInfo.SourceAssembly);
        Assert.NotSame(typeof(ApplicationVersionInfoTests).Assembly, ApplicationVersionInfo.SourceAssembly);

        var informationalVersion = uiAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var provenance = Assert.Single(uiAssembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == ApplicationVersionInfo.BuildVersionSourceMetadataKey).Value;
        Assert.Contains(provenance, new[] { "Development", "Version", "VersionPrefix", "InformationalVersion", "AssemblyVersion" });
        Assert.DoesNotContain(typeof(ApplicationVersionInfoTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == ApplicationVersionInfo.BuildVersionSourceMetadataKey);

        var expected = ApplicationVersionInfo.FormatDisplayLabel(informationalVersion, uiAssembly.GetName().Version, provenance);
        Assert.Equal(expected, ApplicationVersionInfo.DisplayLabel);
        Assert.Equal(expected, ApplicationVersionInfo.FormatDisplayLabel(uiAssembly));
        Assert.DoesNotContain('+', ApplicationVersionInfo.DisplayLabel);
        if (provenance == "Development")
        {
            Assert.Equal("Development build", ApplicationVersionInfo.DisplayLabel);
        }
    }
}
