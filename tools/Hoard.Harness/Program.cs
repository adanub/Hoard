using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hoard.Desktop.Infrastructure;
using Hoard.Desktop.ViewModels;
using Hoard.Desktop.Views;

namespace Hoard.Harness;

/// <summary>
/// Renders a Hoard page headlessly at one or more client sizes, writing a PNG and a geometry report per size.
///
/// Why this exists: the GUI can't be driven from the agent/CI side, but nearly every UI bug this project has hit
/// is a LAYOUT bug — a wrong extent, a cropped edge, a panel that doesn't reflow — and those are all decidable
/// from geometry. The harness renders with the real theme, the real views and the real Avalonia, so its numbers
/// are the app's numbers. What it deliberately does NOT cover is the platform windowing layer: native fullscreen,
/// DPI changes and compositor behaviour live in Avalonia's macOS/Windows backends, and a headless window has
/// none of them. For those, run the app with HOARD_LAYOUT_PROBE=1 (Infrastructure/LayoutProbe.cs).
///
/// Usage (from the repo root):
///   dotnet run --project tools/Hoard.Harness -- --page board --size 1100x700 --size 1512x945 --out artifacts
///   dotnet run --project tools/Hoard.Harness -- --page board --items 60 --folders 4 --scroll bottom
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return Complain(ex);
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        // Headless windowing (no display needed) but REAL Skia drawing, so CaptureRenderedFrame produces the
        // pixels the app would draw rather than a stub. UseHeadlessDrawing=false is the switch for that.
        AppBuilder.Configure<HarnessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        Directory.CreateDirectory(options.OutDir);

        Control view;
        IDisposable? disposable;
        try
        {
            // Building the page validates --page, so it gets the same usage message a bad switch does rather
            // than an unhandled throw.
            view = BuildPage(options, out disposable);
        }
        catch (ArgumentException ex)
        {
            return Complain(ex);
        }
        // ONE window reused across every size: the point of a multi-size run is to exercise a re-layout of a
        // live tree, which is what a resize actually is. Building a fresh window per size would only ever test
        // the first-layout path and would miss anything that goes wrong when an existing tree is re-measured.
        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = options.Sizes[0].Width,
            Height = options.Sizes[0].Height,
            Content = view,
        };
        window.Show();

        var failures = 0;
        foreach (var size in options.Sizes)
        {
            window.Width = size.Width;
            window.Height = size.Height;
            Settle(options.SettleFrames);

            if (options.Scroll is { } scroll) ApplyScroll(window, scroll);
            Settle(options.SettleFrames);

            var stem = Path.Combine(options.OutDir, $"{options.Page}-{size.Width:0}x{size.Height:0}");
            var report = LayoutProbe.Report(window, $"{options.Page} @ {size.Width:0}×{size.Height:0}");

            File.WriteAllText($"{stem}.txt", report + Environment.NewLine);
            Console.WriteLine(report);

            try
            {
                window.CaptureRenderedFrame()?.Save($"{stem}.png", PngBitmapEncoderOptions.Default);
                Console.WriteLine($"[harness] wrote {stem}.png");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"[harness] capture failed for {stem}.png: {ex.Message}");
            }

            Console.WriteLine();
        }

        disposable?.Dispose();
        return failures == 0 ? 0 : 1;
    }

    private static int Complain(Exception ex)
    {
        Console.Error.WriteLine($"hoard-harness: {ex.Message}");
        Console.Error.WriteLine(Options.Usage);
        return 2;
    }

    private static Control BuildPage(Options options, out IDisposable? disposable)
    {
        switch (options.Page)
        {
            case "board":
            {
                var vm = Fixtures.Board(options.Items, options.Folders);
                disposable = vm;
                return new BoardView { DataContext = vm };
            }
            default:
                throw new ArgumentException($"unknown page '{options.Page}' (known: board)");
        }
    }

    // Scroll the page's grid, so "is the last row reachable?" can actually be asked. `bottom` goes as far as the
    // ScrollViewer's own extent allows — which is precisely the number that was wrong when padding shrank it.
    private static void ApplyScroll(Window window, string scroll)
    {
        var sv = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "GridScroll");
        if (sv is null) return;

        sv.Offset = scroll switch
        {
            "bottom" => sv.Offset.WithY(Math.Max(0, sv.Extent.Height - sv.Viewport.Height)),
            "top" => sv.Offset.WithY(0),
            _ => sv.Offset.WithY(double.TryParse(scroll, out var y) ? y : 0),
        };
    }

    // A headless window still needs its layout + render passes pumped. Several rounds, because the board's
    // async thumbnail decodes land over a few dispatcher turns and each one can re-measure the grid.
    private static void Settle(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
