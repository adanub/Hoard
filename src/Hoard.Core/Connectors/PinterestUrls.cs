namespace Hoard.Core.Connectors;

/// <summary>
/// The Pinterest URL shapes Hoard has to build for itself. Sits in Core beside
/// <see cref="PinterestSidecarParser"/> — the other half of "what Hoard knows about Pinterest" — so it's
/// pure, testable, and has exactly one home.
/// </summary>
public static class PinterestUrls
{
    /// <summary>
    /// The crawl URL for one <i>section</i> (sub-folder) of a board: the board's own URL with
    /// <c>id:&lt;sectionId&gt;</c> appended. This is byte-for-byte the URL gallery-dl builds when it walks a
    /// board's sections itself (<c>root + board["url"] + "id:" + section["id"]</c>), so it resolves to the
    /// same extractor — which is what lets an incremental sync crawl each section as its own target rather
    /// than relying on the board crawl to reach them (it appends them <i>after</i> every board pin, where an
    /// early stop would cut them off).
    /// <para>Returns null when there's nothing usable to build from — a board URL that isn't an absolute
    /// http(s) URL, or a blank section id — so a caller can simply skip that target.</para>
    /// </summary>
    public static string? SectionUrl(string? boardUrl, string? sectionId)
    {
        if (string.IsNullOrWhiteSpace(boardUrl) || string.IsNullOrWhiteSpace(sectionId)) return null;
        if (!Uri.TryCreate(boardUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        // Build from the path alone: a stored URL may carry the query/fragment Pinterest hangs off a shared
        // link ("?invite_code=…"), and appending the section after those would be a different, dead URL.
        var root = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) return null; // the site root is not a board

        return $"{root}{path}/id:{sectionId.Trim()}";
    }
}
