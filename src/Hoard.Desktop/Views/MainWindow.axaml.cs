using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hoard.Desktop.Controls;
using Hoard.Desktop.Infrastructure;
using Hoard.Desktop.Navigation;
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
        // dismiss the whole sheet. Only a key no inner control handled bubbles up to us. (The floating bar's search
        // field follows the same contract: while open it claims Esc to collapse itself.)
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        // Any sheet opening/closing anywhere re-evaluates "is a modal up" (the floating bar hides under modals).
        AddHandler(SheetHost.IsOpenChangedEvent, (_, _) => UpdateModalState());
        // Dev-only (HOARD_LAYOUT_PROBE=1): dump window + element geometry on every resize and on demand, so a
        // "it's drawn at the wrong size" report can be settled as layout-vs-renderer instead of guessed at.
        LayoutProbe.Attach(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm) return;
        // A page popped/pushed while one of ITS sheets was open leaves no IsOpen change to observe — recompute
        // once the view swap has settled so the bar can't stay hidden under a modal that left with its page.
        vm.Navigation.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(NavigationService.Current))
                Dispatcher.UIThread.Post(UpdateModalState, DispatcherPriority.Loaded);
        };
        // The Settings "interface scale": applied here because a transform has no DataContext to bind through.
        ApplyUiScale(vm.Settings.UiScalePercent);
        vm.Settings.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(SettingsViewModel.UiScalePercent))
                ApplyUiScale(vm.Settings.UiScalePercent);
        };
    }

    // A layout (not render) transform over the whole shell: everything re-measures at the new size, so the
    // masonry reflows its columns instead of stretching pixels. Null at 100% — LayoutTransformControl
    // short-circuits to plain measure without a transform, so the default case pays nothing per layout pass.
    private void ApplyUiScale(int percent)
    {
        var scale = percent / 100.0;
        RootScale.LayoutTransform = percent == 100 ? null : new ScaleTransform(scale, scale);
    }

    private void UpdateModalState()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.Chrome.IsModalOpen = TopOpenSheet() is not null;
    }

    /// <summary>The single definition of "a modal sheet is up": the last (topmost) open <see cref="SheetHost"/>
    /// in the visual tree, shared by the modal tracker, Back's dismiss, and Forward's refusal so the three can
    /// never disagree about what counts as open.</summary>
    private SheetHost? TopOpenSheet() => this.GetVisualDescendants().OfType<SheetHost>().LastOrDefault(s => s.IsOpen);

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
        // Ctrl+F (EXACTLY Ctrl — HasFlag would also claim Ctrl+Shift+F and AltGr+F, which is Ctrl+Alt on many
        // European layouts and types a real character): open + focus the floating bar's search. Only handled
        // when it actually did something, so the keystroke isn't eaten under a modal / on a searchless page.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = Bar.FocusSearch();
            return;
        }
        if (e.Key != Key.Escape) return;
        Back();
        e.Handled = true;
    }

    /// <summary>One unified "back" for Esc, the mouse back button, and the floating bar's ← button (via
    /// <c>NavigateBack</c>): close the floating bar's ＋ menu if it's up, else dismiss the topmost open in-app
    /// sheet (transient modals — NOT part of history), else step back one navigation step (zoom → band → page).
    /// The fullscreen zoom is a history step, so it's handled by the navigation back, not the sheet sweep.</summary>
    private void Back()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.Chrome.IsPlusMenuOpen) { vm.Chrome.ClosePlusMenuCommand.Execute(null); return; }
        if (TopOpenSheet() is { } sheet) { sheet.Dismiss(); return; }
        vm.NavigateBack();
    }

    private void Forward()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // A forward gesture under the ＋ menu closes the menu (it's a throwaway popup), then stops — like the
        // sheet check below, it never navigates out from under an open transient surface.
        if (vm.Chrome.IsPlusMenuOpen) { vm.Chrome.ClosePlusMenuCommand.Execute(null); return; }
        // Don't navigate forward out from under an open modal sheet (mirrors Back's sheet check). Forward isn't a
        // "cancel", so it just ignores the gesture while a sheet is open rather than dismissing it.
        if (TopOpenSheet() is not null) return;
        vm.NavigateForward();
    }
}
