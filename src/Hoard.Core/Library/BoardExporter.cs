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
    public async Task<ExportReport> ExportAsync(
        int collectionId, string destinationRoot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var subtreeIds = await CollectionTree.SubtreeIdsAsync(db, collectionId, ct).ConfigureAwait(false);
        var collections = await db.Collections
            .Where(c => subtreeIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ParentId, Name = c.DisplayName ?? c.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var directories = BuildDirectories(
            collectionId, destinationRoot,
            collections.Select(c => (c.Id, c.ParentId, c.Name)).ToList());

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
    /// Map every collection in the subtree to its destination directory: the root board becomes a folder
    /// under the destination, each child nests under its parent. Sibling names that sanitise to the same
    /// component are disambiguated with the collection id (case-insensitively — exports must survive
    /// case-preserving file systems).
    /// </summary>
    private static Dictionary<int, string> BuildDirectories(
        int rootId, string destinationRoot, IReadOnlyList<(int Id, int? ParentId, string Name)> collections)
    {
        var byParent = collections
            .Where(c => c.Id != rootId)
            .ToLookup(c => c.ParentId);
        var directories = new Dictionary<int, string>();

        var root = collections.First(c => c.Id == rootId);
        directories[rootId] = Path.Combine(destinationRoot, ExportNames.FolderName(root.Name, rootId));

        var pending = new Queue<int>();
        pending.Enqueue(rootId);
        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in byParent[parentId].OrderBy(c => c.Id))
            {
                var name = ExportNames.FolderName(child.Name, child.Id);
                if (!used.Add(name))
                {
                    name = $"{name} [{child.Id}]";
                    used.Add(name);
                }
                directories[child.Id] = Path.Combine(directories[parentId], name);
                pending.Enqueue(child.Id);
            }
        }

        return directories;
    }
}
