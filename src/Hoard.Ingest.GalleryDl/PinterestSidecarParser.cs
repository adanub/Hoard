using System.Globalization;
using System.Text.Json;
using Hoard.Core.Connectors;

namespace Hoard.Ingest.GalleryDl;

/// <summary>
/// Parses a gallery-dl Pinterest metadata sidecar (the <c>&lt;file&gt;.json</c> twin) into a
/// <see cref="SourceMediaItem"/>. gallery-dl has no fixed sidecar schema and Pinterest's fields
/// drift over time, so every field is looked up defensively across known aliases and the raw
/// JSON is always retained on the item for future re-parsing.
/// </summary>
public static class PinterestSidecarParser
{
    public const string ConnectorName = "pinterest";

    public static SourceMediaItem Parse(string mediaFilePath, string sidecarJson)
    {
        using var doc = JsonDocument.Parse(sidecarJson);
        var root = doc.RootElement;

        var pinId = GetString(root, "pin_id", "id");
        var sourceUrl = GetString(root, "url", "pin_url")
                        ?? (pinId is not null ? $"https://www.pinterest.com/pin/{pinId}/" : null);

        var (boardName, boardId, boardUrl) = ParseBoard(root);
        var (sectionId, sectionName, sectionUrl) = ParseSection(root, boardUrl);

        return new SourceMediaItem
        {
            FilePath = mediaFilePath,
            Connector = ConnectorName,
            SourceId = pinId,
            SourceUrl = sourceUrl,
            OriginalUrl = GetString(root, "link", "domain_link", "source_url"),
            Title = NullIfBlank(GetString(root, "title", "grid_title")),
            Description = NullIfBlank(GetString(root, "description", "note", "alt_text")),
            Width = GetDimension(root, "width"),
            Height = GetDimension(root, "height"),
            CreatedAt = ParseDate(GetString(root, "created_at", "created")),
            BoardName = boardName,
            BoardId = boardId,
            BoardUrl = boardUrl,
            SectionId = sectionId,
            SectionName = sectionName,
            SectionUrl = sectionUrl,
            Tags = ParseTags(root),
            RawJson = sidecarJson,
        };
    }

    /// <summary>
    /// Extract the pin's <i>section</i> (the sub-folder within a board it sits in), when present. gallery-dl
    /// attaches a <c>section</c> object (id + title/slug) to pins crawled from a board section; we also probe
    /// flat keys defensively, since Pinterest's metadata shape drifts. Returns nulls for a sectionless pin.
    /// (The exact field shape should be confirmed against a live gallery-dl crawl.)
    /// </summary>
    private static (string? id, string? name, string? url) ParseSection(JsonElement root, string? boardUrl)
    {
        if (root.TryGetProperty("section", out var section) && section.ValueKind == JsonValueKind.Object)
        {
            var id = NormaliseSectionId(GetString(section, "id"));
            if (id is not null)
            {
                var name = NullIfBlank(GetString(section, "title", "name"));
                var slug = GetString(section, "slug");
                var url = boardUrl is not null && !string.IsNullOrWhiteSpace(slug)
                    ? boardUrl.TrimEnd('/') + "/" + slug!.Trim('/') + "/"
                    : null;
                return (id, name, url);
            }
            // A section object with no usable id falls through to the flat probe below rather than short-circuiting.
        }

        var flatId = NormaliseSectionId(GetString(root, "section_id", "board_section_id"));
        if (flatId is not null)
            return (flatId, NullIfBlank(GetString(root, "section_title", "section_name", "board_section_title")), null);

        return (null, null, null);
    }

    // A sectionless pin may carry section_id 0/"0"/"" — treat those as "no section".
    private static string? NormaliseSectionId(string? id)
        => string.IsNullOrWhiteSpace(id) || id == "0" ? null : id;

    private static (string? name, string? id, string? url) ParseBoard(JsonElement root)
    {
        // Board may be a nested object or flattened "board_name"/"board_id" keys.
        if (root.TryGetProperty("board", out var board) && board.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(board, "name");
            var id = GetString(board, "id");
            var url = GetString(board, "url");
            if (url is not null && url.StartsWith('/')) url = "https://www.pinterest.com" + url;
            return (name, id, url);
        }
        return (GetString(root, "board_name"), GetString(root, "board_id"), GetString(root, "board_url"));
    }

    private static IReadOnlyList<string> ParseTags(JsonElement root)
    {
        foreach (var key in new[] { "hashtags", "tags" })
        {
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var tags = new List<string>();
                foreach (var el in arr.EnumerateArray())
                {
                    var s = el.ValueKind == JsonValueKind.String ? el.GetString() : GetString(el, "name");
                    if (!string.IsNullOrWhiteSpace(s)) tags.Add(s.Trim().TrimStart('#'));
                }
                if (tags.Count > 0) return tags;
            }
        }
        return Array.Empty<string>();
    }

    private static int? GetDimension(JsonElement root, string key)
    {
        // Try a top-level scalar first, then a couple of common nested locations.
        if (TryGetInt(root, key, out var v)) return v;
        foreach (var container in new[] { "media", "image", "images" })
        {
            if (root.TryGetProperty(container, out var c) && c.ValueKind == JsonValueKind.Object && TryGetInt(c, key, out v))
                return v;
        }
        return null;
    }

    private static bool TryGetInt(JsonElement obj, string key, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value)) return true;
        return false;
    }

    private static string? GetString(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var el))
            {
                if (el.ValueKind == JsonValueKind.String) return el.GetString();
                if (el.ValueKind == JsonValueKind.Number) return el.ToString();
            }
        }
        return null;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // ISO 8601 first, then Pinterest's RFC-1123-ish "Tue, 21 Jun 2022 16:38:45" form.
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto;
        if (DateTimeOffset.TryParseExact(s, "ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out dto))
            return dto;
        return null;
    }
}
