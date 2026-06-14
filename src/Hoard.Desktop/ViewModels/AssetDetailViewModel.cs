using System;
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
    public bool HasDescription => !string.IsNullOrWhiteSpace(Model.Description);
    public string? Description => Model.Description;
    public bool IsImage => Model.Kind is MediaKind.Image or MediaKind.Gif;
    public bool IsGif => Model.Kind is MediaKind.Gif;
    public bool IsStaticImage => Model.Kind is MediaKind.Image;
    public bool IsVideo => Model.Kind is MediaKind.Video;
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

    public bool HasDimensions => Dimensions is not null;
    public bool HasCreated => Created is not null;
    public bool HasBoards => Boards is not null;
    public bool HasSourceId => !string.IsNullOrWhiteSpace(SourceId);
    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(SourceUrl);
    public bool HasOriginalUrl => !string.IsNullOrWhiteSpace(OriginalUrl);

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
