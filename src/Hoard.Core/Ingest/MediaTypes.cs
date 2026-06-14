using Hoard.Core.Domain;

namespace Hoard.Core.Ingest;

public static class MediaTypes
{
    public static (MediaKind Kind, string Mime) FromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => (MediaKind.Image, "image/jpeg"),
            ".png" => (MediaKind.Image, "image/png"),
            ".webp" => (MediaKind.Image, "image/webp"),
            ".bmp" => (MediaKind.Image, "image/bmp"),
            ".gif" => (MediaKind.Gif, "image/gif"),
            ".mp4" => (MediaKind.Video, "video/mp4"),
            ".m4v" => (MediaKind.Video, "video/x-m4v"),
            ".webm" => (MediaKind.Video, "video/webm"),
            ".mov" => (MediaKind.Video, "video/quicktime"),
            _ => (MediaKind.Unknown, "application/octet-stream"),
        };
}
