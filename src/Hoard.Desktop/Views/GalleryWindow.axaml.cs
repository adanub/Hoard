using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Controls;
using Hoard.Desktop.Services;

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

    private void OnShowToast(object? sender, RoutedEventArgs e) => _toasts.Show("Saved your changes.");
    private void OnShowErrorToast(object? sender, RoutedEventArgs e) => _toasts.Show("Something went wrong.", isError: true);
}
