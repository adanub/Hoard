using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private IDisposable? _expandedBinding; // the masonry ExpandedIndex binding (Source = this); disposed on detach
    private const double FadeMs = 200;   // matches the band Border's Opacity transition (close fade-out duration)
    private const double ScrollMs = 280; // the smooth scroll that brings the opened band's top to the viewport top

    public BoardView()
    {
        InitializeComponent();
        LeakCanary.Track(this);
        // Decode thumbnails only as the ItemsRepeater realizes containers (virtualized on-demand); free them again
        // as containers are recycled off-screen, so memory tracks what's near the viewport.
        AssetGrid.ElementPrepared += OnAssetElementPrepared;
        AssetGrid.ElementClearing += OnAssetElementClearing;
        // The inline detail band stacks (image over info) below the packer's stack breakpoint; keep the VM flag
        // in sync with the grid width so the band layout matches the band-height math.
        AssetGrid.SizeChanged += OnGridSizeChanged;
        // Drive the masonry's expanded full-width band from the view model (the Layout has no DataContext of its
        // own, so bind it here against this view's DataContext), and listen for its reflow tween settling so we can
        // time the band's fade to the tile movement.
        if (AssetGrid.Layout is MasonryLayout masonry)
        {
            _masonry = masonry;
            // Capture the IDisposable so we can dispose it on detach — an explicit-Source binding (Source = this)
            // that's never disposed keeps the (detached) view rooted, which leaked a board+view per navigation.
            _expandedBinding = masonry.Bind(MasonryLayout.ExpandedIndexProperty, new Binding("DataContext.ExpandedIndex") { Source = this });
            masonry.ReflowSettled += OnReflowSettled;
        }
        DataContextChanged += OnDataContextChanged;
        // GIF autoplay (Settings) is driven by what's actually IN the viewport, debounced so a fling doesn't
        // decode every GIF it passes. NOT realization-driven: the repeater realizes a buffer beyond the
        // viewport (last), so playing-on-realize let off-screen GIFs evict the visible ones from the play LRU.
        _gifScan = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _gifScan.Tick += OnGifScanTick;
        GridScroll.ScrollChanged += OnGridScrollChanged;
        WireFolderEditSheet();
    }

    private readonly DispatcherTimer _gifScan;

    // Extent changes fire this too, so the initial asset load and the band's open/close reflow all reschedule.
    private void OnGridScrollChanged(object? sender, ScrollChangedEventArgs e) => ScheduleGifScan();

    private void ScheduleGifScan()
    {
        if (Vm is not { GifAutoplayEnabled: true }) return;
        _gifScan.Stop();
        _gifScan.Start(); // restart the debounce window
    }

    private void OnGifScanTick(object? sender, EventArgs e)
    {
        _gifScan.Stop();
        if (Vm is not { GifAutoplayEnabled: true } vm) return;

        // Realized containers whose bounds intersect the ScrollViewer's viewport (TranslatePoint maps through
        // the scroll offset). Recycled/pooled elements fall out via the intersection test (the repeater
        // arranges them far off-screen). Sorted top-to-bottom: the VM plays at most the GIF budget, so the
        // TOPMOST visible GIFs must be the ones that win — GetVisualChildren is realization order, which
        // would make the surviving set arbitrary.
        var visible = new List<(double Y, AssetTileViewModel Tile)>();
        var viewportHeight = GridScroll.Bounds.Height;
        foreach (var child in AssetGrid.GetVisualChildren())
        {
            if (child is not Control { DataContext: AssetTileViewModel tile, IsVisible: true } control) continue;
            if (control.TranslatePoint(default, GridScroll) is not { } topLeft) continue;
            if (topLeft.Y + control.Bounds.Height > 0 && topLeft.Y < viewportHeight)
                visible.Add((topLeft.Y, tile));
        }
        visible.Sort((a, b) => a.Y.CompareTo(b.Y));
        vm.AutoplayVisibleGifs(visible.Select(v => v.Tile).ToList());
    }

    private BoardViewModel? Vm => DataContext as BoardViewModel;
    private AssetDetailViewModel? Detail => Vm?.Details;

    private BoardViewModel? _subscribedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null) { _subscribedVm.CollapseStarting -= OnCollapseStarting; _subscribedVm.ViewTeardown -= OnViewTeardown; _subscribedVm.PropertyChanged -= OnVmPropertyChanged; }
        _subscribedVm = Vm;
        if (_subscribedVm is not null) { _subscribedVm.CollapseStarting += OnCollapseStarting; _subscribedVm.ViewTeardown += OnViewTeardown; _subscribedVm.PropertyChanged += OnVmPropertyChanged; }
        UpdateBandStacked(AssetGrid.Bounds.Width); // seed the new VM (SizeChanged keeps it current thereafter)
    }

    // Flipping GIF autoplay on in Settings applies to this open board straight away (ScheduleGifScan gates on
    // the setting itself, so the flip-off case is a cheap no-op).
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardViewModel.GifAutoplayEnabled)) ScheduleGifScan();
    }

    // The board VM is disposing while this view is (still) attached (NavigationService.Back disposes the page before
    // the shell swaps it, which happens on the next layout pass). The ItemsRepeater is therefore still live and WILL
    // process a clear right now — but once detached it never runs layout again, so it would keep every realized tile,
    // and each tile's #Root self-binding (held by a compositor-retained child) roots the whole board through it. So
    // null the source and force a synchronous layout pass: the repeater recycles + DETACHES its tiles, which
    // deactivates their bindings and lets the board (tiles, AssetViews, thumbnails) be collected on back-out.
    private void OnViewTeardown()
    {
        AssetGrid.ItemsSource = null;
        AssetGrid.UpdateLayout();
    }

    private void OnGridSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateBandStacked(e.NewSize.Width);

    private void UpdateBandStacked(double width)
    {
        if (width > 0 && Vm is { } vm) vm.IsBandStacked = width < MasonryPacker.StackBreakpoint;
    }

    // This screen leaves the visual tree only when it's navigated away from (discarded — forward rebuilds a fresh
    // view), so tear everything down: stop the tweens (so neither timer keeps ticking against a dead page), dispose
    // the explicit-Source masonry binding and unsubscribe the cross-object events (which would otherwise keep the
    // detached view — and via its DataContext, the board — rooted), drop the ItemsRepeater's items, and clear the
    // DataContext so a lingering view shell can't drag the board. (A detached repeater never runs layout, so it
    // never recycles its realized tiles on its own — but the band markup is now lazy, so a retained tile is light.)
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // This view never reattaches (forward rebuilds a fresh one) — from here on, it surviving GC is a leak.
        LeakCanary.MarkDead(this);
        // Navigated away with the band still open/closing (e.g. drilled into a folder): the view can't finish the
        // collapse animation, so clear the band state on the VM now — otherwise this board, kept beneath on the
        // stack, reappears half-open when revealed. (Done before DataContext is nulled, while Vm is still reachable.)
        Vm?.AbandonBand();
        _scroll.Stop();
        _gifScan.Stop(); // don't let a pending autoplay scan tick against a dead page
        GridScroll.ScrollChanged -= OnGridScrollChanged;
        _masonry?.StopAnimation();
        _expandedBinding?.Dispose();
        _expandedBinding = null;
        if (_masonry is not null) _masonry.ReflowSettled -= OnReflowSettled;
        AssetGrid.ElementPrepared -= OnAssetElementPrepared;
        AssetGrid.ElementClearing -= OnAssetElementClearing;
        AssetGrid.SizeChanged -= OnGridSizeChanged;
        DataContextChanged -= OnDataContextChanged;
        if (_subscribedVm is not null) { _subscribedVm.CollapseStarting -= OnCollapseStarting; _subscribedVm.ViewTeardown -= OnViewTeardown; _subscribedVm.PropertyChanged -= OnVmPropertyChanged; _subscribedVm = null; }
        AssetGrid.ItemsSource = null; // release the ItemsRepeater's realized elements + its subscription to Assets
        DataContext = null;           // sever the last view→board edge so a lingering view shell can't drag the board
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
            $"Delete the folder “{folder.Name}” and its images — {Hoard.Desktop.Services.RecycleWording.FilesFate} (any also in another board are removed there too).";
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

    // Symmetric with ElementPrepared: a tile recycled well past the viewport drops its decoded thumbnail (freeing
    // the native bitmap) so a long scroll doesn't retain one per image ever shown. The expanded band tile is always
    // realized so it's never cleared here — guard it anyway. Re-realizing re-decodes from the on-disk cache.
    private void OnAssetElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (e.Element.DataContext is AssetTileViewModel { IsExpanded: false } tile)
            tile.ReleaseThumbnail();
    }

    // Delete now opens an in-app note sheet (collects the required tombstone reason), replacing the old
    // DeleteDialog OS window — consistent with the other sheets and mobile-ready.
    private void OnDeleteAsset(object? sender, RoutedEventArgs e) => Vm?.OpenDeleteSheet();

    // Tapping the expanded band's media opens the fullscreen lightbox. Tapped fires for every pointer button, so
    // gate it to a primary (left) press — the same rule as the *Card controls (the band has no press transform,
    // so plain Tapped is fine here; only the button-gating matters).
    private bool _bandMediaPrimary;
    private void OnBandMediaPressed(object? sender, PointerPressedEventArgs e) =>
        _bandMediaPrimary = e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed;
    private void OnBandMediaTapped(object? sender, TappedEventArgs e)
    {
        if (_bandMediaPrimary) Vm?.OpenLightbox();
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
