using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;

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

    /// <summary>How wide the card may grow (it still STRETCHES down to fit a narrow window). The default suits
    /// a form — a column of fields and a couple of buttons. Raise it for a sheet whose content is text to be
    /// READ rather than filled in (the toast details dump), where 440px would wrap a log into a ribbon.</summary>
    public static readonly StyledProperty<double> CardMaxWidthProperty =
        AvaloniaProperty.Register<SheetHost, double>(nameof(CardMaxWidth), 440);

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

    public double CardMaxWidth
    {
        get => GetValue(CardMaxWidthProperty);
        set => SetValue(CardMaxWidthProperty, value);
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
        {
            WarnIfContentHasFixedWidth();
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsOpen) return; // closed again before the post ran
                var target = (IInputElement?)this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible)
                             ?? this;
                target.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// DEBUG-only: the card stretches to fit and caps at <see cref="CardMaxWidth"/>, so content with a fixed
    /// <c>Width</c> doesn't widen the card — it draws OUTSIDE it, spilling symmetrically past both edges and
    /// cutting off its own text. The rule is stated in <c>Theme/Controls/Sheet.axaml</c>'s header and was
    /// broken anyway (a 520px sheet in a 440px card), which is why it's a check now rather than more prose:
    /// prose can't fire. Same self-announcing spirit as <c>Infrastructure/LeakCanary</c> — it costs nothing in
    /// a release build and shouts the first time anyone opens the sheet in development.
    /// </summary>
    [Conditional("DEBUG")]
    private void WarnIfContentHasFixedWidth()
    {
        if (Content is not Layoutable content) return;
        // Only the two that can force a desired width past the card's constraint. A MaxWidth is never a
        // problem — narrower than the cap is a deliberate choice and wider simply never binds — so flagging
        // it would cry wolf, and a check nobody trusts is worse than no check.
        var forced = !double.IsNaN(content.Width) ? content.Width
                   : content.MinWidth > CardMaxWidth ? content.MinWidth
                   : double.NaN;
        if (double.IsNaN(forced)) return;
        Log.Warning(
            "Sheet content {Content} forces a width of {Forced}px inside a card capped at {Cap}px — that "
            + "doesn't widen the card, it overflows both of its edges. Drop the width and raise the host's "
            + "CardMaxWidth instead (see Theme/Controls/Sheet.axaml).",
            content.GetType().Name, forced, CardMaxWidth);
    }

    /// <summary>Close the sheet (raises <see cref="DismissCommand"/>). Public so the shell's unified Back
    /// (<c>MainWindow</c>) can dismiss the topmost open sheet on Esc / the mouse back button, same as the scrim.</summary>
    public void Dismiss()
    {
        if (DismissCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }
}
