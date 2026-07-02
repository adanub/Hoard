using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Hoard.Core.Library;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>Shared card-grid helpers for the tile collections that hold <see cref="BoardCardRef"/>s (the Library
/// grid and the Board screen's folder row).</summary>
internal static class CardTiles
{
    /// <summary>Clear a card grid, disposing each card first — a card owns native cover bitmaps, so just dropping
    /// the references would strand them until finalization (and an in-flight cover load checks the disposed flag).</summary>
    public static void DisposeAndClear(this ObservableCollection<ViewModelBase> tiles)
    {
        foreach (var card in tiles.OfType<BoardCardRef>()) card.Dispose();
        tiles.Clear();
    }
}

/// <summary>
/// Loads the 3-up collage covers for a set of board/folder cards — shared by the Library grid and the Board
/// screen's folder row, which render identical <see cref="Controls.BoardCard"/>s. Per-card try/catch so one bad
/// card (e.g. a missing blob) can't abort covers for the rest.
/// </summary>
internal static class BoardCardCovers
{
    public static async Task LoadAsync(
        IEnumerable<BoardCardRef> cards, LibraryService library, ThumbnailCache? thumbnails)
    {
        foreach (var r in cards)
        {
            // The load is fire-and-forget and grids rebuild under it (OnResumed, import-end, the debounced folder
            // reload) — a rebuild disposes its old cards mid-loop. Check after EVERY await (all on the UI thread, so
            // check-then-assign can't race Dispose) and free rather than assign: a bitmap landed on a disposed card
            // is stranded native memory — nothing swaps or disposes it again.
            if (r.IsDisposed) continue;
            try
            {
                var covers = await library.GetCoverAssetsAsync(r.CollectionId, 3);
                for (var i = 0; i < covers.Count; i++)
                {
                    var bmp = thumbnails is not null
                        ? await thumbnails.GetAsync(covers[i].Sha256, covers[i].AbsolutePath)
                        : null;
                    if (r.IsDisposed) { bmp?.Dispose(); break; }
                    if (i == 0) r.Thumb0 = bmp;
                    else if (i == 1) r.Thumb1 = bmp;
                    else r.Thumb2 = bmp;
                }
            }
            catch
            {
                // A card whose covers can't load keeps its placeholder tiles.
            }
        }
    }
}
