using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Hoard.Desktop.Infrastructure;

/// <summary>
/// The single home for focus policy. Today that's the "click-away clears focus" attached behaviour; any
/// future focus concerns (dialog focus-trapping, restore-focus, etc.) belong here too, so callers never
/// hand-roll focus handling in views.
///
/// The design keeps the Avalonia plumbing thin and pushes the decision into a pure function
/// (<see cref="ShouldClearFocus"/>, unit-tested) and the "how we clear focus" workaround into a single
/// chokepoint (<see cref="ClearFocusTo"/>) — so when Avalonia ships a real <c>ClearFocus</c> API, exactly
/// one method changes. Usage is enforced by an architecture test (focus-clearing primitives may only appear
/// in this file).
///
/// Apply with <c>inf:FocusManagement.ClearFocusOnPointerPressed="True"</c> on a page/window root.
/// </summary>
public static class FocusManagement
{
    public static readonly AttachedProperty<bool> ClearFocusOnPointerPressedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("ClearFocusOnPointerPressed", typeof(FocusManagement));

    public static bool GetClearFocusOnPointerPressed(Control c) => c.GetValue(ClearFocusOnPointerPressedProperty);
    public static void SetClearFocusOnPointerPressed(Control c, bool value) => c.SetValue(ClearFocusOnPointerPressedProperty, value);

    static FocusManagement()
    {
        ClearFocusOnPointerPressedProperty.Changed.AddClassHandler<Control>((host, e) =>
        {
            // Idempotent: remove first so re-applying the property (or a same-value change at a different
            // priority) can't double-register the handler.
            host.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            if (e.GetNewValue<bool>())
            {
                // The root is a neutral, *indicator-less* focus sink: focusable so it can hold focus, but
                // never a keyboard tab stop and never adorned — so parking focus on it (see ClearFocusTo)
                // has no tab-order or focus-ring side effects.
                host.Focusable = true;
                host.IsTabStop = false;
                host.FocusAdorner = null;
                host.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            }
            else
            {
                host.Focusable = false;
                host.IsTabStop = true; // restore the default
            }
        });
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control host || !host.IsKeyboardFocusWithin) return;
        if (ShouldClearFocus(FocusablePathToHost(e.Source as Visual, host)))
            ClearFocusTo(host);
    }

    /// <summary>
    /// The policy (pure, so it's unit-tested directly): clear focus only when the press landed on background
    /// or non-interactive chrome — i.e. nothing focusable-and-enabled sits between the click target and the
    /// host. A press on a focusable control is left alone; Avalonia transfers focus to it itself.
    /// </summary>
    internal static bool ShouldClearFocus(IEnumerable<(bool Focusable, bool Enabled)> targetToHostPath)
        => targetToHostPath.All(e => !(e.Focusable && e.Enabled));

    /// <summary>The (Focusable, Enabled) of each input element from the click target up to — not including — the host.</summary>
    private static IEnumerable<(bool, bool)> FocusablePathToHost(Visual? from, Control host)
    {
        for (var v = from; v is not null && !ReferenceEquals(v, host); v = v.GetVisualParent())
            if (v is InputElement ie)
                yield return (ie.Focusable, ie.IsEffectivelyEnabled);
    }

    /// <summary>
    /// The one place that "clears" focus. This Avalonia version has no public clear-focus API, so we park
    /// focus on a neutral, indicator-less sink (the root). Swap just this method if a real API arrives.
    /// </summary>
    private static void ClearFocusTo(Control sink) => sink.Focus();
}
