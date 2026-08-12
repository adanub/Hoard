using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.ViewModels;
using Xunit;

namespace Hoard.Desktop.Tests;

public class ShellChromeViewModelTests
{
    // A page with no chrome contracts — the bar should offer nothing for it.
    private sealed class PlainPage : ViewModelBase { }

    // A page implementing all the PageChrome contracts, driven directly by the tests.
    private sealed class FakePage : ViewModelBase, ICrumbTitled, IProvidesSearch, IProvidesPlusActions, IImmersivePage
    {
        public FakePage(string title = "Page") => _crumbTitle = title;

        private string _crumbTitle;
        public string CrumbTitle
        {
            get => _crumbTitle;
            set => SetProperty(ref _crumbTitle, value); // live, like a search-result count
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        public string SearchPlaceholder => "Search fake…";

        public int Submits;
        public void SubmitSearch() => Submits++;

        public IReadOnlyList<PlusAction> PlusActions { get; set; } = Array.Empty<PlusAction>();

        private bool _isImmersive;
        public bool IsImmersive
        {
            get => _isImmersive;
            set => SetProperty(ref _isImmersive, value);
        }
    }

    private static (NavigationService Nav, ShellChromeViewModel Chrome) NewShell(ViewModelBase root)
    {
        var nav = new NavigationService();
        var chrome = new ShellChromeViewModel(nav);
        nav.Reset(root);
        return (nav, chrome);
    }

    [Fact]
    public void Crumbs_follow_the_page_chain_with_page_titles()
    {
        var (nav, chrome) = NewShell(new FakePage("Projects"));
        nav.Push(new FakePage("Pinterest Backup"));
        nav.Push(new FakePage("Terrain Ideas"));

        Assert.Equal(new[] { "Projects", "Pinterest Backup", "Terrain Ideas" }, chrome.Crumbs.Select(c => c.Title));

        nav.Back();
        Assert.Equal(new[] { "Projects", "Pinterest Backup" }, chrome.Crumbs.Select(c => c.Title));
    }

    [Fact]
    public void Clicking_an_ancestor_crumb_jumps_back_to_it()
    {
        var root = new FakePage("Projects");
        var (nav, chrome) = NewShell(root);
        nav.Push(new FakePage("Library"));
        nav.Push(new FakePage("Board"));

        chrome.NavigateToCrumbCommand.Execute(chrome.Crumbs[0]);

        Assert.Same(root, nav.Current);
        Assert.True(nav.CanGoForward); // the jump is retraceable
    }

    [Fact]
    public void An_immersive_page_does_not_block_a_crumb_jump()
    {
        // The fullscreen viewer is a history STATE, not a modal, and it doesn't cover the breadcrumb strip
        // (docked above the page) — so the crumbs must stay live. Gating these on IsBarVisible, which also
        // falls for immersion, left them visible and dead while the lightbox was open.
        var root = new FakePage("Projects");
        var (nav, chrome) = NewShell(root);
        nav.Push(new FakePage("Library"));
        var board = new FakePage("Board");
        nav.Push(board);
        board.IsImmersive = true;
        Assert.False(chrome.IsBarVisible); // the floating bar still hides — that part was right

        chrome.NavigateToCrumbCommand.Execute(chrome.Crumbs[0]);

        Assert.Same(root, nav.Current);
    }

    [Fact]
    public void An_open_modal_still_blocks_a_crumb_jump()
    {
        // The other half of the same gate, and the reason it exists: no sheet scrim reaches the strip, so an
        // ungated crumb would pop and dispose the page out from under an open sheet.
        var root = new FakePage("Projects");
        var (nav, chrome) = NewShell(root);
        nav.Push(new FakePage("Library"));
        var board = new FakePage("Board");
        nav.Push(board);
        chrome.IsModalOpen = true;

        chrome.NavigateToCrumbCommand.Execute(chrome.Crumbs[0]);

        Assert.Same(board, nav.Current);
    }

    [Fact]
    public void Search_text_proxies_live_to_the_page_and_back()
    {
        var page = new FakePage();
        var (_, chrome) = NewShell(page);

        chrome.SearchText = "castle";
        Assert.Equal("castle", page.SearchText);

        page.SearchText = "keep"; // page-side change (its own clear/set) mirrors into the bar
        Assert.Equal("keep", chrome.SearchText);
    }

