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
        // Browser-style mouse back/forward (the XButton1/XButton2 thumb buttons). Handled at the window so the
        // gesture works over any page; handledEventsToo so a child that handled the press (rare for thumb
        // buttons) doesn't swallow it.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
        if (DataContext is not MainWindowViewModel vm) return;

        // A modal sheet (import / edit / sync / move / confirm) is open: swallow the gesture so it doesn't pop the
        // page out from under the overlay. The on-screen back chevron is already covered by the sheet's scrim, so
        // this keeps the thumb buttons consistent with it — dismiss the sheet (Esc / scrim) first.
        if (this.GetVisualDescendants().OfType<SheetHost>().Any(h => h.IsOpen)) { e.Handled = true; return; }

        switch (kind)
        {
            case PointerUpdateKind.XButton1Pressed: // thumb "back"
                vm.NavigateBack();
                e.Handled = true;
                break;
            case PointerUpdateKind.XButton2Pressed: // thumb "forward"
                vm.NavigateForward();
                e.Handled = true;
                break;
        }
    }
}
