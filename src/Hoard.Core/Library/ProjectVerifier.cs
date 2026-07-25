using System.Security.Cryptography;
using Hoard.Core.Metadata;
using Hoard.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Library;

public enum VerifyIssueKind
{
    /// <summary>A live asset whose blob is gone from the store (deleted/moved outside the app).</summary>
    MissingBlob,
    /// <summary>A live asset whose blob's bytes no longer hash to its recorded SHA-256.</summary>
    AlteredBlob,
    /// <summary>A store file no live asset references (an interrupted import, or freed-tombstone leftovers).</summary>
    OrphanBlob,
}

/// <param name="Path">The store-relative path of the affected file.</param>
/// <param name="Title">The owning asset's title, when the issue has an owner (not for orphans).</param>
public sealed record VerifyIssue(VerifyIssueKind Kind, string Path, string? Title = null);

public sealed record VerifyReport(int LiveAssets, int BlobsHashed, IReadOnlyList<VerifyIssue> Issues)
{
    public bool IsClean => Issues.Count == 0;
    public int Missing => Issues.Count(i => i.Kind == VerifyIssueKind.MissingBlob);
    public int Altered => Issues.Count(i => i.Kind == VerifyIssueKind.AlteredBlob);
    public int Orphans => Issues.Count(i => i.Kind == VerifyIssueKind.OrphanBlob);
}

/// <summary>
/// The on-demand deep check deliberately kept OFF the open path (open stays cheap — schema + marker +
/// recents): every live asset's blob is re-hashed against its recorded SHA-256, and the store is swept
/// for files nothing references. <b>Report-only</b>: orphans are never deleted here — on a shared (NAS)
/// archive a blob can legitimately precede this machine's knowledge of it (another device's import lands
/// the blob before its ops are caught up; op-implies-blob only holds per device), so an automatic sweep
/// could destroy a sibling's fresh data. Tombstoned assets are skipped: their blobs are freed by design
/// (restore re-downloads from the source), so a lingering one correctly reports as an orphan.
/// </summary>
public static class ProjectVerifier
{
    /// <summary>
    /// Verify a project by folder path, resolving this machine's database the same way
    /// <see cref="ProjectStatsReader"/> does. Returns null when there's no local database to check
    /// against (a v2 archive this machine hasn't opened/indexed yet).
    /// </summary>
    public static async Task<VerifyReport?> VerifyAsync(
        string projectFolder, AppPaths appPaths, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var dbPath = appPaths.LocalDatabasePath(projectFolder);
        if (!File.Exists(dbPath)) return null;

        await using var db = ProjectDbContextFactory.CreateForPath(dbPath);

        // A v2 index can be STALE relative to the shared archive (another machine's ops not yet caught
        // up) — verifying against it would report phantom corruption: that machine's deletions as
        // "missing", its new imports as "unreferenced". Reconcile from the segments first, exactly as
        // an open does. Best-effort: an unreadable share would fail the store scan below anyway.
        var opsRoot = Path.Combine(projectFolder, Sync.ArchiveSegments.DirectoryName);
        if (Directory.Exists(opsRoot))
        {
            try
            {
                await Metadata.SchemaInitializer.InitializeAsync(db, ct).ConfigureAwait(false);
                var deviceId = Sync.DeviceIdentity.GetOrCreate(appPaths);
                await Sync.ArchiveSync.CatchUpAsync(db, opsRoot, new Sync.ArchiveLog(deviceId), null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Verify what we hold — the report is still self-consistent with this machine's view.
            }
        }

        return await VerifyAsync(db, HoardProject.StoreDir(projectFolder), progress, ct).ConfigureAwait(false);
    }

    public static async Task<VerifyReport> VerifyAsync(
        HoardDbContext db, string storeRoot, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var assets = await db.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .Select(a => new { a.Sha256, a.RelativePath, a.Title })
            .ToListAsync(ct).ConfigureAwait(false);

        var issues = new List<VerifyIssue>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashed = 0;
        var done = 0;
        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
            var absolute = Path.GetFullPath(Path.Combine(storeRoot, Normalise(asset.RelativePath)));
            referenced.Add(absolute);
            if (!File.Exists(absolute))
            {
                issues.Add(new VerifyIssue(VerifyIssueKind.MissingBlob, asset.RelativePath, asset.Title));
            }
            else
            {
                if (await HashAsync(absolute, ct).ConfigureAwait(false) != asset.Sha256)
                    issues.Add(new VerifyIssue(VerifyIssueKind.AlteredBlob, asset.RelativePath, asset.Title));
                hashed++;
            }
            progress?.Report((++done, assets.Count));
        }

        if (Directory.Exists(storeRoot))
        {
            foreach (var file in Directory.EnumerateFiles(storeRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!referenced.Contains(Path.GetFullPath(file)))
                    issues.Add(new VerifyIssue(VerifyIssueKind.OrphanBlob, Path.GetRelativePath(storeRoot, file).Replace('\\', '/')));
            }
        }

        return new VerifyReport(assets.Count, hashed, issues);
    }

    // The store's own tolerance rule: rows written by old Windows builds may hold '\'-separated paths.
    private static string Normalise(string relativePath)
        => relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }
}
