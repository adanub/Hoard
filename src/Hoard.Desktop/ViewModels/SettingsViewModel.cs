using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Projects;
using Hoard.Desktop.Services;
using Hoard.Ingest.GalleryDl;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// The Settings sheet (opened from the floating bar's ⚙): theme, UI scale, the default cookies browser, GIF
/// behaviour, and diagnostics/about. Every change writes straight to the shared <see cref="UiSettings"/> and
/// saves — consumers read the same instance live (the shell scale listens to the store's Changed event).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly UiSettingsStore? _store;
    private readonly ProjectManager? _projects;
    private readonly AppPaths? _appPaths;

    /// <summary>The update section's check/install commands and status; shell-owned, shared with the prompt
    /// sheet so a check started here reveals the same pending update the prompt offers.</summary>
    public UpdateViewModel? Update { get; }

    public SettingsViewModel(UiSettingsStore? store, ProjectManager? projects, AppPaths? appPaths,
                             UpdateViewModel? update = null)
    {
        _store = store;
        _projects = projects;
        _appPaths = appPaths;
        Update = update;
        // While the choice is "System", IsDarkTheme reports what the app is ACTUALLY showing — so when the OS
        // flips light↔dark the app re-themes and the breadcrumb strip's switch has to move with it. Without
        // this it would sit at its old position, showing the opposite of what's on screen, until something
        // else touched ThemeChoice. (Shell-lifetime, like the subscription below — nothing to detach.)
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) => OnPropertyChanged(nameof(IsDarkTheme));
        // Both are shell-lifetime singletons, so this subscription lives as long as the app — nothing to detach.
        if (update is not null)
            update.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UpdateViewModel.IsBusy)) OnPropertyChanged(nameof(ShowUpdateBusy));
            };

        var s = store?.Settings ?? new UiSettings();
        _themeChoice = s.Theme switch { "light" => "Light", "dark" => "Dark", _ => "System" };
        _uiScalePercent = UiScaleChoices.Contains(s.UiScalePercent) ? s.UiScalePercent : 100;
        _defaultCookiesBrowser = BrowserCookies.NormaliseChoice(s.DefaultCookiesBrowser);
        _gifAutoplay = s.GifAutoplay;
        _maxPlayingGifs = MaxPlayingGifsChoices.Contains(s.MaxPlayingGifs) ? s.MaxPlayingGifs : 12;
        _autoCheckUpdates = s.AutoCheckUpdates;
        _autoInstallUpdates = s.AutoInstallUpdates;

        // PERSIST the snap-backs: consumers read the raw shared UiSettings live (the GIF budget, cookie
        // defaults), so a stored out-of-choice value the sheet merely displayed differently would be silently
        // ENFORCED while the sheet claims otherwise (a hand-edited maxPlayingGifs: 10 showing as "12" but
        // capping at 10). Writing the normalised values back keeps display and behaviour one thing.
        if (store is not null)
        {
            var theme = _themeChoice.ToLowerInvariant();
            if (s.Theme != theme || s.UiScalePercent != _uiScalePercent
                || s.DefaultCookiesBrowser != _defaultCookiesBrowser || s.MaxPlayingGifs != _maxPlayingGifs)
            {
                s.Theme = theme;
                s.UiScalePercent = _uiScalePercent;
                s.DefaultCookiesBrowser = _defaultCookiesBrowser;
                s.MaxPlayingGifs = _maxPlayingGifs;
                store.Save();
            }
        }
    }

    // Design-time constructor for the XAML previewer.
    public SettingsViewModel() : this(null, null, null, null) { }

    [ObservableProperty] private bool _isOpen;

    /// <summary>Open the sheet (the bar's ⚙). Project-dependent rows re-evaluate here.</summary>
    public void Open()
    {
        // Re-read the cookie default: this VM is shell-lifetime, but every import/sync that resolves cookies
        // now writes its choice back (UiSettingsStore.RememberCookiesBrowser), so the value cached at startup
        // can be stale. Showing the stale one would break the rule the ctor's snap-backs exist for — what the
        // sheet displays and what the pickers actually use must be one value. (An unchanged assignment is a
        // no-op, so this doesn't re-persist on every open.)
        if (_store is not null)
            DefaultCookiesBrowser = BrowserCookies.NormaliseChoice(_store.Settings.DefaultCookiesBrowser);
        OnPropertyChanged(nameof(HasOpenProject));
        OpenProjectFolderCommand.NotifyCanExecuteChanged();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    // ── Theme ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> ThemeChoices { get; } = new[] { "System", "Light", "Dark" };

    [ObservableProperty] private string _themeChoice;

    partial void OnThemeChoiceChanged(string value)
    {
        Persist(s => s.Theme = value.ToLowerInvariant());
        ApplyTheme(value);
        OnPropertyChanged(nameof(IsDarkTheme)); // the breadcrumb strip's switch shows the same state
    }

    /// <summary>
    /// The breadcrumb strip's sun/moon switch: a two-state view of the three-state <see cref="ThemeChoice"/>,
    /// so the common flip doesn't cost a trip into Settings. Both write the same stored preference — this is
    /// a second control over one value, never a second value.
    /// <para>Reading it while the choice is "System" reports what the app is ACTUALLY showing
    /// (<c>ActualThemeVariant</c>), because a switch has to sit where the eye says it should. Flipping it
    /// commits to Light or Dark explicitly: "system" is a follow-the-OS mode a two-state control can't
    /// express, so leaving it is the deliberate meaning of touching the switch (Settings takes you back).</para>
    /// </summary>
    public bool IsDarkTheme
    {
        get => ThemeChoice.Equals("Dark", StringComparison.OrdinalIgnoreCase)
               || (!ThemeChoice.Equals("Light", StringComparison.OrdinalIgnoreCase)
                   && Application.Current?.ActualThemeVariant == ThemeVariant.Dark);
        set => ThemeChoice = value ? "Dark" : "Light";
    }

    /// <summary>Set the app-wide theme variant ("system"/"light"/"dark", case-insensitive). Also called at
    /// startup (from <c>App</c>) with the stored value.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme.ToLowerInvariant() switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // follow the OS
            };
    }

    // ── UI scale (accessibility) ──────────────────────────────────────────────

    public IReadOnlyList<int> UiScaleChoices { get; } = new[] { 75, 90, 100, 110, 125, 150 };

    [ObservableProperty] private int _uiScalePercent;

    partial void OnUiScalePercentChanged(int value) => Persist(s => s.UiScalePercent = value);

    // ── Cookies default ───────────────────────────────────────────────────────

    public IReadOnlyList<string> CookieBrowsers { get; } = BrowserCookies.Choices;

    [ObservableProperty] private string _defaultCookiesBrowser;

    partial void OnDefaultCookiesBrowserChanged(string value) => Persist(s => s.DefaultCookiesBrowser = value);

    // ── GIF behaviour ─────────────────────────────────────────────────────────

    [ObservableProperty] private bool _gifAutoplay;

    partial void OnGifAutoplayChanged(bool value) => Persist(s => s.GifAutoplay = value);

    public IReadOnlyList<int> MaxPlayingGifsChoices { get; } = new[] { 6, 12, 18, 24, 32 };

    [ObservableProperty] private int _maxPlayingGifs;

    partial void OnMaxPlayingGifsChanged(int value) => Persist(s => s.MaxPlayingGifs = value);

    // ── Updates ───────────────────────────────────────────────────────────────

    /// <summary>Look for a new release at startup. On by default — finding one only ever offers it.</summary>
    [ObservableProperty] private bool _autoCheckUpdates;

    partial void OnAutoCheckUpdatesChanged(bool value) => Persist(s => s.AutoCheckUpdates = value);

    /// <summary>Install a found update without asking. OFF by default: updating is the user's call.</summary>
    [ObservableProperty] private bool _autoInstallUpdates;

    partial void OnAutoInstallUpdatesChanged(bool value) => Persist(s => s.AutoInstallUpdates = value);

    /// <summary>Whether to render the Updates section at all — false only in the previewer/gallery, which has
    /// no update service, and where the heading would otherwise sit alone between two dividers.</summary>
    public bool HasUpdates => Update is not null;

    /// <summary>Show the update toggles/buttons — only for a build that can actually replace itself.</summary>
    public bool ShowUpdateControls => Update?.IsSupported == true;

    /// <summary>Show "this build doesn't update itself" instead (the portable zip, a dev run).</summary>
    public bool ShowUpdateUnsupportedNote => Update is { IsSupported: false };

    /// <summary>The Settings busy bar's gate: an update op is running AND this sheet is on screen. The second
    /// half is load-bearing — the shell's Settings <c>SheetHost</c> stays attached when closed, and a
    /// <c>BusyBar</c> keyed only off <c>Update.IsBusy</c> would run its infinite animation invisibly for a
    /// whole download (the leak the control exists to prevent).</summary>
    public bool ShowUpdateBusy => IsOpen && Update?.IsBusy == true;

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(ShowUpdateBusy));

    // ── Diagnostics / about ───────────────────────────────────────────────────

    public string AppVersion { get; } = ReadAppVersion();

    public string GalleryDlVersion { get; } = ReadGalleryDlVersion();

    public bool HasOpenProject => _projects?.Current is not null;

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (_appPaths is { LogsRoot: { } logs }) OpenFolder(logs);
    }

    [RelayCommand(CanExecute = nameof(HasOpenProject))]
    private void OpenProjectFolder()
    {
        if (_projects?.Current is { Root: { } root }) OpenFolder(root);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); // Explorer (or the platform default)
        }
        catch
        {
            // best-effort convenience — never let a shell launch failure surface as a crash
        }
    }

    private static string ReadAppVersion()
    {
        var asm = typeof(SettingsViewModel).Assembly;
        return DisplayVersion(
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            asm.GetName().Version);
    }

    /// <summary>
    /// The version as a human reads it, from the assembly's informational version.
    /// </summary>
    /// <remarks>
    /// The SDK appends <c>+&lt;full 40-char git sha&gt;</c> to <see cref="AssemblyInformationalVersionAttribute"/>
    /// — <c>IncludeSourceRevisionInInformationalVersion</c> has defaulted to true since .NET 8, so
    /// <c>Version=1.1.1</c> ships as <c>1.1.1+318b19d…</c>. That is semver BUILD METADATA and it is worth
    /// keeping in the binary (it identifies the exact commit a release was built from, which is the one
    /// thing a bug report can't reconstruct), so it is trimmed for DISPLAY only rather than switched off
    /// at build time. Trim at '+' alone: a pre-release suffix is introduced by '-' and IS part of the
    /// version ("1.2.0-rc.1" must survive intact).
    /// </remarks>
    internal static string DisplayVersion(string? informational, Version? assemblyVersion)
    {
        if (informational is { Length: > 0 })
        {
            var plus = informational.IndexOf('+');
            var trimmed = plus >= 0 ? informational[..plus] : informational;
            if (trimmed.Length > 0) return trimmed;
        }

        return assemblyVersion?.ToString(3) ?? "unknown";
    }

    private static string ReadGalleryDlVersion()
    {
        try
        {
            var path = App.ResolveGalleryDlPath();
            if (!File.Exists(path)) return "not found (PATH fallback)";
            // No subprocess (--version) — the sandboxed/first-run cost isn't worth it for an About row; the
            // exe's file metadata is enough, and "bundled" still tells the user the tool is present.
            return FileVersionInfo.GetVersionInfo(path).FileVersion is { Length: > 0 } v ? v : "bundled";
        }
        catch
        {
            return "unknown";
        }
    }

    private void Persist(Action<UiSettings> apply)
    {
        if (_store is null) return; // design time
        apply(_store.Settings);
        _store.Save();
    }
}
