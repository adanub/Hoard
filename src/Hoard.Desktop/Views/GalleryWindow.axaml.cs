using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Controls;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.Services;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

/// <summary>
/// Dev-only living catalogue of the design system (tokens, components, icons) — our Storybook equivalent.
/// Launch with the env var <c>HOARD_GALLERY=1</c>. Keep it current as components are added (see DESIGN.md).
/// Click-away-clears-focus is handled declaratively by <c>FocusManagement.ClearFocusOnPointerPressed</c>.
/// </summary>
public partial class GalleryWindow : Window
{
    private readonly ToastService _toasts = new();

    public GalleryWindow()
    {
        InitializeComponent();
        DemoToasts.DataContext = _toasts;
        DemoSheet.DismissCommand = new RelayCommand(() => DemoSheet.IsOpen = false);

        // A toast's ⋯ sets DetailsToast; mirror the shell's wiring so the gallery exercises the real path.
        _toasts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ToastService.DetailsToast)) return;
            DemoToastDetailsSheet.DataContext = _toasts.DetailsToast;
            ToastDetailsDemoSheet.IsOpen = _toasts.DetailsToast is not null;
        };
        DemoToastDetailsSheet.CloseCommand = _toasts.CloseDetailsCommand;
        ToastDetailsDemoSheet.DismissCommand = _toasts.CloseDetailsCommand;

        // The project card's Edit button opens its Edit popup.
        DemoProjectCard.EditCommand = new RelayCommand(() => ProjectEditDemoSheet.IsOpen = true);
        ProjectEditDemoSheet.DismissCommand = new RelayCommand(() => ProjectEditDemoSheet.IsOpen = false);

        ConfirmDemoSheet.DismissCommand = new RelayCommand(() => ConfirmDemoSheet.IsOpen = false);

        // Project Edit popup: non-destructive actions toast; Delete routes through the confirm popup.
        DemoProjectEditSheet.RenameCommand = new RelayCommand<object?>(name => _toasts.Show($"Renamed to “{name}”"));
        DemoProjectEditSheet.OpenFolderCommand = new RelayCommand(() => _toasts.Show("Open in file explorer"));
        DemoProjectEditSheet.ClearCacheCommand = new RelayCommand(() => _toasts.Show("Cleared cache"));
        DemoProjectEditSheet.RemoveCommand = new RelayCommand(() => _toasts.Show("Removed from list"));
        DemoProjectEditSheet.DeleteCommand = new RelayCommand(() => ShowConfirm(
            "Delete project?", "Permanently delete this project and all its data? It's moved to your system's recycle bin.",
            "Delete", 10, () => _toasts.Show("Project deleted (→ recycle bin)", isError: true)));

        // New cards: the whole tile is the action.
        DemoNewCard.Command = new RelayCommand(() => _toasts.Show("New board…"));
        DemoNewProjectCard.Command = new RelayCommand(() => _toasts.Show("New project…"));

        // Board card: body opens the board; pencil opens the board Edit popup.
        DemoBoardCard.OpenCommand = new RelayCommand(() => _toasts.Show("Open board"));
        DemoBoardCard.EditCommand = new RelayCommand(() => BoardEditDemoSheet.IsOpen = true);
        BoardEditDemoSheet.DismissCommand = new RelayCommand(() => BoardEditDemoSheet.IsOpen = false);

        // Board Edit popup demo: placeholder merged source boards. Destructive actions route through the confirm.
        DemoBoardEditSheet.SourceBoards = new List<BoardSourceRef>
        {
            new(1, "alice/animation-refs", "pinterest.com/alice/animation-refs", 27),
            new(2, "bob/keyframes", "pinterest.com/bob/keyframes", 0),
        };
        DemoBoardEditSheet.RenameCommand = new RelayCommand<object?>(name => _toasts.Show($"Renamed to “{name}”"));
        DemoBoardEditSheet.OpenSourceCommand = new RelayCommand<BoardSourceRef?>(s => _toasts.Show($"Open {s?.Name} in browser"));
        DemoBoardEditSheet.RemoveSourceCommand = new RelayCommand<BoardSourceRef?>(s => ShowConfirm(
            "Remove source board?", $"Remove “{s?.Name}” from this board? Its images stay unless you delete the board.",
            "Remove", 0, () => _toasts.Show($"Removed {s?.Name}")));
        DemoBoardEditSheet.AddSourceCommand = new RelayCommand(() => _toasts.Show("Add source board…"));
        DemoBoardEditSheet.ClearCacheCommand = new RelayCommand(() => _toasts.Show("Cleared board cache"));
        DemoBoardEditSheet.DeleteCommand = new RelayCommand(() => ShowConfirm(
            "Delete board?", "Delete this board and its images? They're moved to your system's recycle bin.",
            "Delete", 10, () => _toasts.Show("Board deleted (→ recycle bin)", isError: true)));

        WireShellChromeDemo();
    }

    // Shell chrome: a sample four-deep navigation drives the breadcrumb bars and the floating bar, so the
    // crumb clicks, back button, search morph, ＋ menu, and ⚙ all work in place.
    private void WireShellChromeDemo()
    {
        var nav = new NavigationService();
        var chrome = new ShellChromeViewModel(nav, openSettings: OpenSettingsDemo);
        nav.Reset(new DemoPage("Projects", _toasts, "New project"));

        void PushDemoTrail()
        {
            nav.Push(new DemoPage("Pinterest Backup", _toasts, "Import board"));
            nav.Push(new DemoPage("Terrain Ideas", _toasts, "Sync board", "New folder"));
            nav.Push(new DemoPage("Buildings", _toasts, "Sync board", "New folder"));
        }
        PushDemoTrail();

        void SyncTrails()
        {
            DemoBreadcrumbWide.Trail = chrome.Crumbs;
            DemoBreadcrumbNarrow.Trail = chrome.Crumbs;
        }
        SyncTrails();
        chrome.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName != nameof(ShellChromeViewModel.Crumbs)) return;
            SyncTrails();
            // Backed all the way out (crumb click / ← presses): re-push the trail so the demo stays
            // exercisable — there's no forward gesture in the gallery to recover it otherwise.
            if (nav.PageChain.Count == 1) PushDemoTrail();
        };
        DemoBreadcrumbWide.NavigateCommand = chrome.NavigateToCrumbCommand;
        DemoBreadcrumbNarrow.NavigateCommand = chrome.NavigateToCrumbCommand;
        DemoFloatingBar.DataContext = chrome;

        // The Settings sheet over a design-time VM: nothing persists, but the theme choice really applies.
        // The host follows the VM's IsOpen so the sheet's own Done button (CloseCommand → IsOpen=false)
        // actually closes it, like the shell's bound SheetHost.
        // The update VM carries no update service, so nothing is ever checked, downloaded or applied — it just
        // renders. It also feeds the Settings sheet's Updates section (without one that section hides itself).
        // With no service IsSupported is false, so the section shows its "this build doesn't update itself"
        // note; run the gallery with HOARD_UPDATE_DEMO=1 as well to see the toggles and buttons instead.
        _demoUpdate = new UpdateViewModel { AvailableVersion = "1.4.0" };
        DemoUpdateSheet.DataContext = _demoUpdate;
        _demoUpdate.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(UpdateViewModel.IsOpen)) UpdateDemoSheet.IsOpen = _demoUpdate.IsOpen;
        };
        UpdateDemoSheet.DismissCommand = new RelayCommand(() => _demoUpdate.IsOpen = false);

        _demoSettings = new SettingsViewModel(null, null, null, _demoUpdate);
        DemoSettingsSheet.DataContext = _demoSettings;
        _demoSettings.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(SettingsViewModel.IsOpen)) SettingsDemoSheet.IsOpen = _demoSettings.IsOpen;
        };
        SettingsDemoSheet.DismissCommand = new RelayCommand(() => _demoSettings.IsOpen = false);
    }

    private SettingsViewModel? _demoSettings;
    private UpdateViewModel? _demoUpdate;

    private void OpenSettingsDemo()
    {
        if (_demoSettings is not null) _demoSettings.IsOpen = true;
    }

    private void OpenUpdateDemo()
    {
        if (_demoUpdate is not null) _demoUpdate.IsOpen = true;
    }

    /// <summary>A sample page for the shell-chrome demos: breadcrumb-titled, searchable, with ＋ actions.</summary>
    private sealed class DemoPage : ViewModelBase, ICrumbTitled, IProvidesSearch, IProvidesPlusActions
    {
        private readonly ToastService _toasts;
        private string _searchText = "";

        public DemoPage(string title, ToastService toasts, params string[] actions)
        {
            CrumbTitle = title;
            _toasts = toasts;
            PlusActions = actions.Select(a => new PlusAction(a, new RelayCommand(() => toasts.Show($"{a}…")))).ToArray();
        }

        public string CrumbTitle { get; }
        public IReadOnlyList<PlusAction> PlusActions { get; }

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        public string SearchPlaceholder => "Search the demo…";
        public void SubmitSearch() => _toasts.Show($"Searched for “{SearchText}”");
    }

    // Theme switch: checked (knob to the moon) = dark, unchecked (knob to the sun) = light.
    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
            RequestedThemeVariant = tb.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // Configure + open the shared confirm popup (on top of the current Edit popup); runs onConfirmed if confirmed.
    private void ShowConfirm(string title, string message, string confirmLabel, int countdown, System.Action onConfirmed)
    {
        DemoConfirmSheet.Title = title;
        DemoConfirmSheet.Message = message;
        DemoConfirmSheet.ConfirmLabel = confirmLabel;
        DemoConfirmSheet.ConfirmCommand = new RelayCommand(() => { ConfirmDemoSheet.IsOpen = false; onConfirmed(); });
        DemoConfirmSheet.CancelCommand = new RelayCommand(() => ConfirmDemoSheet.IsOpen = false);
        DemoConfirmSheet.Begin(countdown);
        ConfirmDemoSheet.IsOpen = true;
    }

    private void OnOpenSheet(object? sender, RoutedEventArgs e) => DemoSheet.IsOpen = true;
    private void OnCloseSheet(object? sender, RoutedEventArgs e) => DemoSheet.IsOpen = false;
    private void OnOpenSettingsDemo(object? sender, RoutedEventArgs e) => OpenSettingsDemo();
    private void OnOpenUpdateDemo(object? sender, RoutedEventArgs e) => OpenUpdateDemo();

    private void OnShowToast(object? sender, RoutedEventArgs e) => _toasts.Show("Saved your changes.");
    private void OnShowErrorToast(object? sender, RoutedEventArgs e) => _toasts.Show("Something went wrong.", isError: true);

    // The shape the real thing takes: a short line that fits the card, with the run's full story behind ⋯.
    // The detail is deliberately long AND full of unbreakably long URLs — that's what proves the sheet wraps
    // instead of overflowing sideways, and scrolls instead of capping what it will show.
    private void OnShowDetailedToast(object? sender, RoutedEventArgs e)
    {
        var boards = new[]
        {
            "Dofus/Wakfu", "Animation References", "NPC References", "Map Ideas", "Water References",
            "3D Art", "Drawing Tips", "Character Hair/Face References", "Alchemist Ideas", "ear-refs",
            "Terrain Ideas", "Enemy Ideas", "Equipment", "Weapons", "Abilities", "Isometric",
        };
        var detail = new StringBuilder($"{boards.Length} boards failed.\n");
        foreach (var board in boards)
        {
            var slug = board.ToLowerInvariant().Replace(' ', '-').Replace('/', '-');
            detail.AppendLine();
            detail.AppendLine(board);
            detail.AppendLine($"  Pinterest returned \"board not found\". [1/2] https://www.pinterest.com/jane/{slug}/"
                              + " | [pinterest][error] NotFoundError: Requested board could not be found"
                              + $" | [2/2] https://www.pinterest.com/jane/{slug}/id:5127181054144193440"
                              + " | [pinterest][error] NotFoundError: Requested section could not be found");
        }

        _toasts.Show(
            "Sync failed — no board could be fetched. Pinterest returned \"board not found\". If the board is "
            + "private, select the browser you're logged into Pinterest with in the Cookies dropdown.",
            isError: true, details: detail.ToString().TrimEnd());
    }

    // Past the cap: the stack holds MaxVisible and sheds the OLDEST ROUTINE one, so an error already on the
    // pile is still there at the end. Enough of them to make the column scroll on a short window, too.
    private void OnFillToasts(object? sender, RoutedEventArgs e)
    {
        for (var i = 1; i <= ToastService.MaxVisible + 3; i++)
            _toasts.Show($"Routine message {i} — synced a board.");
    }
}
