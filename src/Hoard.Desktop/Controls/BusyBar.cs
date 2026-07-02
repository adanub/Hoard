using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Hoard.Desktop.Controls;

/// <summary>
/// The app's indeterminate progress spinner: a <see cref="ProgressBar"/> whose indeterminate animation runs
/// ONLY while the bar is actually visible and attached. Use this — never a raw
/// <c>&lt;ProgressBar IsIndeterminate="True"&gt;</c> — for any busy indicator.
///
/// Why this exists (memory-leak root cause, found via a ClrMD heap dump): Fluent's indeterminate template runs an
/// <b>infinite</b> keyframe animation, and Avalonia keeps infinite animations ticking even when the control is
/// <c>IsVisible="False"</c> (AvaloniaUI/Avalonia discussion #15389) and — worse — when its tree has been DETACHED
/// (issues #15793, #17192). A still-ticking animation re-registers its composition visuals with the
/// <c>Compositor</c> every frame, which keeps the whole detached page (via child→ancestor value-store chains)
/// permanently rooted: with plain ProgressBars in every masonry tile, each Board screen leaked wholesale on
/// back-out, ~20–35 MB per navigation. Driving <c>IsIndeterminate</c> from visibility clears the
/// <c>:indeterminate</c> pseudo-class while the control is still attached, which stops the style animation — so a
/// hidden spinner costs nothing and a detached page stops re-registering and gets collected.
///
/// Contract: bind/set the BusyBar's <b>own</b> <see cref="Control.IsVisible"/> to the busy condition — do not rely
/// on hiding an ancestor (effective visibility has no change notification, so an ancestor-only gate would leave the
/// animation running invisibly). <c>IsIndeterminate</c> is owned by this class and follows automatically.
/// </summary>
public class BusyBar : ProgressBar
{
    // Style as a plain ProgressBar (Fluent template + the app's ProgressBar styles apply unchanged).
    protected override Type StyleKeyOverride => typeof(ProgressBar);

    // Own-property change notifications arrive here for free — no per-instance observable/subscription needed
    // (these bars are instantiated ×3 per masonry tile). IsVisible (self) + attach/detach covers every way the
    // bar stops being shown.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Sync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Detach fires no IsVisible change, so this is the ONLY detach-time stop — do not remove it: leaving the
        // infinite animation running against a detached tree is exactly the leak this control exists to prevent.
        Sync();
    }

    private void Sync() => IsIndeterminate = IsVisible && this.IsAttachedToVisualTree();
}
