using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Core.Domain;
using Hoard.Core.Library;

namespace Hoard.Desktop.ViewModels;

/// <summary>Formats an <see cref="AssetDetail"/> for the detail panel and lazily loads a preview image.
/// Disposable: <see cref="Preview"/> is a native (Skia) bitmap, so the owner (the board) must <see cref="Dispose"/>
/// a replaced/abandoned instance — same eager-free rule as <see cref="AssetTileViewModel.Thumbnail"/>; dropping the
/// reference alone leaves the native surface to lagging finalization (one full-resolution surface per band open).</summary>
public partial class AssetDetailViewModel : ViewModelBase, IDisposable
{
    /// <summary>Width of the <b>first</b> decode — a quick, sampled one that gives the band something to draw while
    /// the full-resolution decode runs. Not what the band settles on (see <see cref="LoadPreviewAsync"/>).</summary>
    private const int PreviewWidth = 520;

    public AssetDetail Model { get; }

    /// <summary>What the band draws: the quick sampled decode at first, replaced by the full-resolution image once
    /// that lands. This property <b>owns</b> both surfaces — each swap frees the one it replaces.</summary>
    [ObservableProperty] private Bitmap? _preview;

    /// <summary>The same surface as <see cref="Preview"/>, exposed only once <see cref="Preview"/> holds the
    /// full-resolution decode — null while the band is still showing the sampled stand-in. The fullscreen zoom
    /// <i>borrows</i> it (a second decode of the same file would double the memory and make opening the zoom wait
    /// on work the band has already done), so it is never disposed through here: ownership stays with
    /// <see cref="Preview"/>, and the lightbox drops its reference when this goes null.</summary>
    [ObservableProperty] private Bitmap? _fullPreview;

    [ObservableProperty] private bool _isPreviewLoading;

    private bool _disposed;

    public AssetDetailViewModel(AssetDetail model)
    {
        Model = model;
        _ = LoadPreviewAsync();
    }

    /// <summary>Free the preview's native bitmap. Also supersedes an in-flight decode (it checks the flag on
    /// completion and drops its result) so a fast open→close can't strand a surface.</summary>
    public void Dispose()
    {
        _disposed = true;
        FullPreview = null; // borrowed alias: clear it first so nothing is left pointed at a surface about to go
        Preview = null;     // → OnPreviewChanged frees the native bitmap
    }

    // Free the PREVIOUS surface synchronously on every swap (sampled → full resolution, dispose) — the
    // tile-thumbnail rule.
    partial void OnPreviewChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    public string Title => string.IsNullOrWhiteSpace(Model.Title) ? "(untitled)" : Model.Title!;
    public string? Description => Model.Description;

    /// <summary>A tombstone has no media on disk; the panel shows the deletion note + a Restore action instead.</summary>
    public bool IsDeleted => Model.IsDeleted;
    public bool IsLive => !Model.IsDeleted;
    public string? DeletionNote => Model.DeletionNote;
    public bool CanRestore => Model.IsDeleted && !string.IsNullOrWhiteSpace(Model.SourceUrl);

    // Media kinds only drive the live preview; a deleted asset shows none of it.
    public bool IsImage => IsLive && Model.Kind is MediaKind.Image or MediaKind.Gif;
    public bool IsGif => IsLive && Model.Kind is MediaKind.Gif;
    public bool IsStaticImage => IsLive && Model.Kind is MediaKind.Image;
    public bool IsVideo => IsLive && Model.Kind is MediaKind.Video;
    public string FilePath => Model.AbsolutePath;

    public string? Dimensions => Model is { Width: > 0, Height: > 0 } ? $"{Model.Width} × {Model.Height}" : null;
    public string FileSize => ByteFormat.Format(Model.Bytes);
    public string TypeText => Model.MimeType is { } m ? $"{Model.Kind} · {m}" : Model.Kind.ToString();
    public string Downloaded => Model.ImportedAt.ToLocalTime().ToString("dd MMM yyyy, HH:mm");
    public string? Created => Model.CreatedAt?.ToLocalTime().ToString("dd MMM yyyy");
    public string? Boards => Model.Boards.Count > 0 ? string.Join(", ", Model.Boards) : null;
    public string? SourceId => Model.SourceId;
    public string? SourceUrl => Model.SourceUrl;
    public string? OriginalUrl => Model.OriginalUrl;

    /// <summary>
    /// Fills the band's image in two stages, so it paints fast and still settles at full resolution.
    /// <para>Stage one is a sampled decode at <see cref="PreviewWidth"/> — cheap, because Skia decodes straight to
    /// the smaller size — purely so the band has something to draw while stage two runs. Stage two decodes the file
    /// at its real size and replaces it, and that is what the band is meant to show: the sampled version is a
    /// stand-in, and it reads visibly soft across the band's ~1000px of media area, more so on a HiDPI screen.</para>
    /// <para>An image no wider than <see cref="PreviewWidth"/> skips stage one, since decoding it "smaller" would
    /// only upscale it — something worse than the file itself, for an image whose real decode is cheap anyway.</para>
    /// </summary>
    private async Task LoadPreviewAsync()
    {
        if (!IsStaticImage) return; // GIFs are handled by the animated control; videos have no preview
        var path = Model.AbsolutePath;
        IsPreviewLoading = true;

        if (Model.Width is null or 0 or > PreviewWidth)
        {
            try
            {
                var sampled = await Task.Run(() =>
                {
                    using var stream = System.IO.File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, PreviewWidth);
                });
                // Disposed while decoding (band closed / image switched): drop the surface, don't strand it.
                if (_disposed) { sampled.Dispose(); return; }
                Preview = sampled;
                IsPreviewLoading = false; // something is on screen — the full decode continues quietly behind it
            }
            catch
            {
                // Missing/corrupt file — fall through; the full decode reports the same by showing nothing.
            }
        }

        try
        {
            var full = await Task.Run(() => new Bitmap(path));
            if (_disposed) { full.Dispose(); return; }
            Preview = full;     // → OnPreviewChanged frees the sampled stand-in
            FullPreview = full; // the zoom may borrow it from here on
        }
        catch
        {
            // Missing/corrupt file — show no preview, or keep the sampled one if that much worked.
        }
        finally
        {
            if (!_disposed) IsPreviewLoading = false;
        }
    }
}
