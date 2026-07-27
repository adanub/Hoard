using Hoard.Core.Metadata;
using Hoard.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Library;

/// <summary>What one export run did: files copied, files already up to date, blobs absent from the store.</summary>
public sealed record ExportReport(int Copied, int UpToDate, int MissingBlobs);

/// <summary>
/// Materialises a board's subtree as a browsable <c>Board/Folder/image</c> tree at a destination the
/// user chose — the human-readable view of the archive. Read-only against the archive (the store stays
/// content-addressed; this copies blobs out), so it can run alongside anything that only appends.
/// Re-running is an incremental refresh: names are stable per asset (<see cref="ExportNames.FileName"/>),
/// and a destination file whose length already matches its blob is skipped.
/// </summary>
public sealed class BoardExporter
{
    private readonly IDbContextFactory<HoardDbContext> _dbFactory;
    private readonly IMediaStore _store;

    public BoardExporter(IDbContextFactory<HoardDbContext> dbFactory, IMediaStore store)
    {
        _dbFactory = dbFactory;
        _store = store;
    }

    /// <summary>
    /// Export the live assets of <paramref name="collectionId"/> and its descendant folders into
    /// <paramref name="destinationRoot"/>/&lt;board name&gt;/…. Tombstoned assets never export; a live
    /// asset whose blob is missing from the store is counted, not fatal. Writes are temp-file + rename,
    /// so a cancelled run leaves whole files only and the next run resumes where it left off.
    /// </summary>
    public Task<ExportReport> ExportAsync(
        int collectionId, string destinationRoot, IProgress<string>? progress = null, CancellationToken ct = default)
        => ExportRootsAsync([collectionId], destinationRoot, progress, ct);

    /// <summary>
    /// Export the WHOLE project — every top-level board and its folders — into
    /// <paramref name="destinationRoot"/>/&lt;project name&gt;/&lt;board&gt;/…, in one pass. Same naming,
    /// same up-to-date skip, same read-only treatment of the archive as a single board's export; the only
    /// difference is that every board shares one run, so progress counts the project and two boards whose
    /// names sanitise alike are disambiguated against each other.
    /// </summary>
    public async Task<ExportReport> ExportProjectAsync(
        string projectName, string destinationRoot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        List<int> rootIds;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            rootIds = await db.Collections
                .Where(c => c.ParentId == null)
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        // Every CollectionItem hangs off some collection and every collection roots at a top-level board,
        // so the top-level boards cover the project's live assets exactly.
        return await ExportRootsAsync(
            rootIds,
            Path.Combine(destinationRoot, ExportNames.ProjectFolderName(projectName)),
            progress, ct).ConfigureAwait(false);
    }

