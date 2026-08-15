using System;
using System.Collections.Generic;
using Hoard.Desktop.ViewModels;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// The closing toast of a "sync all" run — the only report a multi-minute run gives, so what it says when
/// things go wrong is a product decision, not formatting. These pin the case that went unnoticed in the
/// wild: a cookie-less run in which every board 404s must not describe itself as "already up to date".
/// </summary>
public class SyncAllSummaryTests
{
    private static readonly IReadOnlyList<(string Board, string Error)> NoFailures =
        Array.Empty<(string, string)>();

    // The connector's real message shape: an actionable sentence, then the raw gallery-dl tail.
    private const string PrivateBoardError =
        "Pinterest returned \"board not found\". If the board is private, select the browser you're logged " +
        "into Pinterest with in the Cookies dropdown (Firefox-based browsers like Zen are supported). " +
        "[pinterest][error] NotFoundError: Requested board could not be found";

    [Fact]
    public void A_clean_run_that_found_nothing_is_up_to_date()
        => Assert.Equal("Synced 3 board(s) — already up to date.",
            LibraryViewModel.SyncAllSummary(done: 3, total: 3, newTotal: 0, NoFailures));

    [Fact]
    public void A_clean_run_reports_what_it_pulled_in()
        => Assert.Equal("Synced 3 board(s) — 12 new image(s).",
            LibraryViewModel.SyncAllSummary(done: 3, total: 3, newTotal: 12, NoFailures));

    // The regression: 16 boards, no cookies, every one 404s. The old wording branched on newTotal alone and
    // said "Synced 0 board(s) — already up to date", which reads as success.
    [Fact]
    public void Every_board_failing_leads_with_the_reason_not_with_up_to_date()
    {
        var failures = Fail(16);
        var summary = LibraryViewModel.SyncAllSummary(done: 0, total: 16, newTotal: 0, failures);

        Assert.DoesNotContain("up to date", summary);
        Assert.Contains("no board could be fetched", summary);
        Assert.Contains("Cookies dropdown", summary); // the actionable half of the connector's message
    }

    [Fact]
    public void A_total_failure_drops_the_gallery_dl_tail()
    {
        var summary = LibraryViewModel.SyncAllSummary(done: 0, total: 1, newTotal: 0, Fail(1));

        Assert.DoesNotContain("[pinterest]", summary);
        Assert.DoesNotContain("NotFoundError", summary);
    }

    // A tail-less message (some other failure) must survive intact rather than being cut to nothing.
    [Fact]
    public void A_reason_with_no_tail_is_kept_whole()
    {
        var summary = LibraryViewModel.SyncAllSummary(
            done: 0, total: 1, newTotal: 0, new[] { ("Board", "The disk is full.") });

        Assert.EndsWith("The disk is full.", summary);
    }

    [Fact]
    public void A_very_long_reason_is_truncated()
    {
        var summary = LibraryViewModel.SyncAllSummary(
            done: 0, total: 1, newTotal: 0, new[] { ("Board", new string('x', 500)) });

        Assert.True(summary.Length < 320, $"toast was {summary.Length} chars");
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void A_partial_failure_says_how_many_of_how_many_worked()
    {
        var summary = LibraryViewModel.SyncAllSummary(done: 5, total: 7, newTotal: 4, Fail(2));

        Assert.Contains("Synced 5 of 7 board(s)", summary);
        Assert.Contains("4 new image(s)", summary);
        Assert.Contains("2 failed: Board 1, Board 2", summary);
        // The toast carries SyncAllDetails, so the reasons are one ⋯ click away — don't send the user
        // hunting for a log file instead.
        Assert.DoesNotContain("see the log", summary);
    }

    // ── The ⋯ detail ──────────────────────────────────────────────────────────

    [Fact]
    public void A_clean_run_has_no_details()
        => Assert.Null(LibraryViewModel.SyncAllDetails(NoFailures));

    [Fact]
    public void The_detail_names_every_failure_not_just_the_first_three()
    {
        var detail = LibraryViewModel.SyncAllDetails(Fail(5))!;

        Assert.Contains("5 boards failed.", detail);
        foreach (var name in new[] { "Board 1", "Board 2", "Board 3", "Board 4", "Board 5" })
            Assert.Contains(name, detail);
        // Unlike the card, the detail keeps the raw connector output — that's what it's for.
        Assert.Contains("NotFoundError", detail);
    }

    [Fact]
    public void One_failure_reads_as_one()
        => Assert.StartsWith("1 board failed.", LibraryViewModel.SyncAllDetails(Fail(1))!);

    // A partial failure that pulled in nothing is still a partial failure — not "already up to date".
    [Fact]
    public void A_partial_failure_with_no_new_images_still_reports_the_failures()
    {
        var summary = LibraryViewModel.SyncAllSummary(done: 5, total: 7, newTotal: 0, Fail(2));

        Assert.DoesNotContain("already up to date", summary);
        Assert.Contains("no new images", summary);
        Assert.Contains("2 failed", summary);
    }

    [Fact]
    public void Only_the_first_three_casualties_are_named()
    {
        var summary = LibraryViewModel.SyncAllSummary(done: 1, total: 6, newTotal: 0, Fail(5));

        Assert.Contains("5 failed: Board 1, Board 2, Board 3…", summary);
        Assert.DoesNotContain("Board 4", summary);
    }

    private static (string Board, string Error)[] Fail(int count)
    {
        var failures = new (string, string)[count];
        for (var i = 0; i < count; i++) failures[i] = ($"Board {i + 1}", PrivateBoardError);
        return failures;
    }
}
