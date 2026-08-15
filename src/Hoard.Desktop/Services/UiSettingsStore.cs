using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Projects;
using Serilog;

namespace Hoard.Desktop.Services;

/// <summary>
/// The desktop head's user preferences. One mutable instance is shared app-wide: the settings sheet writes
/// it, consumers (import/sync sheets, the board's GIF playback, the shell's scale) read it live.
/// </summary>
public sealed class UiSettings
{
    /// <summary>"system" | "light" | "dark".</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";

    /// <summary>Global accessibility scale for text + interactive elements, 75–150. Applied as a layout
    /// transform on the shell, so screens REFLOW at the new size (fewer, larger masonry columns) — this is
    /// user-chosen zoom, not the scaled-desktop-layout DESIGN.md rules out.</summary>
    [JsonPropertyName("uiScalePercent")] public int UiScalePercent { get; set; } = 100;

    /// <summary>The browser the import/sync cookie pickers pre-select (a <c>BrowserCookies.Choices</c> entry;
    /// consumers fall back to "(none)" when the stored value is no longer offered). Every run that resolves
    /// cookies writes its choice back here (<see cref="UiSettingsStore.RememberCookiesBrowser"/>), so this is
    /// "the last one you used" as much as a Settings preference — picking a browser once for an import means
    /// the next sync opens on it instead of silently reverting to "(none)" and 404ing every private board.</summary>
    [JsonPropertyName("defaultCookiesBrowser")] public string DefaultCookiesBrowser { get; set; } = "";

    /// <summary>Start playing a GIF when its tile scrolls into view (default: tap to play). Memory stays
    /// bounded either way — playback goes through the board's playing-GIF LRU.</summary>
    [JsonPropertyName("gifAutoplay")] public bool GifAutoplay { get; set; }

    /// <summary>The playing-GIF LRU bound (was a hardcoded 12): how many GIFs may animate at once before the
    /// least-recently-played one stops.</summary>
    [JsonPropertyName("maxPlayingGifs")] public int MaxPlayingGifs { get; set; } = 12;

    /// <summary>Look for a new release at startup (default on). Finding one only ever <i>offers</i> the
    /// update — see <see cref="AutoInstallUpdates"/> for the hands-off variant.</summary>
    [JsonPropertyName("autoCheckUpdates")] public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Install a found update without asking (default OFF): download quietly and apply it the next
    /// time Hoard closes. Off, an update is offered in a prompt the user can decline.</summary>
    [JsonPropertyName("autoInstallUpdates")] public bool AutoInstallUpdates { get; set; }
}

/// <summary>
/// Loads/saves <see cref="UiSettings"/> at <c>%APPDATA%/Hoard/ui-settings.json</c> — its own file, NOT Core's
/// <c>settings.json</c>, which <c>ProjectManager</c> rewrites whole (recents) and would clobber anything else
/// stored there. Saving raises <see cref="Changed"/> so live appliers (the shell's scale) can react.
/// </summary>
public sealed class UiSettingsStore
{
    // Cached: a fresh JsonSerializerOptions per Save would rebuild its reflection metadata every call (CA1869).
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;

    public UiSettings Settings { get; }

    /// <summary>Raised after every <see cref="Save"/> (the settings sheet saves on each change).</summary>
    public event Action? Changed;

    public UiSettingsStore(AppPaths appPaths)
    {
        _path = Path.Combine(appPaths.AppDataRoot, "ui-settings.json");
        Settings = Load(_path);
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Settings, WriteOptions));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't save UI settings to {Path}", _path); // preferences must never break the app
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Remember the cookie source a run actually used, so the next import/sync sheet opens on it. Called by
    /// every run that gets past <c>BrowserCookies.Resolve</c> — the picker is otherwise seeded from the stored
    /// default alone, so a browser chosen by hand lived exactly as long as that one sheet: picking "zen" for
    /// an import left the sync-all a minute later on "(none)", which 404s every private board.
    /// <para>"(none)" is remembered too — it's a legitimate choice for public boards, and this is a
    /// last-used, not a best-guess. The write is skipped when nothing changed so a run of imports doesn't
    /// rewrite the file (and raise <see cref="Changed"/>) for no reason.</para>
    /// </summary>
    /// <param name="choice">A <c>BrowserCookies.Choices</c> entry, already normalised by the caller.</param>
    public void RememberCookiesBrowser(string choice)
    {
        if (string.IsNullOrEmpty(choice) || Settings.DefaultCookiesBrowser == choice) return;
        Settings.DefaultCookiesBrowser = choice;
        Save();
    }

    private static UiSettings Load(string path)
    {
        var settings = new UiSettings();
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? settings;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't read UI settings from {Path} — using defaults", path);
        }
        // Clamp anything a hand-edited/corrupt file could break.
        settings.UiScalePercent = Math.Clamp(settings.UiScalePercent, 75, 150);
        settings.MaxPlayingGifs = Math.Clamp(settings.MaxPlayingGifs, 1, 64);
        return settings;
    }
}
