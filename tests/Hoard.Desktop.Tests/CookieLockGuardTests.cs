using Hoard.Desktop.Services;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// Whether an import starts at all, so the quiet paths matter as much as the warning: a false alarm in front
/// of every import would be worse than the bug it guards.
/// </summary>
public class CookieLockGuardTests
{
    private static CookieLockGuard Guard(bool locked, Func<string, Task<bool>>? confirm)
        => new(_ => locked) { Confirm = confirm };

    [Fact]
    public async Task Starts_the_import_without_asking_when_the_database_is_readable()
    {
        var asked = false;
        var guard = Guard(locked: false, _ => { asked = true; return Task.FromResult(false); });

        Assert.True(await guard.AllowAsync("opera"));
        Assert.False(asked);
    }

    [Fact]
    public async Task Backs_out_when_the_user_would_rather_close_the_browser()
        => Assert.False(await Guard(locked: true, _ => Task.FromResult(false)).AllowAsync("opera"));

    [Fact]
    public async Task Goes_ahead_when_the_user_insists()
        => Assert.True(await Guard(locked: true, _ => Task.FromResult(true)).AllowAsync("opera"));

    [Fact]
    public async Task Names_the_browser_the_way_the_user_picked_it()
    {
        string? named = null;
        var guard = Guard(locked: true, b => { named = b; return Task.FromResult(true); });

        await guard.AllowAsync("opera gx");
        Assert.Equal("Opera GX", named);
    }

    [Fact]
    public async Task Proceeds_when_no_popup_is_wired_at_all()
    {
        // Headless callers (and every view model test) must not be blocked by a popup that can't be shown.
        Assert.True(await new CookieLockGuard(_ => true).AllowAsync("opera"));
    }
}
