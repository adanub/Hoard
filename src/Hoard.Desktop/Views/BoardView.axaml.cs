using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Controls;
using Hoard.Desktop.Infrastructure;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

/// <summary>
/// The Board screen: a masonry grid of one board's images (ItemCards) with a floating detail overlay. Tile
/// taps/unloads run the tile's own commands (wired to the view model); this code-behind covers the lazy
/// thumbnail decode, the detail-panel open/delete/restore/move actions, and the folder Edit popup (rename /
/// delete, routed through a confirm).
/// </summary>
public partial class BoardView : UserControl
{
    private readonly MasonryLayout? _masonry;
    private const double FadeMs = 200;   // matches the band Border's Opacity transition (close fade-out duration)
    private const double ScrollMs = 280; // the smooth scroll that brings the opened band's top to the viewport top

    public BoardView()
    {
        InitializeComponent();
        // Decode thumbnails only as the ItemsRepeater realizes containers (virtualized on-demand).
        AssetGrid.ElementPrepared += OnAssetElementPrepared;
        // Drive the masonry's expanded full-width band from the view model (the Layout has no DataContext of its
        // own, so bind it here against this view's DataContext), and listen for its reflow tween settling so we can
        // time the band's fade to the tile movement.
        if (AssetGrid.Layout is MasonryLayout masonry)
        {
            _masonry = masonry;
            masonry.Bind(MasonryLayout.ExpandedIndexProperty, new Binding("DataContext.ExpandedIndex") { Source = this });
            masonry.ReflowSettled += OnReflowSettled;
        }
        DataContextChanged += OnDataContextChanged;
        WireFolderEditSheet();
    }

    private BoardViewModel? Vm => DataContext as BoardViewModel;
    private AssetDetailViewModel? Detail => Vm?.Details;

    private BoardViewModel? _subscribedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null) _subscribedVm.CollapseStarting -= OnCollapseStarting;
        _subscribedVm = Vm;
        if (_subscribedVm is not null) _subscribedVm.CollapseStarting += OnCollapseStarting;
    }

    // Stop the reflow + scroll tweens when this screen leaves the visual tree (Pop/dispose), so neither timer keeps
    // ticking against a detached view/VM — otherwise a tile expanded right before navigating Back would leave them
    // firing on a dead page, against the "Pop disposes the page" contract.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _scroll.Stop();
        _masonry?.StopAnimation();
    }

    // The masonry finished a reflow tween. On open (idx ≥ 0): now that the tiles have settled, SMOOTHLY scroll the
    // band's top to the viewport top and fade the band in together (both after the tiles have moved) — the scroll is
    // its own eased animation, never a snap, so the band appears and slides into place rather than a blank gap
    // sitting around. On close (idx < 0): cancel any in-flight open-scroll and finalize the collapse.
    private void OnReflowSettled(int expandedIndex)
    {
        if (Vm is not { } vm) return;
        if (expandedIndex >= 0)
        {
            if (vm.SelectedAsset is { } sel) sel.BandContentOpacity = 1; // fade in the selected tile's band
            ScrollBandToTop(); // smooth scroll, concurrent with the fade
        }
        else
        {
            _scroll.Stop();
            vm.FinishCollapse();
        }
    }

    // User closed the band: fade its content OUT first (the transition), THEN drop it from the layout so the tiles
    // reflow back up into the gap — the reverse of opening, quicker.
    private void OnCollapseStarting()
    {
        if (Vm is not { SelectedAsset: { } tile }) return;
        _scroll.Stop(); // don't keep scrolling toward a band that's now closing
        tile.BandContentOpacity = 0; // fade out
        DispatcherTimer.RunOnce(() =>
        {
            if (!ReferenceEquals(Vm?.SelectedAsset, tile)) return; // re-opened / switched during the fade — abandon
            Vm!.CommitCollapse(); // → layout reflows the tiles back → OnReflowSettled(-1) → FinishCollapse
            // Belt-and-braces: if the reflow-settled signal never arrives, still finalize shortly after.
            DispatcherTimer.RunOnce(() => Vm?.FinishCollapse(), TimeSpan.FromMilliseconds(260));
        }, TimeSpan.FromMilliseconds(FadeMs));
    }

    // ── Smooth scroll to bring the opened band's top to the viewport top ─────────────────────────────────────
    // A value tween (not a snap, and not a transition on ScrollViewer.Offset — that would lag every normal scroll).

    private readonly Tween _scroll = new();
    private static readonly IEasing ScrollEasing = new CubicEaseInOut();

    private void ScrollBandToTop()
    {
        try
        {
            if (_masonry?.ExpandedBandTop is not double bandTop) return;
            // Translate the band's (stable) top from the masonry's own coordinates into the scroll viewport, then
            // offset so it sits at the top — a tall band, or one whose tile was at the viewport edge, would
            // otherwise sit mostly/entirely off-screen.
            if (AssetGrid.TranslatePoint(new Point(0, bandTop), GridScroll) is { } p)
            {
                var target = Math.Max(0, GridScroll.Offset.Y + p.Y - GridScroll.Padding.Top);
                _scroll.Start(GridScroll.Offset.Y, target, ScrollMs, ScrollEasing,
                    onStep: y => GridScroll.Offset = GridScroll.Offset.WithY(y));
            }
        }
        catch
        {
            // Scrolling the band into view is non-critical — never let it crash the app.
        }
    }

    private void WireFolderEditSheet()
    {
        FolderEditSheet.RenameCommand = new RelayCommand<object?>(name =>
        {
            if (Vm is { } vm) _ = vm.FolderEditor.RenameAsync(name as string);
        });
        FolderEditSheet.ClearCacheCommand = new RelayCommand(() =>
        {
            if (Vm is { } vm) _ = vm.FolderEditor.ClearCacheAsync();
        });
        FolderEditSheet.DeleteCommand = new RelayCommand(ShowFolderDeleteConfirm);
        FolderConfirmHost.DismissCommand = new RelayCommand(() => FolderConfirmHost.IsOpen = false);
    }

    private void ShowFolderDeleteConfirm()
    {
        if (Vm is not { FolderEditor.EditTarget: { } folder }) return;
        FolderConfirmContent.Title = "Delete folder?";
        FolderConfirmContent.Message =
            $"Delete the folder “{folder.Name}” and its images — files go to your recycle bin (any also in another board are removed there too).";
        FolderConfirmContent.ConfirmLabel = "Delete";
        FolderConfirmContent.ConfirmCommand = new RelayCommand(() =>
        {
            FolderConfirmHost.IsOpen = false;
            if (Vm is { } v) { v.FolderEditor.CloseCommand.Execute(null); _ = v.FolderEditor.DeleteAsync(); }
        });
        FolderConfirmContent.CancelCommand = new RelayCommand(() => FolderConfirmHost.IsOpen = false);
        FolderConfirmContent.Begin(3); // a brief cooldown — it deletes images
        FolderConfirmHost.IsOpen = true;
    }

    private void OnMoveAsset(object? sender, RoutedEventArgs e) => Vm?.OpenMoveSheet();

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
