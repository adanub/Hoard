using Hoard.Core.Domain;
using Hoard.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Sync;

/// <summary>
/// Appends archive ops (<c>SYNC-DESIGN.md</c>) alongside the DB changes they describe. Callers add the op
/// to the same <see cref="HoardDbContext"/> as the change, inside the same <c>SaveChanges</c>, so the log
/// can never drift from the data — the same contract as the older <see cref="SyncLog"/> stub, which stays
/// in place untouched (append-only promise) until the op log takes over as the source of truth.
///
/// One instance per device id: <see cref="ArchiveOp.Seq"/> is allocated from an in-process counter seeded
/// from the database on first use (and re-seeded when the underlying database changes — a project switch),
/// so concurrent contexts in one app never collide on the unique (DeviceId, Seq) index. The HLC is seeded
/// from the last persisted op the same way, so restarts stay monotonic.
/// </summary>
public sealed class ArchiveLog
{
    private readonly string _deviceId;
    private readonly HybridClock _clock;
    private readonly Func<string?>? _opsRoot;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private string? _seededFor;
    private long _lastSeq;
    private long _lastFlushedSeq = -1; // -1 = not yet read from the segment for this project

    /// <param name="opsRoot">Resolves the current project's ops directory for the P2 dual-write segment
    /// (<c>SYNC-DESIGN.md</c>); null (tests/headless) disables segment IO — the table still gets every op.</param>
    public ArchiveLog(string deviceId, Func<DateTimeOffset>? clock = null, Func<string?>? opsRoot = null, ILogger? logger = null)
    {
        _deviceId = deviceId;
        _clock = new HybridClock(deviceId, clock);
        _opsRoot = opsRoot;
        _logger = logger;
    }

    public string DeviceId => _deviceId;

    /// <summary>Advance the clock past an externally-seen timestamp (a foreign op applied by catch-up).</summary>
    public void Observe(string hlc) => _clock.Observe(hlc);

    /// <summary>
    /// Forget the cached flushed-seq watermark so the next flush re-derives it from the segment files —
    /// call after anything OUTSIDE this log writes the ops directory (a remote pull downloading this
    /// device's lost chapters). A stale-low cached watermark would make the next flush re-append ops the
    /// file already holds — duplicates baked into the append-only segment forever.
    /// </summary>
    public void InvalidateFlushWatermark()
    {
        lock (_gate) _lastFlushedSeq = -1;
    }

    /// <summary>
    /// Advance the per-device sequence past an op of OUR OWN device applied from the segment file —
    /// catch-up replaying our own history into a fresh/rebuilt index. Without this the next local op
    /// would re-mint an already-used seq and hit the unique (DeviceId, Seq) index.
    /// </summary>
    public void ObserveOwnSeq(long seq)
    {
        lock (_gate)
        {
            if (seq > _lastSeq) _lastSeq = seq;
        }
    }

    /// <summary>
    /// Seed (or re-seed after a project switch) the per-device sequence and the clock from the database
    /// this context points at. Call once at the top of any service method that will record ops — cheap
    /// after the first call per project.
    /// </summary>
    public async Task EnsureReadyAsync(HoardDbContext db, CancellationToken ct = default)
    {
        var stamp = db.Database.GetDbConnection().DataSource ?? "";
        lock (_gate)
        {
            if (_seededFor == stamp) return;
        }

        var lastSeq = await db.ArchiveOps.Where(o => o.DeviceId == _deviceId)
            .MaxAsync(o => (long?)o.Seq, ct).ConfigureAwait(false) ?? 0;
        var lastHlc = await db.ArchiveOps.OrderByDescending(o => o.Hlc)
            .Select(o => o.Hlc).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // The on-disk segment can be AHEAD of this table (a fresh/wiped index whose open-time catch-up
        // failed and was swallowed) — new ops must still mint beyond everything ever written to the file,
        // or they'd collide with the segment's history and silently never flush.
        if (_opsRoot?.Invoke() is { } opsRoot)
        {
            var segment = ArchiveSegments.ReadAll(opsRoot, _deviceId);
            if (segment.Count > 0)
            {
                lastSeq = Math.Max(lastSeq, segment[^1].Seq);
                _clock.Observe(segment[^1].Hlc);
            }
        }

        lock (_gate)
        {
            _seededFor = stamp;
            _lastSeq = lastSeq;
            _lastFlushedSeq = -1; // re-read the segment for the newly-stamped project
        }
        if (lastHlc is not null) _clock.Observe(lastHlc);
    }

