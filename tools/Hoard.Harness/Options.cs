using Avalonia;

namespace Hoard.Harness;

/// <summary>Command line for the render harness. Hand-rolled: a dev tool with six switches doesn't need a
/// parsing dependency, and this keeps the harness free of anything the app doesn't already carry.</summary>
internal sealed record Options
{
    public string Page { get; init; } = "board";
    public IReadOnlyList<Size> Sizes { get; init; } = [new Size(1100, 700)];
    public string OutDir { get; init; } = "artifacts/harness";
    public int Items { get; init; } = 40;
    public int Folders { get; init; } = 3;
    public string? Scroll { get; init; }
    public int SettleFrames { get; init; } = 12;
    public bool ShowHelp { get; init; }

    public const string Usage = """
        Usage: dotnet run --project tools/Hoard.Harness -- [options]

          --page <name>      page to render (default: board)
          --size <WxH>       client size to render at; repeatable, rendered in order (default: 1100x700)
          --out <dir>        output directory for the .png/.txt pairs (default: artifacts/harness)
          --items <n>        fixture images in the grid (default: 40)
          --folders <n>      fixture child-folder cards above the grid (default: 3)
          --scroll <where>   top | bottom | <pixels> — scroll the grid before capturing
          --settle <n>       layout/render pump rounds per size (default: 12)
          -h, --help

        Repeating --size reuses ONE window, so later sizes exercise a re-layout rather than a fresh build.
        """;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        var sizes = new List<Size>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    return options with { ShowHelp = true };
                case "--page":
                    options = options with { Page = Next(args, ref i) };
                    break;
                case "--size":
                    sizes.Add(ParseSize(Next(args, ref i)));
                    break;
                case "--out":
                    options = options with { OutDir = Next(args, ref i) };
                    break;
                case "--items":
                    options = options with { Items = ParseCount(Next(args, ref i), "--items") };
                    break;
                case "--folders":
                    options = options with { Folders = ParseCount(Next(args, ref i), "--folders") };
                    break;
                case "--scroll":
                    options = options with { Scroll = Next(args, ref i) };
                    break;
                case "--settle":
                    options = options with { SettleFrames = ParseCount(Next(args, ref i), "--settle") };
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        return sizes.Count > 0 ? options with { Sizes = sizes } : options;
    }

    private static string Next(string[] args, ref int i)
    {
        if (++i >= args.Length) throw new ArgumentException($"'{args[i - 1]}' needs a value");
        return args[i];
    }

    private static Size ParseSize(string text)
    {
        var parts = text.Split(['x', 'X', '×'], 2);
        if (parts.Length != 2
            || !double.TryParse(parts[0], out var w) || !double.TryParse(parts[1], out var h)
            || w <= 0 || h <= 0)
            throw new ArgumentException($"'{text}' is not a size — expected WxH, e.g. 1512x945");
        return new Size(w, h);
    }

    private static int ParseCount(string text, string option)
        => int.TryParse(text, out var n) && n >= 0 ? n : throw new ArgumentException($"'{option}' needs a number ≥ 0");
}
