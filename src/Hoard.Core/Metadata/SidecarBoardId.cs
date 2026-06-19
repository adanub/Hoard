using System.Text.Json;

namespace Hoard.Core.Metadata;

/// <summary>
/// Extracts the originating Pinterest board id from a stored sidecar (<c>Asset.MetadataJson</c>) — nested
/// <c>{"board":{"id":…}}</c> or flat <c>"board_id"</c>. The board id is provenance we can recover when an asset's
/// live <c>CollectionItem</c> links are gone (the legacy attribution backfill and the on-import orphan re-attach
/// both need it). Mirrors <c>PinterestSidecarParser</c>'s extraction (string as-is, number stringified) so it
/// matches what was stored in <c>CollectionSource.SourceBoardId</c>.
/// </summary>
internal static class SidecarBoardId
{
    public static string? From(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("board", out var board) && board.ValueKind == JsonValueKind.Object)
                return Scalar(board, "id");
            return Scalar(root, "board_id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Scalar(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null,
        };
    }
}
