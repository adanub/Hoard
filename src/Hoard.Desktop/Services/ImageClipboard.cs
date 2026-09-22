using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace Hoard.Desktop.Services;

/// <summary>
/// Puts an archived image on the system clipboard as a picture, so it can be pasted into anything else.
/// <para>
/// Both platform clipboards render <b>lazily</b>: Windows keeps our <c>IDataObject</c> and calls back for the
/// bytes when something pastes (Avalonia's OLE wrapper publishes <c>DataFormat.Bitmap</c> as
/// CF_BITMAP/CF_DIB/CF_DIBV5/PNG at that point), and macOS promises the data to <c>NSPasteboard</c> the same
/// way. So the bitmap must stay <b>undisposed</b> for as long as the clipboard holds it — freeing it right after
/// the copy, which is this app's rule everywhere else, would leave a paste with nothing to read.
/// </para>
/// <para>
/// Staying <i>alive</i> is not the problem: the clipboard's own data object references the bitmap, and Avalonia
/// never disposes it for us (<c>DataTransfer.Dispose</c> is an empty body and <c>IDataTransferItem</c> isn't
/// disposable at all). What the retained field buys is a <b>deterministic free</b>: without it, each replaced
/// surface would wait on the <c>Ref&lt;T&gt;</c> finalizer, which is exactly the lagging-finalization pattern the
/// thumbnail and GIF caches exist to avoid. So the last copied bitmap is held until the next copy replaces it
/// — one surface for the one system clipboard, no growth however many times the user copies.
/// </para>
/// <para>
/// Two consequences to accept. The clipboard holds the image only while Hoard is running: rendering all four
/// Windows image formats up front (<c>IClipboard.FlushAsync</c>) would outlive the app, at the cost of encoding
/// a full-resolution image four times on every copy. And freeing the previous surface assumes nobody is still
/// reading it — true of the clipboard itself, which released that data object when this copy replaced it, but
/// an app that grabbed the old <c>IDataObject</c> earlier and reads it later gets a failed paste of content
/// it had already been superseded on.
/// </para>
/// <para>UI thread only (the Windows clipboard verifies that, and the retained field is unsynchronised).</para>
/// </summary>
internal static class ImageClipboard
{
    private static Bitmap? _retained;

    /// <summary>Decode <paramref name="path"/> at full resolution and place it on the clipboard. Throws if the
    /// file can't be read/decoded or the clipboard refuses the data; the caller reports that.</summary>
    public static async Task CopyAsync(IClipboard clipboard, string path)
    {
        var bitmap = await Task.Run(() => new Bitmap(path));

        // DataTransfer is a plain managed holder — its Dispose is an empty body, so there's nothing to release
        // on either path here; the bitmap is the only thing with a lifetime worth tracking.
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
        try
        {
            await clipboard.SetDataAsync(transfer);
        }
        catch
        {
            bitmap.Dispose(); // never made it onto the clipboard, so nothing can be waiting to read it
            throw;
        }

        // Only now has the clipboard let go of the previous copy: SetDataAsync replaced what it was holding.
        var previous = _retained;
        _retained = bitmap;
        previous?.Dispose();
    }
}
