using Hoard.Core.Library;
using Xunit;

namespace Hoard.Core.Tests;

public class ExportNamesTests
{
    [Theory]
    [InlineData("Cosy nook", "Cosy nook")]
    [InlineData("A/B:C*D", "A B C D")]                    // invalid chars become spaces, runs collapse
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("ends with dots...", "ends with dots")]   // Windows silently strips trailing dots
    [InlineData("tab\tand\nnewline", "tab and newline")]  // control chars go too
    public void SanitiseComponent_normalises(string input, string expected) =>
        Assert.Equal(expected, ExportNames.SanitiseComponent(input));

    [Fact]
    public void SanitiseComponent_guards_windows_reserved_names()
    {
        Assert.Equal("CON_", ExportNames.SanitiseComponent("CON"));
        Assert.Equal("lpt1_", ExportNames.SanitiseComponent("lpt1")); // case-insensitive, like Windows
    }

    [Fact]
    public void SanitiseComponent_returns_empty_when_nothing_printable_survives()
    {
        Assert.Equal("", ExportNames.SanitiseComponent("///***"));
        Assert.Equal("", ExportNames.SanitiseComponent("   "));
        Assert.Equal("", ExportNames.SanitiseComponent(null));
    }

    [Fact]
    public void SanitiseComponent_caps_length_at_a_word_boundary()
    {
        var longTitle = string.Join(' ', Enumerable.Repeat("word", 40)); // 199 chars
        var result = ExportNames.SanitiseComponent(longTitle, maxLength: 60);
        Assert.True(result.Length <= 60);
        Assert.EndsWith("word", result); // cut on the space, not mid-word
    }

    [Fact]
    public void SanitiseComponent_hard_cuts_a_single_unbroken_word()
    {
        var result = ExportNames.SanitiseComponent(new string('x', 200), maxLength: 60);
        Assert.Equal(60, result.Length);
    }

    [Fact]
    public void FileName_prefers_title_with_the_pin_id_key()
    {
        Assert.Equal("Cosy nook [111].jpg", ExportNames.FileName("Cosy nook", "111", "abcdef123456789", ".jpg"));
        Assert.Equal("111.jpg", ExportNames.FileName(null, "111", "abcdef123456789", ".jpg"));
        Assert.Equal("111.jpg", ExportNames.FileName("  ", "111", "abcdef123456789", "jpg")); // dotless ext ok
    }

    [Fact]
    public void FileName_falls_back_to_a_sha_stub_without_a_pin_id()
    {
        Assert.Equal("abcdef123456.gif", ExportNames.FileName(null, null, "abcdef1234567890aa", ".gif"));
        Assert.Equal("Lamp [abcdef123456].gif", ExportNames.FileName("Lamp", "", "abcdef1234567890aa", ".gif"));
    }

    [Fact]
    public void FileName_tolerates_a_missing_extension()
    {
        Assert.Equal("111", ExportNames.FileName(null, "111", "abcdef123456789", null));
        Assert.Equal("111", ExportNames.FileName(null, "111", "abcdef123456789", ""));
    }

    [Fact]
    public void FolderName_backstops_an_unprintable_title_with_the_id()
    {
        Assert.Equal("Kitchen Ideas", ExportNames.FolderName("Kitchen Ideas", 7));
        Assert.Equal("Untitled [7]", ExportNames.FolderName("///", 7));
    }

    [Fact]
    public void ProjectFolderName_backstops_an_unprintable_name_with_a_generic_one()
    {
        // A project has no id worth putting in a path, so the fallback is a plain name.
        Assert.Equal("Test Backup", ExportNames.ProjectFolderName("Test Backup"));
        Assert.Equal("Hoard project", ExportNames.ProjectFolderName("///"));
        Assert.Equal("Hoard project", ExportNames.ProjectFolderName(null));
    }
}
