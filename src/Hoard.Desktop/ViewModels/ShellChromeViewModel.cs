using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Navigation;

namespace Hoard.Desktop.ViewModels;

/// <summary>One entry in the breadcrumb trail: a page on the navigation stack under its display title.</summary>
public sealed record Crumb(string Title, ViewModelBase Page);

/// <summary>
/// The shell chrome's state: the breadcrumb trail (derived from <see cref="NavigationService.PageChain"/>)
/// and the floating bottom bar (back · search · ＋ menu). Shell-lifetime — it observes whichever page is
/// <see cref="NavigationService.Current"/> through the small <c>PageChrome</c> contracts
/// (<see cref="IProvidesSearch"/>/<see cref="IProvidesPlusActions"/>/<see cref="IImmersivePage"/>), so it
/// never holds page-specific logic and stays testable against fakes.
/// </summary>
public partial class ShellChromeViewModel : ViewModelBase
{
    private readonly NavigationService _nav;
    private readonly Action? _openSettings;
    private ViewModelBase? _page; // the page currently observed (subscription swapped on navigation)

    public ShellChromeViewModel(NavigationService nav, Action? openSettings = null)
    {
        _nav = nav;
        _openSettings = openSettings;
        _nav.PropertyChanged += OnNavChanged;
        SyncToPage();
        RebuildCrumbs();
    }

    // ── Settings (the ⚙ opens the shell-owned settings sheet) ────────────────

    public bool HasSettings => _openSettings is not null;

    [RelayCommand]
    private void OpenSettings()
    {
        IsPlusMenuOpen = false;
        _openSettings?.Invoke();
    }

    // ── Breadcrumb ────────────────────────────────────────────────────────────

    /// <summary>The page trail, root first, current page last (e.g. Projects › Pinterest Backup › Terrain).</summary>
    public IReadOnlyList<Crumb> Crumbs { get; private set; } = Array.Empty<Crumb>();

    /// <summary>An ancestor crumb was clicked: step Back to that page (forward history keeps the hops).
    /// Gated on <see cref="IsBarVisible"/> — the strip is docked ABOVE the page, so no page-level sheet scrim
    /// (or the lightbox) covers it; without the gate a crumb click would pop and dispose the page underneath
    /// an open modal, bypassing the modal-first contract every other back gesture keeps.</summary>
    [RelayCommand]
    private void NavigateToCrumb(Crumb crumb)
    {
        if (!IsBarVisible) return; // a sheet is open, or the page is immersive (zoom / project opening)
        IsPlusMenuOpen = false;
        _nav.BackTo(crumb.Page);
    }

    private void RebuildCrumbs()
    {
        var chain = _nav.PageChain;
        // Skip identical rebuilds: CrumbTitle raises land here on every search keystroke (live result counts),
        // and each new Crumbs reference makes BreadcrumbBar re-measure and rebuild all its children.
        if (chain.Count == Crumbs.Count)
        {
            var same = true;
            for (var i = 0; i < chain.Count && same; i++)
                same = ReferenceEquals(Crumbs[i].Page, chain[i])
                       && Crumbs[i].Title == ((chain[i] as ICrumbTitled)?.CrumbTitle ?? "");
            if (same) return;
        }
        Crumbs = chain.Select(p => new Crumb((p as ICrumbTitled)?.CrumbTitle ?? "", p)).ToArray();
        OnPropertyChanged(nameof(Crumbs));
    }

    // ── Back ──────────────────────────────────────────────────────────────────

    public bool CanGoBack => _nav.CanGoBack;

    /// <summary>The bar's ← button: close an open ＋ menu first (mirroring the window's unified-back order —
    /// sheets never race it, the bar is hidden whenever one is open), else one navigation step
    /// (zoom → band → page).</summary>
    [RelayCommand]
    private void Back()
    {
        if (IsPlusMenuOpen) { IsPlusMenuOpen = false; return; }
        _nav.Back();
    }

    // ── Forward ─────────────────────────────────────────────────────────────────

    public bool CanGoForward => _nav.CanGoForward;

    /// <summary>The bar's → button: re-enter a backed-out step (page → band → zoom). Closes the ＋ menu first so
    /// the action card isn't left floating over a page that's about to change.</summary>
    [RelayCommand]
    private void Forward()
    {
        IsPlusMenuOpen = false;
        _nav.Forward();
    }

    // ── Search (the pill morphs into an input) ────────────────────────────────

    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchText = "";

    public bool HasSearch => _page is IProvidesSearch;
    public string SearchPlaceholder => (_page as IProvidesSearch)?.SearchPlaceholder ?? "Search…";

    [RelayCommand]
    private void OpenSearch()
    {
        if (!HasSearch) return;
        IsPlusMenuOpen = false; // the pill is morphing — don't leave the action card floating above it
        IsSearchOpen = true;
    }

