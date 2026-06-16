using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Hoard.Desktop.Views;

/// <summary>
/// Dev-only living catalogue of the design system (tokens, components, icons) — our Storybook equivalent.
/// Launch with the env var <c>HOARD_GALLERY=1</c>. Keep it current as components are added (see DESIGN.md).
/// Click-away-clears-focus is handled declaratively by <c>FocusManagement.ClearFocusOnPointerPressed</c>.
/// </summary>
public partial class GalleryWindow : Window
{
    public GalleryWindow()
    {
        InitializeComponent();
    }

    // Theme switch: checked (knob to the moon) = dark, unchecked (knob to the sun) = light.
    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
            RequestedThemeVariant = tb.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
