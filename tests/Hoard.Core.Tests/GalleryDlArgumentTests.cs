using Hoard.Core.Connectors;
using Hoard.Ingest.GalleryDl;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The command line is what decides whether a re-sync costs one page or the whole board, so the flags that
/// bound the crawl are pinned here — no subprocess involved.
/// </summary>
public class GalleryDlArgumentTests
{
    private static List<string> Build(ConnectorOptions options, params string[] urls)
        => PinterestConnector.BuildArguments("/tmp/out", urls.Length == 0 ? ["https://pin/board/"] : urls, options);

    private static string Value(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, $"expected {flag} with a value in: {string.Join(" ", args)}");
        return args[i + 1];
    }

    [Fact]
    public void An_early_stop_is_abort_not_terminate()
    {
        // --abort stops the CURRENT extractor and moves on to the next target; --terminate would take the
        // rest of the run down with it, so one exhausted board would cancel every section after it.
        var args = Build(new ConnectorOptions { StopAfterConsecutiveKnown = 100 });

        Assert.Equal("100", Value(args, "--abort"));
        Assert.DoesNotContain("--terminate", args);
    }

    [Fact]
    public void No_early_stop_by_default_so_a_first_import_walks_everything()
    {
        var args = Build(new ConnectorOptions());

        Assert.DoesNotContain("--abort", args);
        Assert.DoesNotContain("--option", args); // sub-collections followed by the connector itself
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonsense_stop_budget_is_ignored_rather_than_passed_through(int budget)
    {
        // "--abort 0" would abort on the first skipped item — a sync that stops before it starts.
        Assert.DoesNotContain("--abort", Build(new ConnectorOptions { StopAfterConsecutiveKnown = budget }));
    }

    [Fact]
    public void Excluding_sub_collections_turns_off_the_connectors_own_section_recursion()
    {
        var args = Build(new ConnectorOptions { IncludeSubCollections = false });

        Assert.Equal("extractor.pinterest.sections=false", Value(args, "--option"));
    }

    [Fact]
    public void Every_target_is_passed_to_one_run_in_order_after_the_flags()
    {
        var args = Build(
            new ConnectorOptions { StopAfterConsecutiveKnown = 50, IncludeSubCollections = false },
            "https://pin/board/", "https://pin/board/id:s1", "https://pin/board/id:s2");

        Assert.Equal(
            ["https://pin/board/", "https://pin/board/id:s1", "https://pin/board/id:s2"],
            args.TakeLast(3));
        // A flag landing after a URL would be read as another target.
        Assert.Equal(args.Count - 3, args.IndexOf("https://pin/board/"));
    }

    [Fact]
    public void The_skip_archive_and_sidecars_still_ride_along()
    {
        var args = Build(new ConnectorOptions { DownloadArchivePath = "/tmp/archive.db", MaxItems = 5 });

        Assert.Contains("--write-metadata", args);
        Assert.Equal("/tmp/archive.db", Value(args, "--download-archive"));
        Assert.Equal("1-5", Value(args, "--range"));
        Assert.Equal("/tmp/out", Value(args, "--directory"));
    }

    [Fact]
    public void Rate_limits_are_emitted_as_jittered_ranges()
    {
        var args = Build(new ConnectorOptions
        {
            RateLimit = new RateLimitOptions
            {
                RequestIntervalSeconds = 1.0,
                RequestIntervalJitterSeconds = 1.0,
                DownloadIntervalSeconds = 0.5,
                DownloadIntervalJitterSeconds = 0,
                MaxRate = "2M",
            },
        });

        Assert.Equal("1-2", Value(args, "--sleep-request"));
        Assert.Equal("0.5", Value(args, "--sleep"));
        Assert.Equal("2M", Value(args, "--limit-rate"));
    }
}
