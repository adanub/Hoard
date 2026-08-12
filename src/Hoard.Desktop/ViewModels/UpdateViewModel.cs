using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Services;
using Serilog;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// Updating, as the user sees it: the startup check, the "a new version is available" prompt sheet, and the
/// Settings rows (check now / install now). Shell-owned and shell-lifetime, like <see cref="SettingsViewModel"/>.
/// <para>
/// <b>Updating is opt-in.</b> A found update only ever opens a prompt the user can decline — unless they've
/// turned "install updates automatically" on, in which case it downloads quietly and applies on exit. The
/// decision itself lives in <see cref="UpdatePolicy"/> so it's testable without a UI.
/// </para>
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    // Long enough that the launcher has rendered and loaded its recents before a network call starts.
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Dev-only (same spirit as HOARD_GALLERY=1): drive this UI with a pretend update so the prompt and the
    /// Settings rows can be checked from <c>dotnet run</c>. A dev build is never Velopack-installed, so
    /// without this the whole section correctly hides itself and there's nothing to look at. Nothing is
    /// ever downloaded or applied in this mode.
    /// </summary>
    private static readonly bool DemoMode = Environment.GetEnvironmentVariable("HOARD_UPDATE_DEMO") == "1";

    private const string DemoVersion = "9.9.9";

    private readonly UpdateService? _updates;
    private readonly UiSettings _settings;
    private readonly ImportStatus? _importStatus;
    private readonly ToastService? _toasts;
    private readonly Func<bool>? _isModalOpen;

    // Cancels an in-flight download when the user dismisses the prompt mid-install.
    private CancellationTokenSource? _installCancel;

    /// <param name="isModalOpen">Whether some other sheet currently owns the screen — the startup prompt must
    /// not steal it. Null means "never blocked".</param>
    public UpdateViewModel(UpdateService? updates, UiSettings settings,
                           ImportStatus? importStatus = null, ToastService? toasts = null,
                           Func<bool>? isModalOpen = null)
    {
        _updates = updates;
        _settings = settings;
        _importStatus = importStatus;
        _toasts = toasts;
        _isModalOpen = isModalOpen;
    }

    /// <summary>Design-time constructor for the XAML previewer. Deliberately passes NO update service: the
    /// previewer must not construct a Velopack <c>UpdateManager</c> (which probes the disk for install
    /// metadata) just to render a sheet.</summary>
    public UpdateViewModel() : this(null, new UiSettings()) { }

    /// <summary>False for the portable zip and dev builds — there's no install for Velopack to replace, so the
    /// Settings section says so instead of offering buttons that can't work.</summary>
    public bool IsSupported => DemoMode || _updates?.IsSupported == true;

    /// <summary>The prompt sheet's open state (hosted in a shell-level <c>SheetHost</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPromptBusy))]
    private bool _isOpen;

    /// <summary>A check or download is in flight — disables the buttons and drives the busy bars.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(IsPromptBusy))]
    [NotifyPropertyChangedFor(nameof(DismissLabel))]
    private bool _isBusy;

    /// <summary>The prompt's left button. It has always <i>done</i> the right thing mid-download — dismissing
    /// cancels — but labelled "Not now" throughout, the only way to stop an install you'd changed your mind
    /// about was invisible. The label has to say what the button now does.</summary>
    public string DismissLabel => IsBusy ? "Cancel" : "Not now";

    /// <summary>Whether a newer release is waiting; reveals the Install button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(SettingsStatusText))]
    private bool _isUpdateAvailable;

    /// <summary>The version on offer, e.g. "1.4.0".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptMessage))]
    [NotifyPropertyChangedFor(nameof(SettingsStatusText))]
    private string _availableVersion = "";

    /// <summary>Progress / outcome of the current operation. Empty until something happens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsStatusText))]
    private string _statusText = "";

    public bool CanCheck => !IsBusy;

    public bool CanInstall => IsUpdateAvailable && !IsBusy;

    /// <summary>
    /// The prompt's busy bar gate. It must include <see cref="IsOpen"/>, not just <see cref="IsBusy"/>: this
    /// sheet lives in a shell-level <c>SheetHost</c> that is ALWAYS attached (closing it only hides an
    /// ancestor), and <c>BusyBar</c> keys its animation off its own <c>IsVisible</c> — an ancestor-only gate
    /// would leave the infinite indeterminate animation ticking invisibly for the whole download, which is
    /// precisely the leak <c>BusyBar</c> exists to prevent. Same reasoning for the Settings copy
    /// (<c>SettingsViewModel.ShowUpdateBusy</c>).
    /// </summary>
    public bool IsPromptBusy => IsBusy && IsOpen;

    /// <summary>The prompt's body copy, built as ONE string — three adjacent <c>&lt;Run&gt;</c>s relying on
    /// XAML inter-element whitespace to space the words is a trap this codebase uses nowhere else.</summary>
    public string PromptMessage =>
        $"Hoard {AvailableVersion} is ready to install. Hoard will restart to finish; your projects and archive aren't touched.";

    /// <summary>What the Settings section shows. Falls back to naming the pending version, so an update found
    /// by the startup check is still explained there after the prompt was dismissed — the prompt itself
    /// deliberately leaves <see cref="StatusText"/> empty (its body copy already says the same thing, and a
    /// blank line there is what lets a real failure message become visible).</summary>
    public string SettingsStatusText =>
        StatusText.Length > 0 ? StatusText
        : IsUpdateAvailable && AvailableVersion.Length > 0 ? $"Hoard {AvailableVersion} is available."
        : "";

    // ── The startup check ─────────────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget from the shell: after a short delay, check (if the user hasn't turned that off) and
    /// either prompt or install quietly. Failures are silent here — an offline launch shouldn't nag; a
    /// <i>manual</i> check is where the user gets told the check didn't work.
    /// </summary>
    public async Task RunStartupCheckAsync()
    {
        if (!UpdatePolicy.ShouldCheckOnStartup(IsSupported, _settings.AutoCheckUpdates)) return;

        try
        {
            await Task.Delay(StartupCheckDelay);
            var found = DemoMode || _updates is null ? null : await _updates.CheckAsync();
            var action = UpdatePolicy.OnUpdateFound(DemoMode || found is not null, _settings.AutoInstallUpdates);
            if (action == UpdateAction.None) return;

            IsUpdateAvailable = true;
            AvailableVersion = DemoMode ? DemoVersion : _updates?.PendingVersion ?? "";

            if (action == UpdateAction.InstallInBackground)
            {
                await InstallInBackgroundAsync();
                return;
            }

            // Never seize a screen someone is already using. Five seconds in, the user may well have opened
            // Settings or an import sheet — this sheet is declared last in the shell, so it would render over
            // theirs AND take the focus and the next Esc. The offer isn't lost: the Settings section shows it.
            if (_isModalOpen?.Invoke() == true)
            {
                _toasts?.Show($"Hoard {AvailableVersion} is available — see Settings → Updates.");
                return;
            }

            IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup update check failed"); // silent by design
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>The Settings "Check for updates" button. Unlike the startup check this one always reports —
    /// including "couldn't check", which a silent null would misreport as "up to date".</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckNowAsync()
    {
        if (!IsSupported)
        {
            StatusText = "This build doesn't update itself — download the latest release from GitHub.";
            return;
        }

        IsBusy = true;
        StatusText = "Checking for updates…";
        try
        {
            if (DemoMode)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                IsUpdateAvailable = true;
                AvailableVersion = DemoVersion;
                StatusText = $"Hoard {AvailableVersion} is available.";
                return;
            }

            var found = _updates is null ? null : await _updates.CheckAsync();
            IsUpdateAvailable = found is not null;
            AvailableVersion = _updates?.PendingVersion ?? "";
            StatusText = found is null
                ? "You're on the latest version."
                : $"Hoard {AvailableVersion} is available.";
        }
        catch
        {
            // A failed check leaves any previously-found update intact (UpdateService keeps Pending), so
            // IsUpdateAvailable is deliberately NOT cleared here — the Install button shouldn't vanish
            // because the network blipped.
            StatusText = "Couldn't check for updates — check your connection and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Download and install now, then relaunch. Used by both the prompt's "Update now" and the
    /// Settings "Install update" button.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (!DemoMode && _updates?.Pending is null)
        {
            // Reachable if a check failed in a way that cleared the offer while a prompt was still open.
            // Say so — silently doing nothing reads as a broken button.
            IsUpdateAvailable = false;
            StatusText = "That update is no longer available. Check for updates again.";
            return;
        }

        if (!CheckArchiveIdle()) return;

        IsBusy = true;
        using var cancel = new CancellationTokenSource();
        _installCancel = cancel;
        try
        {
            StatusText = "Downloading update…";

            if (DemoMode)
            {
                // Paced to about ten seconds, NOT as fast as it can render: a demo download that finishes in
                // a second leaves no window to actually exercise Cancel, which is the one path here worth
                // trying by hand (the others end in a real relaunch).
                for (var percent = 0; percent <= 100 && !cancel.IsCancellationRequested; percent += 5)
                {
                    StatusText = $"Downloading update… {percent}%";
                    await Task.Delay(500, cancel.Token);
                }
                StatusText = "Demo mode — nothing was downloaded or installed.";
                return;
            }

            var update = _updates!.Pending!;
            await _updates.DownloadAsync(update, ReportProgress, cancel.Token);

            // Re-check the interlock HERE, not just before the download: a download runs for minutes, and an
            // import or backup sync can easily have started in the meantime. Applying swaps the whole app
            // directory and takes the process down with it — mid-write, that's the torn archive the interlock
            // exists to prevent. The download is already on disk, so deferring costs the user nothing.
            if (!CheckArchiveIdle()) return;

            StatusText = "Restarting to finish the update…";
            _updates.ApplyAndRestart(update); // does not return on success
        }
        catch (OperationCanceledException)
        {
            StatusText = "Update cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Installing update {Version} failed", AvailableVersion);
            StatusText = "The update couldn't be installed. Your archive is untouched.";
            _toasts?.Show(StatusText, isError: true);
        }
        finally
        {
            // ApplyAndRestart normally never returns — but it CAN (Velopack declined to relaunch, or the
            // manager was never initialised). Without this the app is left permanently "busy": spinner up,
            // both buttons disabled, no way back short of a restart.
            _installCancel = null;
            IsBusy = false;
        }
    }

    /// <summary>"Not now" on the prompt — dismiss without updating. Also the sheet's scrim/Esc dismiss.
    /// Cancels an install already under way: the sheet vanishing while a download silently continued to a
    /// surprise relaunch is the opposite of what dismissing means.</summary>
    [RelayCommand]
    private void Close()
    {
        if (IsBusy) _installCancel?.Cancel();
        IsOpen = false;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>Auto-install: download quietly and stage it for exit, so nothing interrupts the session.</summary>
    private async Task InstallInBackgroundAsync()
    {
        if (DemoMode)
        {
            StatusText = $"Hoard {AvailableVersion} will be installed when you close the app. (Demo mode — it won't be.)";
            _toasts?.Show(StatusText);
            return;
        }

        if (_updates?.Pending is not { } update) return;

        // Holds IsBusy for the whole background download, which is what stops the Settings "Install update"
        // button starting a SECOND download + apply against the same UpdateManager and staging directory
        // while this one is still writing.
        IsBusy = true;
        try
        {
            await _updates.DownloadAsync(update);
            _updates.ApplyOnExit(update);
            StatusText = $"Hoard {AvailableVersion} will be installed when you close the app.";
            _toasts?.Show(StatusText);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Background update download failed");
            // Leave IsUpdateAvailable set: the Settings section still offers it manually.
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The archive interlock: applying swaps the app directory, so it must never race an import or a
    /// backup sync. Reports to the user when it refuses — a silent no-op reads as a broken button.</summary>
    private bool CheckArchiveIdle()
    {
        if (UpdatePolicy.CanApply(_importStatus?.IsImporting ?? false, _importStatus?.IsRemoteSyncing ?? false))
            return true;

        StatusText = "Hoard is busy writing to your archive — try again once it's finished.";
        _toasts?.Show(StatusText);
        return false;
    }

    // Velopack reports progress from a background thread; the observable properties are UI-bound.
    private void ReportProgress(int percent) => Dispatcher.UIThread.Post(() =>
    {
        if (IsBusy) StatusText = $"Downloading update… {percent}%";
    });
}
