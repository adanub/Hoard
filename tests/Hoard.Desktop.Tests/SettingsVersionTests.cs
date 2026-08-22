using System;
using Hoard.Desktop.ViewModels;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// The About row's version string. The SDK appends "+&lt;git sha&gt;" to the informational version
/// (IncludeSourceRevisionInInformationalVersion, on by default since .NET 8), which belongs in the
/// binary but not on screen — see <see cref="SettingsViewModel.DisplayVersion"/>.
/// </summary>
public class SettingsVersionTests
{
    [Fact]
    public void Strips_the_git_sha_build_metadata()
    {
        Assert.Equal("1.1.1", SettingsViewModel.DisplayVersion(
            "1.1.1+318b19ddf3d7275a7129127571dcd2e28a1fd751", new Version(1, 1, 1)));
    }

    [Fact]
    public void Keeps_a_plain_version_untouched()
    {
        Assert.Equal("1.1.1", SettingsViewModel.DisplayVersion("1.1.1", new Version(1, 1, 1)));
    }

    [Fact]
    public void Keeps_a_prerelease_suffix_because_that_IS_the_version()
    {
        // '-' introduces the pre-release; only '+' introduces build metadata.
        Assert.Equal("1.2.0-rc.1", SettingsViewModel.DisplayVersion(
            "1.2.0-rc.1+abcdef0123456789", new Version(1, 2, 0)));
    }

    [Fact]
    public void Falls_back_to_the_assembly_version_when_there_is_no_informational_one()
    {
        Assert.Equal("2.3.4", SettingsViewModel.DisplayVersion(null, new Version(2, 3, 4, 5)));
        Assert.Equal("2.3.4", SettingsViewModel.DisplayVersion("", new Version(2, 3, 4, 5)));
    }

    [Fact]
    public void Falls_back_when_the_informational_version_is_ONLY_build_metadata()
    {
        // Degenerate, but trimming would otherwise hand the About row an empty string.
        Assert.Equal("2.3.4", SettingsViewModel.DisplayVersion("+abcdef", new Version(2, 3, 4)));
    }

    [Fact]
    public void Says_unknown_when_there_is_nothing_at_all()
    {
        Assert.Equal("unknown", SettingsViewModel.DisplayVersion(null, null));
    }
}
