using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Hoard.Desktop.Services;

/// <summary>What a completed update check should lead to.</summary>
public enum UpdateAction
{
    /// <summary>Nothing to do — already current.</summary>
    None,

    /// <summary>Ask the user (the default): updating is always opt-in unless they turned auto-install on.</summary>
    Prompt,

    /// <summary>Download quietly and stage it for the next launch — no prompt, no forced restart.</summary>
    InstallInBackground,
}

/// <summary>
/// The update rules, as pure functions — no Velopack, no Avalonia, so they're unit-tested directly
/// (<c>UpdatePolicyTests</c>) rather than through the view model.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>Check on startup only for an actually-updatable install the user hasn't opted out of.</summary>
    public static bool ShouldCheckOnStartup(bool isSupported, bool autoCheckEnabled)
        => isSupported && autoCheckEnabled;

    /// <summary>An update exists: prompt, unless the user asked for hands-off installs.</summary>
    public static UpdateAction OnUpdateFound(bool updateFound, bool autoInstallEnabled)
        => !updateFound ? UpdateAction.None
            : autoInstallEnabled ? UpdateAction.InstallInBackground
            : UpdateAction.Prompt;

    /// <summary>
    /// Applying swaps the whole app directory out from under a running process, so it must never happen
    /// while the archive is being written: an import has a gallery-dl subprocess writing blobs + op
    /// segments, and a backup sync is copying those same files. Same interlock every other
    /// archive-writing entry point observes (see <c>ImportStatus</c>).
    /// </summary>
    public static bool CanApply(bool isImporting, bool isRemoteSyncing)
        => !isImporting && !isRemoteSyncing;
}

/// <summary>
/// Installation/auto-update via Velopack, reading this repo's GitHub Releases. Desktop-only by nature —
/// applying an update re-execs a native updater binary — so it lives in the head, never in Core.
/// <para>
/// <b>Everything here no-ops unless the app was actually installed by Velopack</b> (<see cref="IsSupported"/>):
/// a <c>dotnet run</c> build and the portable zip have no update feed to speak of, and must keep working
/// exactly as before rather than erroring at the user.
/// </para>
/// </summary>
public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/adanub/Hoard";

    /// <summary>
    /// Dev-only (same spirit as <c>HOARD_UPDATE_DEMO</c>, one step more real): a folder of Velopack packages
    /// to update from instead of GitHub Releases — a <c>vpk pack --outputDir</c> directory. Demo mode fakes
    /// the UI; this one genuinely downloads and applies, which is the only way to exercise the whole path on
    /// a platform before a release carrying its feed exists. That is how the macOS leg was verified.
    /// </summary>
    private const string LocalFeedVariable = "HOARD_UPDATE_FEED";

    // Null when Velopack couldn't initialise at all (no install metadata, unreadable app dir). Treated the
    // same as "not installed" — the updater must never be the reason the app fails to start.
    private readonly UpdateManager? _manager;

    public UpdateService()
    {
        try
        {
            var localFeed = Environment.GetEnvironmentVariable(LocalFeedVariable);
            // prerelease: false — the stable channel only. The channel itself is whatever the app was
            // packed/installed with (win / osx), so the two OS legs of one GitHub Release read their own
            // feed file (releases.win.json / releases.osx.json) and never see each other's builds.
            _manager = string.IsNullOrWhiteSpace(localFeed)
                ? new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false))
                : new UpdateManager(localFeed);
            if (!string.IsNullOrWhiteSpace(localFeed))
                Log.Warning("Updates are pointed at the local feed {Feed} ({Variable})", localFeed, LocalFeedVariable);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Velopack could not initialise — updates are unavailable this session");
        }
    }

    /// <summary>True only for a Velopack-installed app; false for the portable zip and dev builds.</summary>
    public bool IsSupported => _manager?.IsInstalled ?? false;

    /// <summary>The running version as Velopack sees it, or null when not an installed build.</summary>
    public string? InstalledVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>The update found by the last successful <see cref="CheckAsync"/>, if any.</summary>
    public UpdateInfo? Pending { get; private set; }

    /// <summary>The version string of <see cref="Pending"/>, for the prompt and the settings row.</summary>
    public string? PendingVersion => Pending?.TargetFullRelease?.Version?.ToString();

    /// <summary>
    /// Ask GitHub whether there's a newer release; null means "already current" (or this build can't
    /// update). A failed check <b>throws</b> rather than returning null, so a manual check can say
    /// "couldn't check for updates" instead of a misleading "you're up to date".
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            Pending = null;
            return null;
        }
        try
        {
            // Assigned only on SUCCESS. Clearing it up front would let a re-check that merely failed (offline
            // for a moment, GitHub rate-limited) throw away an update an earlier check legitimately found —
            // the Install button would vanish for the rest of the session. A successful check that returns
            // null is different, and correctly clears it.
            Pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (Pending is not null)
                Log.Information("Update available: {Version}", PendingVersion);
            return Pending;
        }
        catch (Exception ex)
        {
            // Offline, rate-limited (unauthenticated GitHub allows 60/hour per IP), or a malformed feed.
            Log.Warning(ex, "Update check failed");
            throw;
        }
    }

    /// <summary>Download the pending update's packages (delta when one applies). Progress is 0–100.</summary>
    public Task DownloadAsync(UpdateInfo update, Action<int>? progress = null, CancellationToken cancel = default)
        => _manager is null
            ? Task.CompletedTask
            : _manager.DownloadUpdatesAsync(update, progress, cancel);

    /// <summary>Swap in the downloaded update and relaunch. Does not return when it succeeds.</summary>
    public void ApplyAndRestart(UpdateInfo update)
        => _manager?.ApplyUpdatesAndRestart(update.TargetFullRelease);

    /// <summary>
    /// Stage the downloaded update to be applied once this process exits, without restarting now. This is
    /// what "install updates automatically" does: yanking the app out from under someone mid-browse to
    /// install something they never asked about would be worse than the update being a launch late.
    /// </summary>
    public void ApplyOnExit(UpdateInfo update)
        => _manager?.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
}
