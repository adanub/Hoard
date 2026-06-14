using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Core.Domain;
using Hoard.Core.Library;

namespace Hoard.Desktop.ViewModels;

/// <summary>Formats an <see cref="AssetDetail"/> for the detail panel and lazily loads a preview image.</summary>
public partial class AssetDetailViewModel : ViewModelBase
{
    private const int PreviewWidth = 520;

    public AssetDetail Model { get; }

    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private bool _isPreviewLoading;

    public AssetDetailViewModel(AssetDetail model)
    {
        Model = model;
        _ = LoadPreviewAsync();
    }

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
            Preview = await Task.Run(() =>
            {
                using var stream = System.IO.File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, PreviewWidth);
            });
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
