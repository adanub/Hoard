using Hoard.Ingest.GalleryDl;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// A cookie database Hoard couldn't read is the cause of the "board not found" that follows it, so the run
/// has to tell the two apart from gallery-dl's stderr alone: blaming the board sends the user to change a
/// setting that was already correct. Pinned against the real line a locked Chromium browser produces.
/// </summary>
public class CookieFailureDetectionTests
{
    [Theory]
    // The line from the reproduction: Opera open, so Windows refuses every reader and Python reports errno 13.
    [InlineData(@"[pinterest][warning] cookies: [Errno 13] Permission denied: 'C:\Users\a\AppData\Roaming\Opera Software\Opera Stable\Default\Network\Cookies'")]
    [InlineData("[pinterest][warning] cookies: [Errno 13] Permission denied: '/home/a/.config/opera/Cookies'")]
    [InlineData("[twitter][warning] cookies: The process cannot access the file because it is being used by another process")]
    [InlineData("[pinterest][warning] cookies: database is locked")]
    public void Reads_a_locked_cookie_database_as_a_cookie_failure(string line)
        => Assert.True(PinterestConnector.IsCookieFailure(line));

    [Theory]
    // Cosmetic: gallery-dl's CLI passes an empty profile, so every Opera run warns. Not an access failure.
    [InlineData("[cookies][warning] opera does not support profiles")]
    [InlineData("[cookies][info] Extracted 2413 cookies from Firefox")]
    [InlineData("[pinterest][error] NotFoundError: Requested board could not be found")]
    [InlineData("[pinterest][warning] 751186412840670854: Unsupported story block 'story_pin_heading_block'")]
    // "Permission denied" without the cookies prefix is some other file's problem.
    [InlineData("[downloader][error] [Errno 13] Permission denied: '/out/image.jpg'")]
    public void Leaves_every_other_line_alone(string line)
        => Assert.False(PinterestConnector.IsCookieFailure(line));
}