    private async Task<ExportReport> ExportRootsAsync(
        IReadOnlyList<int> rootIds, string destinationRoot, IProgress<string>? progress, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var subtreeIds = new List<int>();
        foreach (var rootId in rootIds)
            subtreeIds.AddRange(await CollectionTree.SubtreeIdsAsync(db, rootId, ct).ConfigureAwait(false));
        var collections = await db.Collections
            .Where(c => subtreeIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ParentId, Name = c.DisplayName ?? c.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var directories = BuildDirectories(
            rootIds, destinationRoot,
            collections.Select(c => (c.Id, c.ParentId, c.Name)).ToList());
        RefuseToWriteIntoTheArchive(directories.Values);

        var items = await db.CollectionItems
            .Where(ci => subtreeIds.Contains(ci.CollectionId) && ci.Asset.DeletedAt == null)
            .Select(ci => new
            {
                ci.CollectionId,
                ci.Asset.Title,
                ci.Asset.SourceId,
                ci.Asset.Sha256,
                ci.Asset.RelativePath,
                ci.Asset.CreatedAt,
                ci.Asset.ImportedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        int copied = 0, upToDate = 0, missing = 0, done = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            var source = new FileInfo(_store.GetAbsolutePath(item.RelativePath));
            if (!source.Exists)
            {
                missing++;
                continue;
            }

            var directory = directories[item.CollectionId];
            var destination = Path.Combine(
                directory,
                ExportNames.FileName(item.Title, item.SourceId, item.Sha256, Path.GetExtension(item.RelativePath)));

            // Blobs are immutable, so a same-length destination is the same content — already exported.
            var existing = new FileInfo(destination);
            if (existing.Exists && existing.Length == source.Length)
            {
                upToDate++;
                continue;
            }

            Directory.CreateDirectory(directory);
            var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source.FullName, temp);
                File.Move(temp, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The blob was freed between the Exists check above and the copy. An export refuses to
                // START during an import/sync but publishes no flag of its own, so one can begin under a
                // running export and tombstone a blob mid-run — count it like any other missing file
                // rather than killing a whole project's export over one image.
                missing++;
                continue;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            // Stamp the source's date (import time for Pinterest — the sidecar carries no per-pin date)
            // so sorting the exported folder by date means something.
            File.SetLastWriteTimeUtc(destination, (item.CreatedAt ?? item.ImportedAt).UtcDateTime);
            copied++;

            if (done % 20 == 0 || done == items.Count)
                progress?.Report($"Exporting… {done}/{items.Count}");
        }

        return new ExportReport(copied, upToDate, missing);
    }

    /// <summary>
    /// The archive folder holds only the archive, so an export must never land inside it — and the
    /// dangerous case isn't an obviously-silly destination but an innocent one: exporting to the
    /// project's own PARENT makes <c>&lt;parent&gt;/&lt;project or board name&gt;</c> resolve straight back
    /// onto the project folder. Checked here, on the directories actually about to be written, so it
    /// holds for every caller rather than only the ones that remembered to look.
    /// </summary>
    private void RefuseToWriteIntoTheArchive(IEnumerable<string> directories)
    {
        // The store is <project>/store by construction, so its parent is the archive folder.
        if (Directory.GetParent(Path.GetFullPath(_store.Root))?.FullName is not { } projectRoot) return;
        var prefix = projectRoot.EndsWith(Path.DirectorySeparatorChar)
            ? projectRoot
            : projectRoot + Path.DirectorySeparatorChar;

        foreach (var directory in directories)
        {
            var full = Path.GetFullPath(directory);
            if (full.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "That would export into the project folder itself — choose a folder outside it.");
        }
    }

    /// <summary>
    /// Map every collection in the subtrees to its destination directory: each root board becomes a folder
    /// under the destination, each child nests under its parent. Sibling names that sanitise to the same
    /// component are disambiguated with the collection id (case-insensitively — exports must survive
    /// case-preserving file systems). The ROOTS are siblings of each other, so a whole-project run
    /// disambiguates two boards that sanitise alike — which per-board runs could never do.
    /// </summary>
    private static Dictionary<int, string> BuildDirectories(
        IReadOnlyList<int> rootIds, string destinationRoot, IReadOnlyList<(int Id, int? ParentId, string Name)> collections)
    {
        var roots = rootIds.ToHashSet();
        var byParent = collections
            .Where(c => !roots.Contains(c.Id))
            .ToLookup(c => c.ParentId);
        var directories = new Dictionary<int, string>();
        var pending = new Queue<int>();

        var usedAtRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootId in rootIds.OrderBy(id => id))
        {
            if (collections.FirstOrDefault(c => c.Id == rootId) is not { Id: > 0 } root) continue;
            directories[rootId] = Path.Combine(destinationRoot, Distinct(root.Name, rootId, usedAtRoot));
            pending.Enqueue(rootId);
        }

        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in byParent[parentId].OrderBy(c => c.Id))
            {
                directories[child.Id] = Path.Combine(directories[parentId], Distinct(child.Name, child.Id, used));
                pending.Enqueue(child.Id);
            }
        }

        return directories;

        static string Distinct(string? name, int id, HashSet<string> used)
        {
            var folder = ExportNames.FolderName(name, id);
            if (!used.Add(folder))
            {
                folder = $"{folder} [{id}]";
                used.Add(folder);
            }
            return folder;
        }
    }
}
