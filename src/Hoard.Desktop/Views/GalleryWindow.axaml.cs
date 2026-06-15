using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Hoard.Desktop.Views;

/// <summary>
/// Dev-only living catalogue of the design system (tokens, components, icons) — our Storybook equivalent.
/// Launch with the env var <c>HOARD_GALLERY=1</c>. Keep it current as components are added (see DESIGN.md).
/// </summary>
public partial class GalleryWindow : Window
{
    public GalleryWindow()
    {
        InitializeComponent();
    }

    private void OnLight(object? sender, RoutedEventArgs e) => RequestedThemeVariant = ThemeVariant.Light;
    private void OnDark(object? sender, RoutedEventArgs e) => RequestedThemeVariant = ThemeVariant.Dark;
}
