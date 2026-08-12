using Hoard.Desktop.Services;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// The update rules, pinned without Velopack or a UI. These encode product decisions that are easy to
/// "simplify" back out: updating is opt-in, and applying one must never race the archive writers.
/// </summary>
public class UpdatePolicyTests
{
    // ── The startup check ─────────────────────────────────────────────────────

    [Fact]
    public void Startup_checks_when_supported_and_enabled()
        => Assert.True(UpdatePolicy.ShouldCheckOnStartup(isSupported: true, autoCheckEnabled: true));

    [Fact]
    public void Startup_check_respects_the_user_turning_it_off()
        => Assert.False(UpdatePolicy.ShouldCheckOnStartup(isSupported: true, autoCheckEnabled: false));

    [Fact]
    public void Startup_never_checks_for_a_build_that_cannot_update_itself()
    {
        // The portable zip and `dotnet run`: there's no install for Velopack to replace, so a check could
        // only ever produce an offer we can't honour.
        Assert.False(UpdatePolicy.ShouldCheckOnStartup(isSupported: false, autoCheckEnabled: true));
        Assert.False(UpdatePolicy.ShouldCheckOnStartup(isSupported: false, autoCheckEnabled: false));
    }

    // ── What a found update leads to ──────────────────────────────────────────

    [Fact]
    public void Nothing_found_means_nothing_happens()
    {
        Assert.Equal(UpdateAction.None, UpdatePolicy.OnUpdateFound(updateFound: false, autoInstallEnabled: false));
        // Auto-install must not invent work when there's no update to install.
        Assert.Equal(UpdateAction.None, UpdatePolicy.OnUpdateFound(updateFound: false, autoInstallEnabled: true));
    }

    [Fact]
    public void An_update_is_offered_not_taken_by_default()
        => Assert.Equal(UpdateAction.Prompt, UpdatePolicy.OnUpdateFound(updateFound: true, autoInstallEnabled: false));

    [Fact]
    public void Auto_install_skips_the_prompt()
        => Assert.Equal(UpdateAction.InstallInBackground,
                        UpdatePolicy.OnUpdateFound(updateFound: true, autoInstallEnabled: true));

    // ── The archive interlock ─────────────────────────────────────────────────

    [Fact]
    public void Applying_is_allowed_only_when_the_archive_is_idle()
        => Assert.True(UpdatePolicy.CanApply(isImporting: false, isRemoteSyncing: false));

    [Theory]
    [InlineData(true, false)]  // an import has gallery-dl writing blobs + op segments
    [InlineData(false, true)]  // a backup sync is copying those same files
    [InlineData(true, true)]
    public void Applying_is_refused_while_the_archive_is_being_written(bool importing, bool syncing)
        => Assert.False(UpdatePolicy.CanApply(importing, syncing));
}