    [Fact]
    public void Arriving_on_a_page_with_an_active_query_opens_the_search_field()
    {
        var (nav, chrome) = NewShell(new FakePage());
        var results = new FakePage("Results") { SearchText = "vintage" };

        nav.Push(results);

        Assert.True(chrome.IsSearchOpen);
        Assert.Equal("vintage", chrome.SearchText);

        nav.Back(); // back on a page with no query → the field collapses
        Assert.False(chrome.IsSearchOpen);
        Assert.Equal("", chrome.SearchText);
    }

    [Fact]
    public void Closing_search_clears_the_page_filter_too()
    {
        var page = new FakePage();
        var (_, chrome) = NewShell(page);
        chrome.OpenSearchCommand.Execute(null);
        chrome.SearchText = "castle";

        chrome.CloseSearchCommand.Execute(null);

        Assert.False(chrome.IsSearchOpen);
        Assert.Equal("", page.SearchText); // no invisible filter left behind
    }

    [Fact]
    public void Submit_forwards_to_the_page()
    {
        var page = new FakePage();
        var (_, chrome) = NewShell(page);

        chrome.SubmitSearch();

        Assert.Equal(1, page.Submits);
    }

    [Fact]
    public void Plus_actions_surface_from_the_page_and_running_one_closes_the_menu()
    {
        var runs = 0;
        var page = new FakePage { PlusActions = new[] { new PlusAction("Sync board", new RelayCommand(() => runs++)) } };
        var (_, chrome) = NewShell(page);

        Assert.True(chrome.HasPlusActions);
        chrome.TogglePlusMenuCommand.Execute(null);
        Assert.True(chrome.IsPlusMenuOpen);

        chrome.RunPlusActionCommand.Execute(chrome.PlusActions[0]);

        Assert.Equal(1, runs);
        Assert.False(chrome.IsPlusMenuOpen);
    }

    [Fact]
    public void A_page_without_contracts_offers_no_search_and_no_plus()
    {
        var (_, chrome) = NewShell(new PlainPage());

        Assert.False(chrome.HasSearch);
        Assert.False(chrome.HasPlusActions);
    }

    [Fact]
    public void The_bar_hides_under_a_modal_and_while_the_page_is_immersive()
    {
        var page = new FakePage();
        var (_, chrome) = NewShell(page);
        Assert.True(chrome.IsBarVisible);

        chrome.IsModalOpen = true;
        Assert.False(chrome.IsBarVisible);
        chrome.IsModalOpen = false;
        Assert.True(chrome.IsBarVisible);

        page.IsImmersive = true; // the fullscreen zoom
        Assert.False(chrome.IsBarVisible);
        page.IsImmersive = false;
        Assert.True(chrome.IsBarVisible);
    }

    [Fact]
    public void The_gear_opens_settings_and_closes_the_plus_menu()
    {
        var nav = new NavigationService();
        var opened = 0;
        var chrome = new ShellChromeViewModel(nav, openSettings: () => opened++);
        nav.Reset(new FakePage());
        chrome.TogglePlusMenuCommand.Execute(null);

        chrome.OpenSettingsCommand.Execute(null);

        Assert.True(chrome.HasSettings);
        Assert.Equal(1, opened);
        Assert.False(chrome.IsPlusMenuOpen);
    }

    [Fact]
    public void Crumbs_refresh_when_the_current_pages_title_changes()
    {
        var page = new FakePage("Terrain Ideas");
        var (_, chrome) = NewShell(page);

        page.CrumbTitle = "Terrain Ideas (12 items found)"; // the live search-result count

        Assert.Equal("Terrain Ideas (12 items found)", chrome.Crumbs[^1].Title);
    }

    [Fact]
    public void Navigating_closes_the_plus_menu()
    {
        var (nav, chrome) = NewShell(new FakePage());
        chrome.TogglePlusMenuCommand.Execute(null);
        Assert.True(chrome.IsPlusMenuOpen);

        nav.Push(new FakePage("Next"));

        Assert.False(chrome.IsPlusMenuOpen);
    }
}
