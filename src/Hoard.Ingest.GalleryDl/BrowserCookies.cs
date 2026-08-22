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
        "chrome", "edge", "brave", "chromium", "vivaldi", "opera", "opera gx",
    };

    public const string None = "(none)";

    /// <summary>Opera GX keeps its profile in a folder of its own and gallery-dl has no name for it, so the
    /// choice resolves to a path instead - see <see cref="Resolve"/>.</summary>
    public const string OperaGx = "opera gx";

    /// <summary>Map a stored/user value onto the canonical <see cref="Choices"/> entry it names
    /// (case-insensitive, like <see cref="Resolve"/>), or <see cref="None"/> when it isn't offered — the one
    /// home for the "is this saved default still valid" rule the cookie pickers and Settings all need.</summary>
    public static string NormaliseChoice(string? choice)
        => Choices.FirstOrDefault(c => string.Equals(c, choice, StringComparison.OrdinalIgnoreCase)) ?? None;

    /// <summary>The choice as it should read in a sentence — the picker's entries are lowercase.</summary>
    public static string DisplayName(string? choice)
    {
        var c = NormaliseChoice(choice);
        if (c == None) return c;
        if (c == OperaGx) return "Opera GX";
        return char.ToUpperInvariant(c[0]) + c[1..];
    }

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

        // Opera GX is a separate install from Opera, in "Opera GX Stable" rather than "Opera Stable", and
        // gallery-dl knows only "opera" - so a GX user picking Opera gets a search of a folder they don't
        // have, no cookies, and every private board reported missing. It reads the same Chromium database,
        // so hand gallery-dl the GX folder as a path: with a path it searches there for the cookies AND
        // takes that folder as the directory holding "Local State", which is the decryption key. (Opera
        // stores no per-profile subfolders, so root-as-directory is right - confirmed against a real
        // install: Local State at the root, cookies at Default/Network/Cookies.) Same shape as the Gecko
        // forks above.
        if (choice.Equals(OperaGx, StringComparison.OrdinalIgnoreCase))
        {
            var root = ChromiumRoot(OperaGx);
            if (root is null || !Directory.Exists(root))
                return new Resolution(null, false,
                    "Couldn't find an Opera GX profile. Make sure Opera GX is installed and you've logged " +
                    "into Pinterest in it at least once.");
            return new Resolution($"opera:{root}", true, null);
        }

        // Standard browser gallery-dl already knows by name.
        return new Resolution(choice.ToLowerInvariant(), true, null);
    }

    /// <summary>Where a Chromium browser keeps the profile root holding its cookie database. Mirrors
    /// gallery-dl's own table — only the two platforms Hoard ships for; anywhere else we simply find
    /// nothing and stay quiet.</summary>
    private static string? ChromiumRoot(string browser)
    {
        string? Local(params string[] parts) => Join(Environment.SpecialFolder.LocalApplicationData, parts);
        string? Roaming(params string[] parts) => Join(Environment.SpecialFolder.ApplicationData, parts);
        string? Support(params string[] parts) => Join(Environment.SpecialFolder.UserProfile,
            new[] { "Library", "Application Support" }.Concat(parts).ToArray());

        var mac = OperatingSystem.IsMacOS();
        return browser switch
        {
            "chrome" => mac ? Support("Google", "Chrome") : Local("Google", "Chrome", "User Data"),
            "edge" => mac ? Support("Microsoft Edge") : Local("Microsoft", "Edge", "User Data"),
            "brave" => mac ? Support("BraveSoftware", "Brave-Browser")
                           : Local("BraveSoftware", "Brave-Browser", "User Data"),
            "chromium" => mac ? Support("Chromium") : Local("Chromium", "User Data"),
            "vivaldi" => mac ? Support("Vivaldi") : Local("Vivaldi", "User Data"),
            "opera" => mac ? Support("com.operasoftware.Opera") : Roaming("Opera Software", "Opera Stable"),
            OperaGx => mac ? Support("com.operasoftware.OperaGX")
                           : Roaming("Opera Software", "Opera GX Stable"),
            _ => null,
        };

        static string? Join(Environment.SpecialFolder root, string[] parts)
        {
            var b = Environment.GetFolderPath(root);
            return string.IsNullOrEmpty(b) ? null : Path.Combine(new[] { b }.Concat(parts).ToArray());
        }
    }

    /// <summary>
    /// The cookie database under a Chromium profile root, or null when there isn't one. Chromium keeps it at
    /// <c>Network/Cookies</c> (older builds: <c>Cookies</c>) inside a profile directory, and which directory
    /// that is varies: <c>Default</c>, a <c>Profile N</c>, or - Opera - the root itself. Rather than walk the
    /// tree (a profile root contains the browser's whole cache; gallery-dl's recursive search is affordable
    /// for it only because it runs once per crawl), the plausible spots are checked directly and the most
    /// recently written wins, which is the same rule by a cheaper route.
    /// </summary>
    internal static string? FindChromiumCookieDb(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        var profiles = new List<string> { root };
        try
        {
            profiles.AddRange(Directory.EnumerateDirectories(root).Where(d =>
            {
                var name = Path.GetFileName(d);
                return name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase);
            }));
        }
        catch (IOException) { /* unreadable root - nothing to report */ }
        catch (UnauthorizedAccessException) { }

        return profiles
            .SelectMany(p => new[] { Path.Combine(p, "Network", "Cookies"), Path.Combine(p, "Cookies") })
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Whether the chosen browser's cookie database can't be read right now because the browser has
    /// it open. Chromium browsers hold it with the share mode set to deny-all while they run, so no reader
    /// can touch it and every private board comes back "not found"; Gecko browsers permit shared reads.
    /// <para>Fails OPEN on purpose: unless a database was found AND positively refused us, this reports
    /// false. A warning nobody can act on, in front of every import, would be far worse than a missed one.</para></summary>
    public static bool IsCookieDbLocked(string? choice)
    {
        var browser = NormaliseChoice(choice);
        if (browser == None) return false;

        var path = GeckoForkAppDataDirs.ContainsKey(browser) || browser == "firefox"
            ? GeckoCookieDb(browser)
            : FindChromiumCookieDb(ChromiumRoot(browser) ?? "");
        return path is not null && !CanRead(path);
    }

    private static string? GeckoCookieDb(string browser)
    {
        var dir = GeckoForkAppDataDirs.TryGetValue(browser, out var d)
            ? FindNewestFirefoxProfile(d)
            : FindNewestFirefoxProfile(Path.Combine("Mozilla", "Firefox"));
        if (dir is null) return null;
        var db = Path.Combine(dir, "cookies.sqlite");
        return File.Exists(db) ? db : null;
    }

    /// <summary>Can this file be opened for reading alongside whoever else has it? The share flags are the
    /// most permissive available, so a refusal means the HOLDER denied it - which is exactly the condition
    /// worth warning about.</summary>
    private static bool CanRead(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
