using Hoard.Core.Connectors;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Metadata;

/// <summary>
/// One-time v9 data step: stamp each asset's first-class pin provenance —
/// <see cref="Domain.Asset.SourceBoardId"/> / <see cref="Domain.Asset.SourceSectionId"/> — from the
/// stored sidecar, via the one shared <see cref="PinterestSidecarParser"/>. Idempotent (only rows still
/// missing a board id are touched); a row whose sidecar carries no board stays null and self-heals on
/// its pin's next re-crawl (the pin-keyed upsert refreshes provenance in place). Index-local repair, so
/// no archive ops are emitted — a rebuilt index gets the same values from op replay instead.
/// </summary>
internal static class ProvenanceBackfill
{
    public static async Task RunAsync(HoardDbContext db, CancellationToken ct = default)
    {
        var assets = await db.Assets
            .Where(a => a.SourceBoardId == null && a.MetadataJson != null)
            .ToListAsync(ct).ConfigureAwait(false);

        var dirty = false;
        foreach (var asset in assets)
        {
            var item = PinterestSidecarParser.TryParse(asset.MetadataJson);
            if (item?.BoardId is null) continue;
            asset.SourceBoardId = item.BoardId;
            asset.SourceSectionId ??= item.SectionId;
            dirty = true;
        }
        if (dirty) await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
