using System.Text;
using System.Text.Json;
using Hoard.Core.Domain;

namespace Hoard.Core.Sync;

/// <summary>
/// The on-disk op segment format (<c>SYNC-DESIGN.md</c> P2): per device, an append-only JSON Lines
/// stream holding that device's ops in seq order. The device id lives in the FILENAME (one writer per
/// device is the whole concurrency model), so lines carry only seq/hlc/kind/keys/payload. The stream is
/// cut into <b>chapters</b> at a size threshold (P4 rotation): <c>&lt;deviceId&gt;.jsonl</c> is chapter
/// zero, rotated continuations are <c>&lt;deviceId&gt;.00001.jsonl</c>, … — the writer appends only to
/// the highest-numbered chapter, so a chapter is CLOSED (name and content final, forever) merely by a
/// higher one existing. No file is ever renamed: a closed chapter is exactly what object storage and
/// third-party sync want — an immutable blob — and compaction can later retire whole closed chapters.
/// Appends are open→append→flush→close per batch — no held handles, which plays nicest with SMB client
/// caching. Reads tolerate a torn trailing line (a crash mid-append): the reader stops at the first
/// unparsable line, and because the writer re-derives what to append from the authoritative table
/// (everything beyond the last <i>valid</i> seq), the torn op is simply re-landed by the next flush.
/// </summary>
public static class ArchiveSegments
{
    public const string DirectoryName = "ops";
    private const string Extension = ".jsonl";

    /// <summary>Rotate the active chapter once it reaches this size. A chapter may exceed it by at most
    /// one line (the check runs before each write, so a line never splits across chapters).</summary>
    public const long DefaultRotateBytes = 4 * 1024 * 1024;

    /// <summary>Chapter zero of a device's stream — the original single-segment path, unchanged so
    /// pre-rotation archives need no migration.</summary>
    public static string SegmentPath(string opsRoot, string deviceId) =>
        Path.Combine(opsRoot, deviceId + Extension);

    private static string ChapterPath(string opsRoot, string deviceId, int chapter) =>
        chapter == 0 ? SegmentPath(opsRoot, deviceId) : Path.Combine(opsRoot, $"{deviceId}.{chapter:D5}{Extension}");

