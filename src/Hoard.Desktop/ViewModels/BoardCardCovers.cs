using System.Collections.Generic;
using System.Threading.Tasks;
using Hoard.Core.Library;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

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
            try
            {
                var covers = await library.GetCoverAssetsAsync(r.CollectionId, 3);
                for (var i = 0; i < covers.Count; i++)
                {
                    var bmp = thumbnails is not null
                        ? await thumbnails.GetAsync(covers[i].Sha256, covers[i].AbsolutePath, 240)
                        : null;
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
