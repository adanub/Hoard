using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
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

        // Board card: body opens the board, pencil edits it (popup comes next). Toasts demo the two targets.
        DemoBoardCard.OpenCommand = new RelayCommand(() => _toasts.Show("Open board"));
        DemoBoardCard.EditCommand = new RelayCommand(() => _toasts.Show("Edit board (popup next)"));
    }

    // Theme switch: checked (knob to the moon) = dark, unchecked (knob to the sun) = light.
    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
            RequestedThemeVariant = tb.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void OnOpenSheet(object? sender, RoutedEventArgs e) => DemoSheet.IsOpen = true;
    private void OnCloseSheet(object? sender, RoutedEventArgs e) => DemoSheet.IsOpen = false;

    private void OnShowToast(object? sender, RoutedEventArgs e) => _toasts.Show("Saved your changes.");
    private void OnShowErrorToast(object? sender, RoutedEventArgs e) => _toasts.Show("Something went wrong.", isError: true);
}