    /// <summary>Every device with at least one chapter in an ops directory. Missing directory = none.</summary>
    public static IReadOnlyList<string> ListDevices(string opsRoot)
    {
        if (!Directory.Exists(opsRoot)) return [];
        return Directory.EnumerateFiles(opsRoot, "*" + Extension)
            .Select(p => ParseName(p)?.DeviceId)
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A device's existing chapters in stream order (chapter zero first). Empty when none.</summary>
    public static IReadOnlyList<(string Path, int Chapter)> ListChapters(string opsRoot, string deviceId)
    {
        if (!Directory.Exists(opsRoot)) return [];
        return Directory.EnumerateFiles(opsRoot, "*" + Extension)
            .Select(p => (Path: p, Parsed: ParseName(p)))
            .Where(x => x.Parsed is { } parsed && parsed.DeviceId == deviceId)
            .Select(x => (x.Path, x.Parsed!.Value.Chapter))
            .OrderBy(x => x.Chapter)
            .ToList();
    }

    /// <summary>A device's whole op stream — every chapter, concatenated in order (= seq order, since
    /// the one writer appends monotonically).</summary>
    public static IReadOnlyList<ArchiveOp> ReadAll(string opsRoot, string deviceId)
    {
        var ops = new List<ArchiveOp>();
        foreach (var (path, _) in ListChapters(opsRoot, deviceId))
            ops.AddRange(Read(path, deviceId));
        return ops;
    }

    /// <summary>The last valid seq across a device's whole chain (0 when it has none) — the flush watermark.
    /// Walks chapters from the newest so a fresh chain doesn't read the entire history.</summary>
    public static long LastSeq(string opsRoot, string deviceId)
    {
        foreach (var (path, _) in ListChapters(opsRoot, deviceId).Reverse())
        {
            var ops = Read(path, deviceId);
            if (ops.Count > 0) return ops[^1].Seq;
        }
        return 0;
    }

    /// <summary>Append ops (already in seq order) to a device's stream, rotating into a new chapter
    /// whenever the active one reaches <paramref name="rotateBytes"/>. Creates the directory/chapter on
    /// first write, and repairs a torn tail first — a crashed append leaves a partial line (our lines
    /// contain no embedded newline, so torn = bytes after the last '\n'); appending blindly would weld
    /// the next line onto that garbage and poison the file mid-stream. Only the ACTIVE (highest) chapter
    /// is ever opened for writing — closed chapters are immutable. The caller guarantees the ops belong
    /// to this device.</summary>
    public static void Append(string opsRoot, string deviceId, IEnumerable<ArchiveOp> ops, long rotateBytes = DefaultRotateBytes)
    {
        Directory.CreateDirectory(opsRoot);
        var chapters = ListChapters(opsRoot, deviceId);
        var (activePath, activeChapter) = chapters.Count > 0 ? chapters[^1] : (SegmentPath(opsRoot, deviceId), 0);

        var stream = new FileStream(activePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            TruncateTornTail(stream);
            stream.Seek(0, SeekOrigin.End);
            foreach (var op in ops)
            {
                if (stream.Length >= rotateBytes)
                {
                    stream.Flush(flushToDisk: true);
                    stream.Dispose();
                    activeChapter++;
                    activePath = ChapterPath(opsRoot, deviceId, activeChapter);
                    // OpenOrCreate + tail repair: a crash between creating this chapter and finishing
                    // its first line leaves a torn stub the next rotation would otherwise weld onto.
                    stream = new FileStream(activePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    TruncateTornTail(stream);
                    stream.Seek(0, SeekOrigin.End);
                }
                var line = Serialise(op);
                stream.Write(line, 0, line.Length);
            }
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>The device a segment file belongs to, from its name alone (null for non-segment files) —
    /// lets the replicator apply per-device rules to remote listings it can't ListChapters over.</summary>
    public static string? SegmentDevice(string fileName) => ParseName(fileName)?.DeviceId;

    /// <summary>
    /// Split a segment filename into (deviceId, chapter). <c>dev.jsonl</c> → chapter 0;
    /// <c>dev.00017.jsonl</c> → chapter 17. Null for non-segment files. A dot + exactly five digits is
    /// always a chapter suffix — production device ids are 32-hex GUIDs, which can't end that way.
    /// </summary>
    private static (string DeviceId, int Chapter)? ParseName(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(Extension, StringComparison.Ordinal)) return null;
        var stem = name[..^Extension.Length];
        if (stem.Length > 6 && stem[^6] == '.' && stem[^5..].All(char.IsAsciiDigit))
            return (stem[..^6], int.Parse(stem[^5..]));
        return stem.Length > 0 ? (stem, 0) : null;
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
    public static IReadOnlyList<ArchiveOp> Read(string path, string deviceId) => ReadFrom(path, deviceId, 0);

    /// <summary>
    /// The length of a segment's WHOLE-LINE prefix: the byte just past its last newline (0 when it holds
    /// none). Every complete line is '\n'-terminated, so this is the file's meaningful content length —
    /// bytes beyond it are a torn tail from a crashed append, which the next append repairs.
    /// <para>Replication compares copies on this rather than on raw length: a torn tail pushed to a
    /// remote by an older build would otherwise look "longer" than the repaired local copy forever, and
    /// the chapter would never converge.</para>
    /// </summary>
    public static long ValidLength(string path)
    {
        if (!File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return 0;
        for (long window = 64 * 1024; ; window *= 8)
        {
            var tail = Math.Min(stream.Length, window);
            var buffer = new byte[tail];
            stream.Seek(-tail, SeekOrigin.End);
            stream.ReadExactly(buffer);
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
            if (lastNewline >= 0) return stream.Length - tail + lastNewline + 1;
            if (tail == stream.Length) return 0; // no newline anywhere — the whole file is one torn line
        }
    }

    /// <summary>
    /// Read only the ops stored at or after <paramref name="offset"/> bytes — the delta replicator's
    /// window ("everything the remote copy of this chapter doesn't have yet"), so a steady-state push
    /// parses nothing and a growing chapter parses only its new tail.
    /// <para>The offset comes from ANOTHER copy's length, so it is trusted only when it lands on a line
    /// boundary: a chapter uploaded with a torn tail the local writer has since repaired past would
    /// otherwise start the read mid-line. When the byte before the offset isn't a newline the whole
    /// chapter is read instead — a safe superset (spare work, never a missed op).</para>
    /// </summary>
    public static IReadOnlyList<ArchiveOp> ReadFrom(string path, string deviceId, long offset)
    {
        var ops = new List<ArchiveOp>();
        if (!File.Exists(path)) return ops;

        // FileShare.ReadWrite, deliberately: Append holds the ACTIVE chapter open (FileAccess.ReadWrite,
        // FileShare.Read), and a stricter share mode here would fail the read outright mid-flush.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > 0)
        {
            if (offset >= stream.Length) return ops;
            stream.Seek(offset - 1, SeekOrigin.Begin);
            // Not a line boundary (an offset taken from a copy carrying a torn tail): read the whole
            // chapter instead of guessing where the next line starts. Spare work, never a missed op.
            if (stream.ReadByte() != '\n') stream.Seek(0, SeekOrigin.Begin);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
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
