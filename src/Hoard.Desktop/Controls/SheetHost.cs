using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A reusable in-app modal sheet: dims the page with a scrim and centres its <see cref="ContentControl.Content"/>
/// in a floating card (DESIGN.md — "Sheet / Dialog = card + ShadowMd + scrim"; mobile-ready, unlike an OS
/// dialog window). The whole host is hidden while closed. Clicking the scrim raises <see cref="DismissCommand"/>;
/// Esc is handled ONCE at the window (MainWindow dismisses the topmost open sheet) — deliberately NOT here: a
/// per-sheet Esc handler fires on whichever sheet FOCUS bubbles through, so with a confirm floating over an edit
/// sheet (focus still on the edit sheet's button that opened it) it dismissed the underlying edit sheet out from
/// under the confirm. Opening moves focus to the sheet's first TextBox (or the sheet itself) so typing lands in
/// the sheet, not the scrim-covered page. Page-local for now; lift to the shell when more screens need sheets.
/// </summary>
public class SheetHost : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<SheetHost, bool>(nameof(IsOpen));

    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<SheetHost, ICommand?>(nameof(DismissCommand));

    /// <summary>Bubbles to the window whenever a sheet opens or closes, so the shell can track "any sheet
    /// open" without polling (it hides the floating bar while a modal owns the screen).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IsOpenChangedEvent =
        RoutedEvent.Register<SheetHost, RoutedEventArgs>(nameof(IsOpenChanged), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> IsOpenChanged
    {
        add => AddHandler(IsOpenChangedEvent, value);
        remove => RemoveHandler(IsOpenChangedEvent, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public ICommand? DismissCommand
    {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public SheetHost() => Focusable = true; // the focus fallback target when a sheet has no TextBox (confirm sheets)

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // The scrim (the dimmed backdrop) is the click-away target; the card sits above it and swallows clicks.
        // Dismiss on a primary (left) click only — a right / middle / thumb press over the scrim shouldn't close it.
        if (e.NameScope.Find<Control>("PART_Scrim") is { } scrim)
            scrim.PointerPressed += (_, ev) =>
            {
                if (ev.GetCurrentPoint(scrim).Properties.IsLeftButtonPressed) Dismiss();
            };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsOpenProperty) return;

        // Tell the shell (bubbles to MainWindow) on every open/close so it can recompute "any sheet open".
        RaiseEvent(new RoutedEventArgs(IsOpenChangedEvent));

        // Opening: move focus INTO the sheet (its first TextBox, else the sheet itself) — the scrim blocks pointers
        // but not keys, so without this the first keystrokes went to whatever scrim-covered control opened the sheet
        // (e.g. Space re-invoking the opener). Posted so the sheet has become visible/measured first.
        if (IsOpen)
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsOpen) return; // closed again before the post ran
                var target = (IInputElement?)this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible)
                             ?? this;
                target.Focus();
            }, DispatcherPriority.Loaded);
    }

    /// <summary>Close the sheet (raises <see cref="DismissCommand"/>). Public so the shell's unified Back
    /// (<c>MainWindow</c>) can dismiss the topmost open sheet on Esc / the mouse back button, same as the scrim.</summary>
    public void Dismiss()
    {
        if (DismissCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }
}
