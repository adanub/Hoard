using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Code-behind for the floating bottom bar: the pieces that need the view — focusing the search box when the
/// pill morphs open, the field's own Esc/Enter handling, and the ＋ menu's click-away backdrop. Everything
/// stateful lives on <see cref="ShellChromeViewModel"/>.
/// </summary>
public partial class FloatingBar : UserControl
{
    public FloatingBar()
    {
        InitializeComponent();
        // Click-away for the ＋ menu — primary button only, matching the sheet scrim's filter.
        MenuBackdrop.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(MenuBackdrop).Properties.IsLeftButtonPressed) Chrome?.ClosePlusMenuCommand.Execute(null);
        };
    }

    private ShellChromeViewModel? Chrome => DataContext as ShellChromeViewModel;

    // Open via a Click handler (not just the command) so the view can move focus into the field — posted so
    // the box is visible/measured first (the SheetHost focus idiom).
    private void OnOpenSearchClick(object? sender, RoutedEventArgs e) => FocusSearch();

    /// <summary>Open the search field and move focus into it — the 🔍 button, and Ctrl+F at the window.
    /// Returns false (so the caller can leave the keystroke unhandled) when the current page has no search
    /// or the bar is hidden (modal/zoom/busy).</summary>
    public bool FocusSearch()
    {
        if (Chrome is not { HasSearch: true, IsBarVisible: true } chrome) return false;
        chrome.OpenSearchCommand.Execute(null);
        Dispatcher.UIThread.Post(() =>
        {
            if (chrome.IsSearchOpen) SearchBox.Focus();
        }, DispatcherPriority.Loaded);
        return true;
    }

    // The search field claims Esc while it's open — it closes its own "popup" (the morphed pill), so the
    // window-level Esc-as-back never sees it (the same contract as an open ComboBox dropdown; see the
    // MainWindow Esc notes). Enter submits, for pages where search navigates (the Library's results board).
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Only while genuinely open — if focus were ever left on the collapsed (hidden) box, a handled Esc
        // here would silently eat the window's Esc-as-back.
        if (Chrome is not { IsSearchOpen: true } chrome) return;
        switch (e.Key)
        {
            case Key.Escape:
                chrome.CloseSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                chrome.SubmitSearch();
                e.Handled = true;
                break;
        }
    }
}
