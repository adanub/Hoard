using System;
using System.Collections.Generic;

namespace Hoard.Desktop.Controls;

/// <summary>A laid-out tile's rectangle (plain doubles so the packing math is Avalonia-free and testable).</summary>
public readonly record struct MasonryTile(double X, double Y, double Width, double Height);

/// <summary>
/// The pure masonry layout algorithm, separated from the Avalonia <see cref="MasonryLayout"/> shell so
/// it can be unit-tested. Packs items into fixed-width columns (each item's height from its aspect
/// ratio) using greedy shortest-column placement, and answers "which items intersect a vertical
/// viewport" in <c>O(columns·log n + visible)</c> via a per-column binary search — each column is
/// monotonically increasing in Y, so the visible slice per column is contiguous.
/// </summary>
public sealed class MasonryPacker
{
    private readonly MasonryTile[] _tiles;
    private readonly List<int>[] _columns; // per column: item indices in increasing-Y order
    private readonly int _expandedIndex;   // -1 if no item is expanded into a full-width band

    public int Count => _tiles.Length;
    public double TotalHeight { get; }

    public MasonryTile this[int index] => _tiles[index];

    // Detail-band sizing (kept here with the rest of the packing math so it stays Avalonia-free + unit-tested).
    // Wide: image on the left + a fixed-width info rail on the right. Narrow: a full-width image (capped) stacked
    // over a fixed info area that scrolls. The height is derived from the item's real (unclamped) aspect, capped,
    // so the packer stays deterministic.
    private const double StackBreakpoint = 640; // below this the band stacks image-over-info
    private const double MinBandHeight = 280, MaxBandHeight = 760, StackInfoHeight = 260;

    /// <summary>The full-width detail band's height for an item of the given (real, unclamped) <paramref name="aspect"/>
    /// at the given content <paramref name="width"/>, leaving room for a <paramref name="railWidth"/>-wide info rail
    /// (plus <paramref name="spacing"/>) when wide. Fed to the constructor as <c>expandedHeight</c>.</summary>
    public static double BandHeight(double width, double aspect, double spacing, double railWidth)
    {
        aspect = aspect > 0 ? aspect : 1.0;
        if (width >= StackBreakpoint)
        {
            var imageArea = Math.Max(160, width - railWidth - spacing); // image sits left of the info rail
            return Math.Clamp(imageArea * aspect, MinBandHeight, MaxBandHeight);
        }
        return Math.Min(width * aspect, MaxBandHeight) + StackInfoHeight;
    }

    /// <param name="aspectRatios">Height ÷ width for each item (clamped to [minAspect, maxAspect]).</param>
    /// <param name="expandedIndex">An item to lay out as a <b>full-width band</b> (the inline image-detail
    /// expansion) instead of a column tile, or -1 for none. The band drops below every column, spans the full
    /// width at <paramref name="expandedHeight"/>, then the columns resume beneath it — so items pack above it and
    /// below it, never beside it.</param>
    /// <param name="expandedHeight">The band's height (ignored unless &gt; 0 and the index is in range).</param>
    public MasonryPacker(
        IReadOnlyList<double> aspectRatios, double width,
        double targetColumnWidth, double spacing, double minAspect, double maxAspect,
        int expandedIndex = -1, double expandedHeight = 0)
    {
        _expandedIndex = expandedHeight > 0 && expandedIndex >= 0 && expandedIndex < aspectRatios.Count
            ? expandedIndex : -1;

        var columnCount = Math.Max(1, (int)Math.Floor((width + spacing) / (targetColumnWidth + spacing)));
        var columnWidth = (width - spacing * (columnCount - 1)) / columnCount;

        _columns = new List<int>[columnCount];
        for (var c = 0; c < columnCount; c++) _columns[c] = new List<int>();

        var columnBottoms = new double[columnCount];
        _tiles = new MasonryTile[aspectRatios.Count];

        for (var i = 0; i < aspectRatios.Count; i++)
        {
            if (i == _expandedIndex)
            {
                // Full-width band: start below the tallest column (so nothing overlaps), span the whole width,
                // then reset every column to its bottom so subsequent items pack beneath it. The band is kept out
                // of the column lists; GetVisible checks it directly.
                var baseline = 0.0;
                foreach (var b in columnBottoms) baseline = Math.Max(baseline, b);
                _tiles[i] = new MasonryTile(0, baseline, width, expandedHeight);
                var resume = baseline + expandedHeight + spacing;
                for (var c = 0; c < columnCount; c++) columnBottoms[c] = resume;
                continue;
            }

            var aspect = Math.Clamp(aspectRatios[i] > 0 ? aspectRatios[i] : 1.0, minAspect, maxAspect);
            var height = columnWidth * aspect;

            // Greedy: place into the currently shortest column.
            var col = 0;
            for (var c = 1; c < columnCount; c++)
                if (columnBottoms[c] < columnBottoms[col]) col = c;

            var x = col * (columnWidth + spacing);
            var y = columnBottoms[col];
            _tiles[i] = new MasonryTile(x, y, columnWidth, height);
            _columns[col].Add(i);
            columnBottoms[col] = y + height + spacing;
        }

        var tallest = 0.0;
        foreach (var bottom in columnBottoms) tallest = Math.Max(tallest, bottom);
        TotalHeight = Math.Max(0, tallest - spacing); // drop the trailing inter-item spacing
    }

    /// <summary>Append the indices of every tile intersecting the vertical range [top, bottom] to <paramref name="result"/>.</summary>
    public void GetVisible(double top, double bottom, List<int> result)
    {
        result.Clear();

        // The full-width band isn't in any column (it spans them all), so test it directly.
        if (_expandedIndex >= 0)
        {
            var t = _tiles[_expandedIndex];
            if (t.Y + t.Height >= top && t.Y <= bottom) result.Add(_expandedIndex);
        }

        foreach (var column in _columns)
        {
            // First index whose tile.Bottom >= top (lower bound — Bottom is increasing down a column).
            int lo = 0, hi = column.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                var tile = _tiles[column[mid]];
                if (tile.Y + tile.Height < top) lo = mid + 1;
                else hi = mid;
            }

            for (var k = lo; k < column.Count; k++)
            {
                var index = column[k];
                if (_tiles[index].Y > bottom) break; // Top past the viewport: nothing below it can intersect
                result.Add(index);
            }
        }
    }
}
