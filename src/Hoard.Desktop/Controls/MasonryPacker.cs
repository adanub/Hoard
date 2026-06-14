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

    public int Count => _tiles.Length;
    public double TotalHeight { get; }

    public MasonryTile this[int index] => _tiles[index];

    /// <param name="aspectRatios">Height ÷ width for each item (clamped to [minAspect, maxAspect]).</param>
    public MasonryPacker(
        IReadOnlyList<double> aspectRatios, double width,
        double targetColumnWidth, double spacing, double minAspect, double maxAspect)
    {
        var columnCount = Math.Max(1, (int)Math.Floor((width + spacing) / (targetColumnWidth + spacing)));
        var columnWidth = (width - spacing * (columnCount - 1)) / columnCount;

        _columns = new List<int>[columnCount];
        for (var c = 0; c < columnCount; c++) _columns[c] = new List<int>();

        var columnBottoms = new double[columnCount];
        _tiles = new MasonryTile[aspectRatios.Count];

        for (var i = 0; i < aspectRatios.Count; i++)
        {
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
