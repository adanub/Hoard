using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;

namespace Hoard.Desktop.Infrastructure;

/// <summary>
/// Dev-only layout diagnostic, enabled with <c>HOARD_LAYOUT_PROBE=1</c> (same spirit as <c>HOARD_GALLERY</c> /
/// <c>HOARD_UPDATE_DEMO</c>). It answers the one question a screenshot can't: when the window looks wrong, is the
/// LAYOUT wrong or is the RENDERER drawing a correct layout at the wrong size?
///
/// Every dump prints the window's own metrics (client size, frame, scaling, state, the screen it's on) and then,
/// per interesting element, its <c>Bounds</c> AND its full <c>TransformToVisual(window)</c> matrix. Those two
/// together are the discriminator:
/// <list type="bullet">
/// <item>Bounds too wide for the client size → a real layout bug, ours to fix.</item>
/// <item>Bounds correct and every matrix ≈ identity, yet the screen looks zoomed → the scene graph is right and
/// the compositor is scaling a stale surface; a platform/renderer problem, not a layout one.</item>
/// <item>A matrix with a scale ≠ the UI-scale setting → a stray <c>RenderTransform</c> left on an ancestor
/// (hit-testing follows the same matrix, so this is what "clicks land elsewhere" would look like).</item>
/// </list>
///
/// Dumps fire on client-size / window-state / scaling changes (immediately, then again after the layout settles —
/// a resize that only lands late shows up as a second, different dump), on <see cref="Note"/> calls from
/// interesting moments (the lightbox opening/closing), and on demand via <b>Ctrl/⌘ + Shift + L</b>, which is the
/// one that matters: reproduce the glitch, press the chord, read <c>hoard.log</c>.
/// </summary>
internal static class LayoutProbe
{
    /// <summary>Whether the probe is switched on for this run. Everything below is a no-op when false, so the
    /// call sites don't need their own guards.</summary>
    public static bool IsEnabled { get; } =
        Environment.GetEnvironmentVariable("HOARD_LAYOUT_PROBE") == "1";

    // The attached window, weakly held — the probe must never be the reason a window survives collection.
    private static WeakReference<Window>? _window;
    private static DispatcherTimer? _settle;

    // Named elements worth reporting when they're in the tree. Anything absent is simply skipped, so this list
    // can name elements from pages that aren't currently shown.
    private static readonly string[] InterestingNames =
        ["RootScale", "Bar", "GridScroll", "AssetGrid"];

