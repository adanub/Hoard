using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Hoard.Core.Storage;

namespace Hoard.Desktop.Services;

/// <summary>
/// Sends files/folders to the Windows recycle bin via the shell's <c>SHFileOperation</c> (with
/// <c>FOF_ALLOWUNDO</c>), so a deletion stays recoverable. Pure P/Invoke — no Windows Desktop framework
/// reference — but functions on Windows only; a future macOS/Linux head registers its own
/// <see cref="IFileRecycler"/>. (P/Invoke lives here in the desktop head, never in platform-neutral Core.)
/// </summary>
public sealed class WindowsFileRecycler : IFileRecycler
{
    public void RecycleDirectory(string path) => Run(Path.GetFullPath(path) + '\0' + '\0', path);
    public void RecycleFile(string path) => Run(Path.GetFullPath(path) + '\0' + '\0', path);

    public void RecycleFiles(IReadOnlyCollection<string> paths)
    {
        // SHFileOperation takes a NUL-separated, double-NUL-terminated list — so the whole set recycles in one
        // shell call. Skip files that are already gone (a missing path would abort the batch).
        var existing = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) existing.Add(full);
        }
        if (existing.Count == 0) return;
        Run(string.Join('\0', existing) + '\0' + '\0', $"{existing.Count} file(s)");
    }

    // The source list must be double-null-terminated; the marshaller adds the string's own terminator, so the
    // caller appends one extra NUL.
    private static void Run(string pFrom, string label)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = pFrom,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        var result = SHFileOperation(ref op);
        if (result != 0)
            throw new IOException($"Could not recycle '{label}' (SHFileOperation returned 0x{result:X}).");
        if (op.fAnyOperationsAborted)
            throw new IOException($"Recycling '{label}' was aborted.");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
