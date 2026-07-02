using System;
using System.Collections.Generic;

namespace Hoard.Desktop.Controls;

/// <summary>A breadcrumb segment chosen by <see cref="BreadcrumbTrimmer.Fit"/>: which original title it is
/// (so the control can wire the right click target) and the text to render — only ever the FIRST visible
/// segment differs from its full title (a leading "…" marks everything elided to its left).</summary>
internal readonly record struct BreadcrumbSegment(int Index, string Text);

/// <summary>
/// The pure fitting maths behind <see cref="BreadcrumbBar"/> (Avalonia-free, unit-tested — the MasonryPacker
/// rule). The rightmost (current-page) crumb keeps priority; when the trail overflows, the ellipsis always
/// eats from the BASE end: the leftmost visible segment is trimmed from its start ("…rest Backup"), whole
/// ancestors drop off when even a trimmed stub wouldn't be meaningful, and a dropped ancestor leaves its "…"
/// marker on the next segment ("…Terrain Ideas"). Text measurement is injected so the logic stays pure.
/// </summary>
internal static class BreadcrumbTrimmer
{
    internal const string Ellipsis = "…";

    /// <summary>A trimmed non-current segment must keep at least this many of its own characters — below that
    /// a stub like "…p" says nothing, so the segment drops entirely instead. The current page's crumb is
    /// exempt (it always shows whatever fits).</summary>
    private const int MinTrimmedChars = 3;

    /// <summary>Choose what to render for <paramref name="titles"/> (root first, current page last) in
    /// <paramref name="availableWidth"/>. <paramref name="separatorWidth"/> is the full width one rendered
    /// separator occupies (glyph + its padding); <paramref name="measure"/> maps text to rendered width.</summary>
    internal static IReadOnlyList<BreadcrumbSegment> Fit(
        IReadOnlyList<string> titles, double availableWidth, double separatorWidth, Func<string, double> measure)
    {
        if (titles.Count == 0) return Array.Empty<BreadcrumbSegment>();

        // The common case: everything fits untrimmed.
        var total = measure(titles[0]);
        for (var i = 1; i < titles.Count; i++) total += separatorWidth + measure(titles[i]);
        if (total <= availableWidth) return BuildFull(titles);

        // Overflow: keep the tail (k+1 … end) at full width and give segment k whatever remains — first as a
        // whole name behind the elision marker, then as a leading-trimmed suffix; drop it (k++) when even a
        // meaningful stub won't fit.
        for (var k = 0; k < titles.Count; k++)
        {
            double tail = 0;
            for (var i = k + 1; i < titles.Count; i++) tail += separatorWidth + measure(titles[i]);
            var remaining = availableWidth - tail;
            if (remaining <= 0) continue;

            // Whole name behind the marker ("…Terrain Ideas") — unreachable for k == 0 (the all-fit check
            // above already failed), so a rendered marker always means something really is hidden.
            if (k > 0 && measure(Ellipsis + titles[k]) <= remaining)
                return Build(titles, k, Ellipsis + titles[k]);

            var trimmed = TrimLeadingToWidth(titles[k], remaining, measure);
            var minChars = k == titles.Count - 1 ? 1 : MinTrimmedChars;
            if (trimmed.Length - Ellipsis.Length >= minChars)
                return Build(titles, k, trimmed);
        }

        // Degenerate (a very narrow window): best-effort trim of the current page's title alone — possibly
        // just the ellipsis, but never nothing.
        return new[] { new BreadcrumbSegment(titles.Count - 1, TrimLeadingToWidth(titles[^1], availableWidth, measure)) };
    }

    // The longest "…"-prefixed suffix of text that fits width (binary search — measure is monotonic in length).
    private static string TrimLeadingToWidth(string text, double width, Func<string, double> measure)
    {
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (measure(Ellipsis + text[^mid..]) <= width) lo = mid;
            else hi = mid - 1;
        }
        return Ellipsis + text[^lo..];
    }

    private static BreadcrumbSegment[] BuildFull(IReadOnlyList<string> titles)
    {
        var result = new BreadcrumbSegment[titles.Count];
        for (var i = 0; i < titles.Count; i++) result[i] = new BreadcrumbSegment(i, titles[i]);
        return result;
    }

    // Segments k…end, with k's text replaced by its trimmed/marked form.
    private static BreadcrumbSegment[] Build(IReadOnlyList<string> titles, int k, string firstText)
    {
        var result = new BreadcrumbSegment[titles.Count - k];
        result[0] = new BreadcrumbSegment(k, firstText);
        for (var i = k + 1; i < titles.Count; i++) result[i - k] = new BreadcrumbSegment(i, titles[i]);
        return result;
    }
}
