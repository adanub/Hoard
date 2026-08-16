using Hoard.Core.Domain;
using Hoard.Core.Library;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.Services;
using Hoard.Desktop.ViewModels;
using SkiaSharp;

namespace Hoard.Harness;

/// <summary>
/// Synthetic content for the harness: real image files on disk plus the view models that point at them.
///
/// The images are real because the grid is only honest with real ones — <c>AssetTileViewModel</c> probes the
/// file's existence and decodes it (falling back to a direct decode when there's no <c>ThumbnailCache</c>), so
/// fake paths would render a wall of "file missing" tiles and tell us nothing about the layout. They're
/// generated once into the temp folder and reused, keyed by their dimensions, so repeat runs are instant.
///
/// The view models are built through the REAL public constructors with null services. Every async load in
/// <c>BoardViewModel</c> guards on <c>_library is null</c> (that's what makes the XAML previewer work), so a
/// service-less board is a supported state, not a hack — it just never queries anything, which is exactly what
/// a fixture wants.
/// </summary>
internal static class Fixtures
{
    private static readonly string ImageRoot =
        Path.Combine(Path.GetTempPath(), "hoard-harness-fixtures");

    // Height ÷ width, cycled across the grid. Deliberately spans the masonry's clamp range (0.5–2.6) and
    // overshoots it at both ends, so a run also exercises the clamping.
    private static readonly double[] Aspects = [0.45, 0.75, 1.0, 1.33, 0.62, 1.8, 1.1, 2.9, 0.9, 1.5];

    /// <summary>A board with <paramref name="items"/> images and <paramref name="folders"/> child-folder cards.</summary>
    public static BoardViewModel Board(int items, int folders)
    {
        // The full constructor, not the design-time one: that one hardcodes the virtual "All images" target
        // (a null collection id), which suppresses the folder row and the ＋ actions. A real board id gives
        // the layout the same shape the app has.
        var board = new BoardViewModel(
            null!, null!, null!, null, new ToastService(), new ImportStatus(), null,
            new BoardTarget(1, "Fixture board"), new NavigationService(), _ => { });

        for (var i = 0; i < items; i++)
            board.Assets.Add(new AssetTileViewModel(Asset(i)));

        if (folders > 0)
        {
            board.FolderTiles.Add(NewFolderTile.Instance);
            for (var i = 0; i < folders; i++)
                board.FolderTiles.Add(new BoardCardRef(i + 2, $"Folder {i + 1}", _ => { }, _ => { })
                {
                    MetaText = $"{12 + i * 7} images · {3 + i} MB",
                });
        }

        return board;
    }

    private static AssetView Asset(int index)
    {
        var aspect = Aspects[index % Aspects.Length];
        const int width = 500;
        var height = (int)Math.Round(width * aspect);
        var path = EnsureImage(width, height, index);

        return new AssetView(
            Id: index + 1,
            AbsolutePath: path,
            Kind: MediaKind.Image,
            Title: $"Fixture item {index + 1}",
            Description: "A generated placeholder used by the render harness.",
            SourceUrl: "https://example.invalid/pin/0",
            Width: width,
            Height: height,
            Sha256: $"fixture{index:D6}");
    }

    // One file per (size, hue) combination, reused across runs. Drawn as a flat colour with its index and
    // dimensions on it, so a tile is identifiable in a screenshot and a mis-sized one is obvious at a glance.
    private static string EnsureImage(int width, int height, int index)
    {
        Directory.CreateDirectory(ImageRoot);
        var hue = index * 37 % 360;
        var path = Path.Combine(ImageRoot, $"fixture-{width}x{height}-{hue}.png");
        if (File.Exists(path)) return path;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.FromHsl(hue, 55, 62));

        using var ink = new SKPaint { Color = new SKColor(0x1a, 0x1a, 0x1a), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, Math.Max(18, width / 12f));
        canvas.DrawText($"#{index + 1}", 18, font.Size + 12, SKTextAlign.Left, font, ink);
        using var small = new SKFont(SKTypeface.Default, Math.Max(12, width / 24f));
        canvas.DrawText($"{width}×{height}", 18, font.Size + small.Size + 20, SKTextAlign.Left, small, ink);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var file = File.Create(path);
        data.SaveTo(file);
        return path;
    }
}