    /// <summary>
    /// P2 dual-write: append this device's committed table ops beyond the segment's last seq to
    /// <c>ops/&lt;deviceId&gt;.jsonl</c>. Call <b>after</b> a successful SaveChanges — the table is the
    /// authority, so a crash, a failed append, or a missed call just re-lands next flush (and the same
    /// mechanism backfills a segment torn mid-append). Never throws: a segment IO failure must not fail
    /// the user's operation while the DB holds the truth.
    /// </summary>
    public async Task FlushSegmentAsync(HoardDbContext db, CancellationToken ct = default)
    {
        var opsRoot = _opsRoot?.Invoke();
        if (opsRoot is null) return;

        // One flush at a time: two overlapping flushes (a curation write racing an import's end-of-run
        // flush on this shared singleton) would both read the same watermark and append the same ops —
        // duplicate lines baked into the append-only file forever.
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long from;
            lock (_gate)
            {
                if (_lastFlushedSeq < 0)
                    _lastFlushedSeq = ArchiveSegments.LastSeq(opsRoot, _deviceId);
                from = _lastFlushedSeq;
            }

            var pending = await db.ArchiveOps
                .Where(o => o.DeviceId == _deviceId && o.Seq > from)
                .OrderBy(o => o.Seq)
                .AsNoTracking()
                .ToListAsync(ct).ConfigureAwait(false);
            if (pending.Count == 0) return;

            ArchiveSegments.Append(opsRoot, _deviceId, pending);
            lock (_gate)
            {
                if (_lastFlushedSeq == from) _lastFlushedSeq = pending[^1].Seq;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Op segment flush failed; will retry on the next write/open.");
            lock (_gate) _lastFlushedSeq = -1; // re-derive from the file next time
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // ---- asset lifecycle -------------------------------------------------------------------------

    public void RecordAssetAdded(HoardDbContext db, Asset asset, IReadOnlyList<string>? tags = null) =>
        Append(db, ArchiveOpKinds.AssetAdded, sha256: asset.Sha256, payload: ArchiveOpJson.Serialize(
            new AssetAddedPayload(
                CanonicalPath(asset.RelativePath), asset.MimeType, asset.Kind, asset.Width, asset.Height, asset.Bytes,
                asset.SourceConnector, asset.SourceId, asset.SourceUrl, asset.OriginalUrl,
                asset.Title, asset.Description, asset.MetadataJson,
                asset.CreatedAt, asset.ImportedAt, tags is { Count: > 0 } ? tags : null)));

    public void RecordAssetTombstoned(HoardDbContext db, string sha256, string note, DateTimeOffset deletedAt) =>
        Append(db, ArchiveOpKinds.AssetTombstoned, sha256: sha256,
            payload: ArchiveOpJson.Serialize(new AssetTombstonedPayload(note, deletedAt)));

    public void RecordAssetRestored(HoardDbContext db, string oldSha256, Asset asset) =>
        Append(db, ArchiveOpKinds.AssetRestored, sha256: oldSha256, payload: ArchiveOpJson.Serialize(
            new AssetContentChangedPayload(asset.Sha256, CanonicalPath(asset.RelativePath), asset.Bytes)));

    public void RecordAssetRefetched(HoardDbContext db, string oldSha256, Asset asset) =>
        Append(db, ArchiveOpKinds.AssetRefetched, sha256: oldSha256, payload: ArchiveOpJson.Serialize(
            new AssetContentChangedPayload(asset.Sha256, CanonicalPath(asset.RelativePath), asset.Bytes)));

    /// <summary>
    /// Op payloads carry store paths in canonical forward-slash form: a Windows-written row (or one
    /// synthesised from a pre-v2 database) may hold backslashes, and while the store's reader tolerates
    /// either, the segments are shared archive files — they must not leak a device's separator.
    /// </summary>
    private static string CanonicalPath(string relativePath) => relativePath.Replace('\\', '/');

    public void RecordAssetRetagged(HoardDbContext db, string sha256, IReadOnlyList<string> fullTagSet) =>
        Append(db, ArchiveOpKinds.AssetRetagged, sha256: sha256,
            payload: ArchiveOpJson.Serialize(new AssetRetaggedPayload(fullTagSet)));

    public void RecordAssetRemoved(HoardDbContext db, string sha256) =>
        Append(db, ArchiveOpKinds.AssetRemoved, sha256: sha256);

    // ---- collections / sources -------------------------------------------------------------------

    public void RecordCollectionCreated(HoardDbContext db, Collection collection, string? parentUid) =>
        Append(db, ArchiveOpKinds.CollectionCreated, entityUid: UidOf(collection), payload: ArchiveOpJson.Serialize(
            new CollectionCreatedPayload(
                collection.Name, parentUid, collection.SourceConnector, collection.SourceBoardId,
                collection.SourceUrl, collection.SourceSectionId, collection.CreatedAt)));

    public void RecordCollectionRenamed(HoardDbContext db, Collection collection) =>
        Append(db, ArchiveOpKinds.CollectionRenamed, entityUid: UidOf(collection),
            payload: ArchiveOpJson.Serialize(new CollectionRenamedPayload(collection.DisplayName ?? "")));

    public void RecordCollectionDeleted(HoardDbContext db, Collection collection) =>
        Append(db, ArchiveOpKinds.CollectionDeleted, entityUid: UidOf(collection));

    public void RecordSourceAttached(HoardDbContext db, CollectionSource source, Collection owner) =>
        Append(db, ArchiveOpKinds.SourceAttached, entityUid: UidOf(source), payload: ArchiveOpJson.Serialize(
            new SourceAttachedPayload(
                UidOf(owner), source.SourceConnector, source.SourceBoardId, source.SourceUrl, source.Name,
                source.AddedAt)));

    public void RecordSourceUpdated(HoardDbContext db, CollectionSource source) =>
        Append(db, ArchiveOpKinds.SourceUpdated, entityUid: UidOf(source),
            payload: ArchiveOpJson.Serialize(new SourceUpdatedPayload(source.SourceUrl)));

    public void RecordSourceRemoved(HoardDbContext db, CollectionSource source) =>
        Append(db, ArchiveOpKinds.SourceRemoved, entityUid: UidOf(source));

    // ---- links -----------------------------------------------------------------------------------

    public void RecordItemLinked(
        HoardDbContext db, string sha256, string collectionUid, string? sourceUid, string? note, DateTimeOffset addedAt) =>
        Append(db, ArchiveOpKinds.ItemLinked, sha256: sha256, entityUid: collectionUid,
            payload: ArchiveOpJson.Serialize(new ItemLinkedPayload(sourceUid, note, addedAt)));

    public void RecordItemUnlinked(HoardDbContext db, string sha256, string collectionUid) =>
        Append(db, ArchiveOpKinds.ItemUnlinked, sha256: sha256, entityUid: collectionUid);

    /// <summary>
    /// The entity's cross-device uid, minting one in place when absent (a row created by pre-op code or
    /// raw EF in tests) — the entity is tracked, so the assignment persists with the same SaveChanges.
    /// </summary>
    public static string UidOf(Collection collection) => collection.Uid ??= NewUid();
    public static string UidOf(CollectionSource source) => source.Uid ??= NewUid();
    public static string NewUid() => Guid.NewGuid().ToString("N");

    private void Append(HoardDbContext db, string kind, string? sha256 = null, string? entityUid = null, string? payload = null)
    {
        long seq;
        string hlc;
        lock (_gate)
        {
            if (_seededFor is null)
                throw new InvalidOperationException("ArchiveLog.EnsureReadyAsync must run before recording ops.");
            // Seq and HLC are allocated under the SAME gate: ticked outside it, two racing appends could
            // give the higher seq the lower timestamp, baking a seq/HLC order disagreement into the
            // immutable segment (seq-ordered and HLC-ordered replays would then diverge).
            seq = ++_lastSeq;
            hlc = _clock.Tick();
        }
        db.ArchiveOps.Add(new ArchiveOp
        {
            DeviceId = _deviceId,
            Seq = seq,
            Hlc = hlc,
            Kind = kind,
            Sha256 = sha256,
            EntityUid = entityUid,
            PayloadJson = payload,
        });
    }
}
