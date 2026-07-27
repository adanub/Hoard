using System;

namespace Hoard.Desktop.Infrastructure;

/// <summary>
/// Where a scrolling viewport's alpha ramps sit — the pure geometry behind <see cref="Controls.Marquee"/>'s
/// opacity mask, kept Avalonia-free so it can be unit-tested (the MasonryPacker rule).
/// </summary>
internal static class EdgeFade
{
    /// <summary>
    /// The fully-opaque band of a viewport, as relative (0..1) offsets: content ramps up from transparent
    /// before <c>Start</c> and back down after <c>End</c>.
    /// <para>A side only fades by as much as it actually hides, so a viewport whose content is at rest
    /// keeps a crisp leading edge and grows its fade in as the content pans under the edge. Both ramps cap
    /// at half the width, so a viewport narrower than two fades ramps to a point instead of producing
    /// crossed-over stops.</para>
    /// </summary>
    /// <param name="width">Viewport width.</param>
    /// <param name="fadeWidth">How far a fully-hidden edge takes to reach full opacity.</param>
    /// <param name="overflow">How much wider the content is than the viewport (0 = it fits).</param>
    /// <param name="pan">The content's current X offset: 0 at rest, -overflow when panned to the end.</param>
    public static (double Start, double End) Band(double width, double fadeWidth, double overflow, double pan)
    {
        if (width <= 0 || fadeWidth <= 0 || overflow <= 0) return (0, 1);

        var cap = Math.Min(fadeWidth, width / 2);
        var left = Math.Clamp(-pan, 0, cap);
        var right = Math.Clamp(overflow + pan, 0, cap);
        return (left / width, 1 - right / width);
    }
}
