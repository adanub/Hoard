namespace Hoard.Ingest.GalleryDl;

/// <summary>
/// Translates a user-facing browser choice into the value gallery-dl's <c>--cookies-from-browser</c>
/// expects. Standard browsers pass straight through by name. Firefox-based forks (Zen, LibreWolf, …)
/// aren't known to gallery-dl, but they store a Firefox-format <c>cookies.sqlite</c>, so we locate
/// their profile and return <c>firefox:&lt;profile-path&gt;</c>, which gallery-dl reads natively.
///
/// This is desktop-specific (it spawns gallery-dl and reads local browser profiles), which is why it
/// lives in the ingestion assembly rather than the platform-neutral Core.
/// </summary>
public static class BrowserCookies
{
    /// <summary>Gecko forks → the folder under %APPDATA% that holds their Firefox-style profiles.</summary>
    private static readonly IReadOnlyDictionary<string, string> GeckoForkAppDataDirs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zen"] = "zen",
            ["librewolf"] = "librewolf",
            ["floorp"] = "Floorp",
            ["waterfox"] = "Waterfox",
        };

    /// <summary>Browsers offered in the UI. "(none)" means no cookies (public boards only).</summary>
    public static readonly IReadOnlyList<string> Choices = new[]
    {
        "(none)", "firefox", "zen", "librewolf", "floorp", "waterfox",
        "chrome", "edge", "brave", "chromium", "vivaldi", "opera",
    };

    public const string None = "(none)";

    /// <summary>Map a stored/user value onto the canonical <see cref="Choices"/> entry it names
    /// (case-insensitive, like <see cref="Resolve"/>), or <see cref="None"/> when it isn't offered — the one
    /// home for the "is this saved default still valid" rule the cookie pickers and Settings all need.</summary>
    public static string NormaliseChoice(string? choice)
        => Choices.FirstOrDefault(c => string.Equals(c, choice, StringComparison.OrdinalIgnoreCase)) ?? None;

    public readonly record struct Resolution(string? Spec, bool Found, string? Error);

    /// <summary>
    /// Resolve a UI choice to a gallery-dl cookie spec. Returns <c>Found=false</c> with an
    /// <c>Error</c> when a chosen Firefox fork's profile can't be located.
    /// </summary>
    public static Resolution Resolve(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice) || choice.Equals(None, StringComparison.OrdinalIgnoreCase))
            return new Resolution(Spec: null, Found: true, Error: null);

        if (GeckoForkAppDataDirs.TryGetValue(choice, out var appDataDir))
        {
            var profile = FindNewestFirefoxProfile(appDataDir);
            if (profile is null)
                return new Resolution(null, false,
                    $"Couldn't find a {choice} profile with cookies. Make sure {choice} is installed and " +
                    $"you've logged into Pinterest in it at least once.");
            return new Resolution($"firefox:{profile}", true, null);
        }

        // Standard browser gallery-dl already knows by name.
        return new Resolution(choice.ToLowerInvariant(), true, null);
    }

    /// <summary>Find the profile directory holding the most recently used cookies.sqlite.</summary>
    private static string? FindNewestFirefoxProfile(string appDataDir)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appDataDir, "Profiles");
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateFiles(root, "cookies.sqlite", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(d => d is not null);
    }
}
