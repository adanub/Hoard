using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hoard.Desktop.Controls;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Browser-style mouse back/forward (XButton1/XButton2 thumb buttons) — handled at the window so the gesture
        // works over any page; handledEventsToo so a child that handled the press doesn't swallow it.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        // Esc == Back, handled at the window so it works on every page regardless of where focus sits. On the BUBBLE
        // route (not tunnel) so a focused control that wants Esc gets it first — a ComboBox dropdown, flyout, or IME
        // composition (e.g. in the Sync/Move sheets) closes its popup instead of having the window eat the key and
        // dismiss the whole sheet. Only a key no inner control handled bubbles up to us.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        switch (kind)
        {
            case PointerUpdateKind.XButton1Pressed: // thumb "back"
                Back();
                e.Handled = true;
                break;
            case PointerUpdateKind.XButton2Pressed: // thumb "forward"
                Forward();
                e.Handled = true;
                break;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Back();
        e.Handled = true;
    }

    /// <summary>One unified "back" for Esc, the mouse back button, and (via <c>NavigateBack</c>) the ← chevron:
    /// dismiss the topmost open in-app sheet (a transient modal — NOT part of history) if there is one, else step
    /// back one navigation step (zoom → band → page). The fullscreen zoom is a history step, so it's handled by the
    /// navigation back, not the sheet sweep.</summary>
    private void Back()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var sheet = this.GetVisualDescendants().OfType<SheetHost>().LastOrDefault(s => s.IsOpen);
        if (sheet is not null) { sheet.Dismiss(); return; }
        vm.NavigateBack();
    }

    private void Forward()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // Don't navigate forward out from under an open modal sheet (mirrors Back's sheet check). Forward isn't a
        // "cancel", so it just ignores the gesture while a sheet is open rather than dismissing it.
        if (this.GetVisualDescendants().OfType<SheetHost>().Any(s => s.IsOpen)) return;
        vm.NavigateForward();
    }
}
