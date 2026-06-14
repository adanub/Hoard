using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        // Decode thumbnails only as the ItemsRepeater realizes containers (virtualized on-demand).
        AssetGrid.ElementPrepared += OnAssetElementPrepared;
    }

    private void OnAssetElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element.DataContext is AssetTileViewModel tile)
            _ = tile.EnsureThumbnailAsync();
    }

    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        // Ignore taps that came from a button inside the tile (e.g. the Unload button), which would
        // otherwise re-select/replay the GIF we just acted on.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        if (sender is Control { DataContext: AssetTileViewModel tile } && DataContext is LibraryViewModel vm)
            vm.ActivateTile(tile);
    }

    private void OnUnloadTile(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AssetTileViewModel tile } && DataContext is LibraryViewModel vm)
            vm.UnloadGif(tile);
        e.Handled = true; // don't let the click fall through to the tile's tap (which would replay it)
    }

    private void OnStatusTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm || string.IsNullOrEmpty(vm.StatusText)) return;
        var dialog = new MessageDialog("Status", vm.StatusText);
        if (TopLevel.GetTopLevel(this) is Window owner)
            dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e) => _ = OpenAsync(Detail?.Model.SourceUrl);
    private void OnOpenOriginal(object? sender, RoutedEventArgs e) => _ = OpenAsync(Detail?.Model.OriginalUrl);
    private void OnOpenImage(object? sender, RoutedEventArgs e) => _ = OpenAsync(Detail?.Model.AbsolutePath);

    private AssetDetailViewModel? Detail => (DataContext as LibraryViewModel)?.Details;

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
