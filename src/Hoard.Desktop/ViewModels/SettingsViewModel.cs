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

    public SettingsViewModel(UiSettingsStore? store, ProjectManager? projects, AppPaths? appPaths)
    {
        _store = store;
        _projects = projects;
        _appPaths = appPaths;

        var s = store?.Settings ?? new UiSettings();
        _themeChoice = s.Theme switch { "light" => "Light", "dark" => "Dark", _ => "System" };
        _uiScalePercent = UiScaleChoices.Contains(s.UiScalePercent) ? s.UiScalePercent : 100;
        _defaultCookiesBrowser = BrowserCookies.NormaliseChoice(s.DefaultCookiesBrowser);
        _gifAutoplay = s.GifAutoplay;
        _maxPlayingGifs = MaxPlayingGifsChoices.Contains(s.MaxPlayingGifs) ? s.MaxPlayingGifs : 12;

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
    public SettingsViewModel() : this(null, null, null) { }

    [ObservableProperty] private bool _isOpen;

    /// <summary>Open the sheet (the bar's ⚙). Project-dependent rows re-evaluate here.</summary>
    public void Open()
    {
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
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString(3)
               ?? "unknown";
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
