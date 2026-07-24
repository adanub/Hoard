using System.Text;
using System.Text.Json;
using Hoard.Core.Domain;

namespace Hoard.Core.Sync;

/// <summary>
/// The on-disk op segment format (<c>SYNC-DESIGN.md</c> P2): per device, one append-only JSON Lines file
/// <c>ops/&lt;deviceId&gt;.jsonl</c> holding that device's ops in seq order. The device id lives in the
/// FILENAME (one writer per file is the whole concurrency model), so lines carry only seq/hlc/kind/keys/
/// payload. Appends are open→append→flush→close per batch — no held handles, which plays nicest with SMB
/// client caching. Reads tolerate a torn trailing line (a crash mid-append): the reader stops at the
/// first unparsable line, and because the writer re-derives what to append from the authoritative table
/// (everything beyond the last <i>valid</i> seq), the torn op is simply re-landed by the next flush.
/// </summary>
public static class ArchiveSegments
{
    public const string DirectoryName = "ops";
    private const string Extension = ".jsonl";

    public static string SegmentPath(string opsRoot, string deviceId) =>
        Path.Combine(opsRoot, deviceId + Extension);

    /// <summary>Every segment in an ops directory, as (deviceId, path). Missing directory = no segments.</summary>
    public static IReadOnlyList<(string DeviceId, string Path)> ListSegments(string opsRoot)
    {
        if (!Directory.Exists(opsRoot)) return [];
        return Directory.EnumerateFiles(opsRoot, "*" + Extension)
            .Select(p => (Path.GetFileNameWithoutExtension(p), p))
            .Where(s => s.Item1.Length > 0)
            .OrderBy(s => s.Item1, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Append ops (already in seq order) to a device's segment. Creates the directory/file on
    /// first write, and repairs a torn tail first — a crashed append leaves a partial line (our lines
    /// contain no embedded newline, so torn = bytes after the last '\n'); appending blindly would weld
    /// the next line onto that garbage and poison the file mid-stream. The caller guarantees the ops
    /// belong to the segment's device.</summary>
    public static void Append(string opsRoot, string deviceId, IEnumerable<ArchiveOp> ops)
    {
        Directory.CreateDirectory(opsRoot);
        using var stream = new FileStream(
            SegmentPath(opsRoot, deviceId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        TruncateTornTail(stream);
        stream.Seek(0, SeekOrigin.End);
        foreach (var op in ops)
        {
            var line = Serialise(op);
            stream.Write(line, 0, line.Length);
        }
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Cut the file back to just after its last newline (every complete line is '\n'-terminated).
    /// Scans a widening tail window so even a torn line longer than the first window is found.</summary>
    private static void TruncateTornTail(FileStream stream)
    {
        if (stream.Length == 0) return;
        for (long window = 64 * 1024; ; window *= 8)
        {
            var tail = Math.Min(stream.Length, window);
            var buffer = new byte[tail];
            stream.Seek(-tail, SeekOrigin.End);
            stream.ReadExactly(buffer);
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
            if (lastNewline >= 0)
            {
                var validLength = stream.Length - tail + lastNewline + 1;
                if (validLength < stream.Length) stream.SetLength(validLength);
                return;
            }
            if (tail == stream.Length)
            {
                stream.SetLength(0); // no newline anywhere — the whole file is one torn line
                return;
            }
        }
    }

    /// <summary>
    /// Read a segment's ops (in file = seq order), assigning <paramref name="deviceId"/> to each. Stops
    /// at the first unparsable line — a torn tail from a crashed append; the authoritative-table flush
    /// re-lands anything beyond the last valid seq.
    /// </summary>
    public static IReadOnlyList<ArchiveOp> Read(string path, string deviceId)
    {
        var ops = new List<ArchiveOp>();
        if (!File.Exists(path)) return ops;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            ArchiveOp op;
            try
            {
                op = Parse(line, deviceId);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Torn/garbled line. Not only malformed JSON: a shape-damaged but well-formed line throws
                // KeyNotFound/InvalidOperation from GetProperty/GetInt64 — those must stop the read the
                // same way, not escape and fail the whole segment (which would wedge every open + flush).
                break; // everything before it stands
            }
            ops.Add(op);
        }
        return ops;
    }

    /// <summary>The last valid seq in a segment (0 for a missing/empty file) — the flush watermark.</summary>
    public static long LastSeq(string path, string deviceId)
    {
        var ops = Read(path, deviceId);
        return ops.Count == 0 ? 0 : ops[^1].Seq;
    }

    private static byte[] Serialise(ArchiveOp op)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", op.Seq);
            writer.WriteString("hlc", op.Hlc);
            writer.WriteString("op", op.Kind);
            if (op.Sha256 is not null) writer.WriteString("sha", op.Sha256);
            if (op.EntityUid is not null) writer.WriteString("uid", op.EntityUid);
            if (op.PayloadJson is not null)
            {
                writer.WritePropertyName("payload");
                using var payload = JsonDocument.Parse(op.PayloadJson);
                payload.RootElement.WriteTo(writer); // embedded as real JSON, not a double-encoded string
            }
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static ArchiveOp Parse(string line, string deviceId)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        return new ArchiveOp
        {
            DeviceId = deviceId,
            Seq = root.GetProperty("seq").GetInt64(),
            Hlc = root.GetProperty("hlc").GetString() ?? throw new JsonException("hlc missing"),
            Kind = root.GetProperty("op").GetString() ?? throw new JsonException("op missing"),
            Sha256 = root.TryGetProperty("sha", out var sha) ? sha.GetString() : null,
            EntityUid = root.TryGetProperty("uid", out var uid) ? uid.GetString() : null,
            PayloadJson = root.TryGetProperty("payload", out var payload) ? payload.GetRawText() : null,
        };
    }
}
