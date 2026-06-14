namespace Hoard.Desktop;

/// <summary>Human-readable byte sizes (e.g. "12.3 MB").</summary>
public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {Units[unit]}";
    }
}