    /// <summary>The ✕ (or Esc inside the field): clear the query — which clears the page's filter — and
    /// collapse the pill back to its icons. The bar's open state always mirrors the page's query, so a page
    /// is never left silently filtered behind a collapsed bar.</summary>
    [RelayCommand]
    private void CloseSearch()
    {
        SearchText = ""; // pushed through to the page (clears its filter) before collapsing
        IsSearchOpen = false;
    }

    /// <summary>Enter in the field — pages where search navigates (the Library) act here; filters ignore it.</summary>
    public void SubmitSearch() => (_page as IProvidesSearch)?.SubmitSearch();

    // Live proxy: every keystroke lands on the page (the Board's debounced filter, the launcher's card filter).
    partial void OnSearchTextChanged(string value)
    {
        if (_page is IProvidesSearch s && s.SearchText != value) s.SearchText = value;
    }

    // ── ＋ menu ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isPlusMenuOpen;

    public IReadOnlyList<PlusAction> PlusActions =>
        (_page as IProvidesPlusActions)?.PlusActions ?? Array.Empty<PlusAction>();

    public bool HasPlusActions => PlusActions.Count > 0;

    [RelayCommand]
    private void TogglePlusMenu() => IsPlusMenuOpen = !IsPlusMenuOpen;

    [RelayCommand]
    private void ClosePlusMenu() => IsPlusMenuOpen = false;

    /// <summary>Run a ＋ menu entry: close the menu first (the action usually opens a sheet, which hides the
    /// bar), then execute the page's command.</summary>
    [RelayCommand]
    private void RunPlusAction(PlusAction action)
    {
        IsPlusMenuOpen = false;
        if (action.Command.CanExecute(null)) action.Command.Execute(null);
    }

    // ── Bar visibility ────────────────────────────────────────────────────────

    /// <summary>True while any in-app sheet is open (set by <c>MainWindow</c>'s sheet tracking): the bar hides
    /// so its actions can't fire under a modal.</summary>
    [ObservableProperty] private bool _isModalOpen;

    /// <summary>The floating bar shows unless a sheet is open or the page is immersive (fullscreen zoom).</summary>
    [ObservableProperty] private bool _isBarVisible = true;

    partial void OnIsModalOpenChanged(bool value) => RecomputeBarVisible();

    private void RecomputeBarVisible()
    {
        IsBarVisible = !IsModalOpen && (_page as IImmersivePage)?.IsImmersive != true;
        if (!IsBarVisible) IsPlusMenuOpen = false; // never leave the menu floating over a modal/zoom
    }

    // ── Page tracking ─────────────────────────────────────────────────────────

    private void OnNavChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NavigationService.Current): SyncToPage(); break;
            case nameof(NavigationService.PageChain): RebuildCrumbs(); break;
            case nameof(NavigationService.CanGoBack): OnPropertyChanged(nameof(CanGoBack)); break;
            case nameof(NavigationService.CanGoForward): OnPropertyChanged(nameof(CanGoForward)); break;
        }
    }

    private void SyncToPage()
    {
        if (_page is not null) _page.PropertyChanged -= OnPagePropertyChanged;
        _page = _nav.Current;
        if (_page is not null) _page.PropertyChanged += OnPagePropertyChanged;

        IsPlusMenuOpen = false;
        // Pull the page's existing query into the bar (a results board, or a rebuilt page re-entered via
        // Forward) — the bar's open state mirrors it, so an active filter is always visible. Trimmed, because
        // the pages treat a whitespace-only query as inactive (a stray space mustn't reopen an "active" pill).
        var query = (_page as IProvidesSearch)?.SearchText ?? "";
        SearchText = query;
        IsSearchOpen = query.Trim().Length > 0;
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(PlusActions));
        OnPropertyChanged(nameof(HasPlusActions));
        RecomputeBarVisible();
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _page)) return; // a just-swapped page's parting notification
        switch (e.PropertyName)
        {
            case nameof(IProvidesSearch.SearchText): // page-side change (e.g. its own clear) → mirror it
                if (_page is IProvidesSearch s && s.SearchText != SearchText) SearchText = s.SearchText;
                // A page-side SET opens the pill so the filter is never silent behind a collapsed bar; a
                // page-side CLEAR leaves it open (the user may be mid-typing) — an empty open field is honest.
                if (SearchText.Trim().Length > 0) IsSearchOpen = true;
                break;
            case nameof(ICrumbTitled.CrumbTitle): // live title (the search result count) → rebuild the trail
                RebuildCrumbs();
                break;
            case nameof(IImmersivePage.IsImmersive):
                RecomputeBarVisible();
                break;
        }
    }
}
