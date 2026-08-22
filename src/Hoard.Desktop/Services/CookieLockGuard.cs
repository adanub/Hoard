using System;
using System.Threading.Tasks;
using Hoard.Ingest.GalleryDl;

namespace Hoard.Desktop.Services;

/// <summary>
/// Stands between picking a cookies browser and starting a crawl. If that browser is holding its cookie
/// database open, gallery-dl can't read the Pinterest login out of it, the request goes out anonymous, and
/// every private board comes back "not found" — after a crawl that can run for minutes. The state is knowable
/// up front, so ask first and let the user close the browser rather than spending the run to find out.
/// <para>The popup itself belongs to the view, which supplies <see cref="Confirm"/>. The probe is headless,
/// so a view model under test (or any caller that never wires one) simply proceeds.</para>
/// </summary>
public sealed class CookieLockGuard
{
    private readonly Func<string?, bool> _isLocked;

    public CookieLockGuard() : this(BrowserCookies.IsCookieDbLocked) { }

    /// <summary>Seam for tests — a real locked database can't be staged portably (Windows and Unix disagree
    /// about what a share mode means), and the decision this makes is worth pinning on its own.</summary>
    internal CookieLockGuard(Func<string?, bool> isLocked) => _isLocked = isLocked;

    /// <summary>Set by the view: warn about this browser, resolving true to go ahead regardless.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>True when the import should proceed.</summary>
    public async Task<bool> AllowAsync(string? browser)
    {
        if (Confirm is not { } ask) return true;
        // Hits the file system, so keep it off the UI thread — a browser profile can sit on a slow disk.
        var locked = await Task.Run(() => _isLocked(browser));
        return !locked || await ask(BrowserCookies.DisplayName(browser));
    }
}
