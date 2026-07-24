using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Library;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// The shared rename / clear-cache / delete lifecycle behind a <see cref="BoardCardRef"/>'s Edit popup (the
/// pencil) — used by both the Library grid's board cards and the Board screen's folder cards. A folder is just a
/// child board, so the pencil does the same things; this removes the near-identical edit block that used to live
/// in <c>LibraryViewModel</c> and <c>BoardViewModel</c>. The host supplies the noun for the toasts ("board" /
/// "folder"), how to drop the card from its grid on delete, an optional post-change hook (e.g. re-evaluating a
/// "can move" guard), and an optional extra-detail loader (the Library board edit also lists the board's merged
/// sources, which folders don't have).
/// </summary>
public sealed partial class BoardCardEditor : ObservableObject
{
    private readonly LibraryService _library;
    private readonly CurationService _curation;
    private readonly ThumbnailCache? _thumbnails;
    private readonly ToastService _toasts;
    private readonly string _noun;
    private readonly Action<BoardCardRef> _removeCard;
    private readonly Action? _afterChange;
    private readonly Func<BoardCardRef, BoardDetail, Task>? _loadExtraDetail;

    public BoardCardEditor(
        LibraryService library, CurationService curation, ThumbnailCache? thumbnails, ToastService toasts,
        string noun, Action<BoardCardRef> removeCard,
        Action? afterChange = null, Func<BoardCardRef, BoardDetail, Task>? loadExtraDetail = null)
    {
        _library = library;
        _curation = curation;
        _thumbnails = thumbnails;
        _toasts = toasts;
        _noun = noun;
        _removeCard = removeCard;
        _afterChange = afterChange;
        _loadExtraDetail = loadExtraDetail;
    }

    /// <summary>The card currently being edited (null when the popup is closed / never opened).</summary>
    [ObservableProperty] private BoardCardRef? _editTarget;
    [ObservableProperty] private bool _isSheetOpen;

    [RelayCommand]
    private void Close() => IsSheetOpen = false;

    /// <summary>Open the Edit popup for a card (the pencil) and fill its detail rows lazily.</summary>
    public void Begin(BoardCardRef r)
    {
        EditTarget = r;
        IsSheetOpen = true;
        _ = LoadDetailAsync(r);
    }

    private async Task LoadDetailAsync(BoardCardRef r)
    {
        if (_library is null || r.CollectionId is not int id) return;
        r.CountsText = "Counting…";
        r.CacheText = "";
        r.Sources.Clear(); // the board edit repopulates these; a folder never has any (a harmless no-op)

        try
        {
            var detail = await _library.GetBoardDetailAsync(id);
            if (detail is null) { r.CountsText = "No details."; return; }
            r.CountsText = $"{detail.Images} images · {detail.Gifs} GIFs · {detail.Videos} videos";
            r.CacheText = ByteFormat.Format(detail.SizeBytes) + " on disk";
            r.AddedText = "Added " + detail.CreatedAt.LocalDateTime.ToString("d MMM yyyy");
            if (_loadExtraDetail is not null) await _loadExtraDetail(r, detail);
        }
        catch (Exception ex)
        {
            // Don't leave the popup stuck on "Counting…" if the read fails — say so and surface the reason.
            r.CountsText = "Couldn't load details.";
            _toasts.Show($"Couldn't load {_noun} details: {ex.Message}", isError: true);
        }
    }

    /// <summary>Rename the card under edit (its local display name).</summary>
    public async Task RenameAsync(string? newName)
    {
        if (EditTarget is not { CollectionId: int id } r || string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            await _curation.RenameBoardAsync(id, newName.Trim());
            r.Name = newName.Trim();
            _toasts.Show($"Renamed to “{r.Name}”.");
        }
        catch (Exception ex) { _toasts.Show($"Couldn't rename: {ex.Message}", isError: true); }
    }

    /// <summary>Clear the card-under-edit's cached thumbnails (regenerated on demand).</summary>
    public async Task ClearCacheAsync()
    {
        if (EditTarget is not { CollectionId: int id } r) return;
        var shas = await _library.GetBoardAssetShasAsync(id);
        if (_thumbnails is not null)
            foreach (var sha in shas) _thumbnails.Evict(sha);
        _toasts.Show($"Cleared cached thumbnails for “{r.Name}”.");
    }

    /// <summary>Delete the card under edit and its whole subtree (files to the recycle bin); drop its card.</summary>
    public async Task DeleteAsync()
    {
        if (EditTarget is not { CollectionId: int id } r) return;
        try
        {
            var removed = await _curation.DeleteBoardAsync(id);
            _removeCard(r);
            _afterChange?.Invoke();
            _toasts.Show($"Deleted {_noun} “{r.Name}” — {removed} image(s) {Services.RecycleWording.SentFate}.");
        }
        catch (Exception ex) { _toasts.Show($"Couldn't delete {_noun}: {ex.Message}", isError: true); }
    }
}
