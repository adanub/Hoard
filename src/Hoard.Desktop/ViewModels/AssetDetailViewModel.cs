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
/// reference alone leaves the native surface to lagging finalization (one ~520px surface per band open).</summary>
public partial class AssetDetailViewModel : ViewModelBase, IDisposable
{
    private const int PreviewWidth = 520;

    public AssetDetail Model { get; }

    [ObservableProperty] private Bitmap? _preview;
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
        Preview = null; // → OnPreviewChanged frees the native bitmap
    }

    // Free the PREVIOUS surface synchronously on every swap (replace/dispose) — the tile-thumbnail rule.
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

    private async Task LoadPreviewAsync()
    {
        if (!IsStaticImage) return; // GIFs are handled by the animated control; videos have no preview
        IsPreviewLoading = true;
        try
        {
            var path = Model.AbsolutePath;
            var decoded = await Task.Run(() =>
            {
                using var stream = System.IO.File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, PreviewWidth);
            });
            // Disposed while decoding (band closed / image switched): drop the surface, don't strand it.
            if (_disposed) { decoded.Dispose(); return; }
            Preview = decoded;
        }
        catch
        {
            // Missing/corrupt file — just show no preview.
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

}
