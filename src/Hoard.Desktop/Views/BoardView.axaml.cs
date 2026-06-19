using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

/// <summary>
/// The Board screen: a masonry grid of one board's images (ItemCards) with a floating detail overlay. Tile
/// taps/unloads run the tile's own commands (wired to the view model); this code-behind covers the lazy
/// thumbnail decode and the detail-panel open/delete/restore actions.
/// </summary>
public partial class BoardView : UserControl
{
    public BoardView()
    {
        InitializeComponent();
        // Decode thumbnails only as the ItemsRepeater realizes containers (virtualized on-demand).
        AssetGrid.ElementPrepared += OnAssetElementPrepared;
    }

    private BoardViewModel? Vm => DataContext as BoardViewModel;
    private AssetDetailViewModel? Detail => Vm?.Details;

    private void OnAssetElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element.DataContext is AssetTileViewModel tile)
            _ = tile.EnsureThumbnailAsync();
    }

    private async void OnDeleteAsset(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedAsset: { } tile } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new DeleteDialog(tile.Model.Title ?? "this image");
        if (await dialog.ShowDialog<string?>(owner) is { Length: > 0 } note)
            await vm.DeleteSelectedAsync(note);
    }

    private async void OnRestoreAsset(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.RestoreSelectedAsync();
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e) => _ = OpenAsync(Detail?.Model.SourceUrl);
    private void OnOpenImage(object? sender, RoutedEventArgs e) => _ = OpenAsync(Detail?.Model.AbsolutePath);

    /// <summary>Open a URL or local file path with the OS default handler (cross-platform via the launcher).</summary>
    private async Task OpenAsync(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
                await top.Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Nothing actionable if the shell refuses to open it.
        }
    }
}