    public static void Attach(Window window)
    {
        if (!IsEnabled) return;
        _window = new WeakReference<Window>(window);

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ClientSizeProperty) Schedule("client-size");
            else if (e.Property == Window.WindowStateProperty) Schedule($"window-state={window.WindowState}");
        };
        window.ScalingChanged += (_, _) => Schedule("scaling");

        // Tunnel, handledEventsToo: the probe must fire from anywhere, including while a control has focus and
        // swallows the key. It never marks the event handled, so it can't shadow a real binding.
        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                var chord = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                    && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
                if (e.Key == Key.L && chord) Dump("manual (Ctrl/⌘+Shift+L)");
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);

        Log.Information("[probe] Layout probe armed — Ctrl/⌘+Shift+L dumps the layout to this log");
        Schedule("attached");
    }

    /// <summary>Record a dump around an interesting moment (called from the lightbox open/close, which is where
    /// the resize desync was first seen). Cheap no-op when the probe is off.</summary>
    public static void Note(string reason)
    {
        if (!IsEnabled) return;
        Schedule(reason);
    }

    // Dump now AND once more after the tree settles. "The resize takes a moment to take effect" is exactly the
    // kind of thing only a second, later dump can show — if the two disagree, the late one is what the user sees.
    private static void Schedule(string reason)
    {
        Dump(reason);
        _settle?.Stop();
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _settle.Tick += (_, _) =>
        {
            _settle?.Stop();
            _settle = null;
            Dump($"{reason} (settled)");
        };
        _settle.Start();
    }

    private static void Dump(string reason)
    {
        if (!IsEnabled) return;
        if (_window is null || !_window.TryGetTarget(out var window)) return;
        // Passed as a PROPERTY, never as the message template itself: the report is composed from values
        // (matrices, rects, enum names) that Serilog would otherwise try to parse for {…} tokens. Both sinks
        // render {Message:lj}, so the literal flag keeps it unquoted and readable.
        Log.Information("{ProbeReport:l}", Report(window, reason));
    }

    /// <summary>The dump itself, as a string, for any host that wants it — the running app logs it, and the
    /// headless render harness (<c>tools/Hoard.Harness</c>) prints it beside each PNG so a layout can be
    /// asserted on numbers rather than eyeballed.</summary>
    public static string Report(Window window, string reason)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"[probe] ── {reason} ──\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"[probe]   window   client={Fmt(window.ClientSize)} " +
                $"frame={(window.FrameSize is { } frame ? Fmt(frame) : "n/a")} " +
                $"bounds={Fmt(window.Bounds)} state={window.WindowState} " +
                $"renderScaling={window.RenderScaling:0.###} desktopScaling={window.DesktopScaling:0.###}\n");

            try
            {
                if (window.Screens.ScreenFromWindow(window) is { } screen)
                    sb.Append(CultureInfo.InvariantCulture,
                        $"[probe]   screen   bounds={screen.Bounds} workingArea={screen.WorkingArea} " +
                        $"scaling={screen.Scaling:0.###}\n");
            }
            catch
            {
                // Screen enumeration is best-effort — never let diagnostics take the app down.
            }

            foreach (var (label, visual) in Targets(window))
                sb.Append(Describe(label, visual, window));

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"[probe] layout dump failed: {ex}";
        }
    }

    // The elements worth a line: the named shell/page parts, plus whichever page view is currently mounted
    // (found by type, since each page is a different class).
    private static IEnumerable<(string Label, Visual Visual)> Targets(Window window)
    {
        var descendants = window.GetVisualDescendants().OfType<Control>().ToList();

        foreach (var name in InterestingNames)
            if (descendants.FirstOrDefault(c => c.Name == name) is { } named)
                yield return (name, named);

        if (descendants.FirstOrDefault(c => c is UserControl && c.GetType().Namespace == "Hoard.Desktop.Views") is { } page)
            yield return ($"page:{page.GetType().Name}", page);
    }

    private static string Describe(string label, Visual visual, Window window)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"[probe]   {label,-22} bounds={Fmt(visual.Bounds)}");

        // The full accumulated matrix from this element up to the window — render transforms included. This is
        // the same chain hit-testing walks, so a mismatch between it and what's on screen is a renderer problem.
        if (visual.TransformToVisual(window) is { } m)
            line.Append(CultureInfo.InvariantCulture,
                $" toWindow=[{m.M11:0.###} {m.M12:0.###}; {m.M21:0.###} {m.M22:0.###}] +({m.M31:0.#},{m.M32:0.#})");

        if (visual.RenderTransform is { } rt)
            line.Append(CultureInfo.InvariantCulture, $" renderTransform={rt.Value}");

        if (visual is LayoutTransformControl ltc)
            line.Append(CultureInfo.InvariantCulture,
                $" layoutTransform={(ltc.LayoutTransform is null ? "none" : ltc.LayoutTransform.Value.ToString())}");

        // A ScrollViewer's extent vs viewport is what says whether content is reachable — the "pins cut off at
        // the bottom" class of bug reads straight off these three numbers.
        if (visual is ScrollViewer sv)
            line.Append(CultureInfo.InvariantCulture,
                $" extent={Fmt(sv.Extent)} viewport={Fmt(sv.Viewport)} offset={Fmt(sv.Offset)} padding={sv.Padding}");

        return line.Append('\n').ToString();
    }

    private static string Fmt(Size s) => $"{s.Width:0.#}×{s.Height:0.#}";
    private static string Fmt(Vector v) => $"{v.X:0.#},{v.Y:0.#}";
    private static string Fmt(Rect r) => $"{r.Width:0.#}×{r.Height:0.#}@{r.X:0.#},{r.Y:0.#}";
}
