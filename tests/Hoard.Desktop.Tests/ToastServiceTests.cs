using System.Linq;
using Hoard.Desktop.Services;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// Toast behaviour, pinned without a UI. The rule these protect: a message stays until it is dismissed. The
/// four-second auto-fade this replaced is what made a whole failed sync run invisible — it reported itself
/// once, briefly, at the end of a run the user had walked away from.
/// </summary>
public class ToastServiceTests
{
    [Fact]
    public void A_toast_stays_until_it_is_dismissed()
    {
        var toasts = new ToastService();
        toasts.Show("Saved.");

        Assert.Single(toasts.Toasts);

        toasts.Toasts[0].DismissCommand.Execute(null);
        Assert.Empty(toasts.Toasts);
    }

    [Fact]
    public void Toasts_stack_newest_last()
    {
        var toasts = new ToastService();
        toasts.Show("First.");
        toasts.Show("Second.");

        Assert.Equal(new[] { "First.", "Second." }, toasts.Toasts.Select(t => t.Message));
    }

    [Fact]
    public void Clear_all_empties_the_stack()
    {
        var toasts = new ToastService();
        toasts.Show("One.");
        toasts.Show("Two.", isError: true);

        toasts.ClearAllCommand.Execute(null);
        Assert.Empty(toasts.Toasts);
    }

    // "Clear all" is a second dismiss button; under a lone toast that already has its own ✕ it's just noise.
    [Fact]
    public void Clear_all_is_offered_only_for_a_pile()
    {
        var toasts = new ToastService();
        Assert.False(toasts.ShowClearAll);

        toasts.Show("One.");
        Assert.False(toasts.ShowClearAll);

        toasts.Show("Two.");
        Assert.True(toasts.ShowClearAll);
    }

    // ── The stack cap ─────────────────────────────────────────────────────────

    [Fact]
    public void The_stack_is_capped()
    {
        var toasts = new ToastService();
        for (var i = 0; i < ToastService.MaxVisible + 4; i++) toasts.Show($"Message {i}.");

        Assert.Equal(ToastService.MaxVisible, toasts.Toasts.Count);
    }

    // Errors are the messages worth keeping, so a flood of routine confirmations must not push one out.
    [Fact]
    public void The_cap_sheds_routine_toasts_before_errors()
    {
        var toasts = new ToastService();
        toasts.Show("The sync failed.", isError: true);
        for (var i = 0; i < ToastService.MaxVisible + 4; i++) toasts.Show($"Routine {i}.");

        Assert.Contains(toasts.Toasts, t => t.Message == "The sync failed.");
        Assert.Equal(ToastService.MaxVisible, toasts.Toasts.Count);
    }

    // Only when there's nothing else left to shed: a wall of errors still can't grow past the cap.
    [Fact]
    public void A_stack_of_only_errors_still_sheds_the_oldest()
    {
        var toasts = new ToastService();
        for (var i = 0; i < ToastService.MaxVisible + 2; i++) toasts.Show($"Error {i}.", isError: true);

        Assert.Equal(ToastService.MaxVisible, toasts.Toasts.Count);
        Assert.DoesNotContain(toasts.Toasts, t => t.Message == "Error 0.");
    }

    // ── Details ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_toast_without_details_offers_no_expand_button()
    {
        var toasts = new ToastService();
        toasts.Show("Saved.");

        Assert.False(toasts.Toasts[0].HasDetails);
    }

    [Fact]
    public void Whitespace_is_not_details()
    {
        var toasts = new ToastService();
        toasts.Show("Saved.", details: "   ");

        Assert.False(toasts.Toasts[0].HasDetails);
    }

    [Fact]
    public void Expanding_a_toast_opens_its_details()
    {
        var toasts = new ToastService();
        toasts.Show("Sync failed.", isError: true, details: "Board A\n  404");
        var toast = toasts.Toasts[0];

        Assert.True(toast.HasDetails);
        Assert.False(toasts.IsDetailsOpen);

        toast.ShowDetailsCommand.Execute(null);
        Assert.True(toasts.IsDetailsOpen);
        Assert.Same(toast, toasts.DetailsToast);

        toasts.CloseDetailsCommand.Execute(null);
        Assert.False(toasts.IsDetailsOpen);
    }

    // A sheet showing a toast that's no longer on screen is a ghost — closing the toast closes its details.
    [Fact]
    public void Dismissing_a_toast_closes_its_open_details()
    {
        var toasts = new ToastService();
        toasts.Show("Sync failed.", isError: true, details: "Board A\n  404");
        var toast = toasts.Toasts[0];
        toast.ShowDetailsCommand.Execute(null);

        toast.DismissCommand.Execute(null);
        Assert.False(toasts.IsDetailsOpen);
    }

    [Fact]
    public void Another_toasts_details_survive_a_dismissal()
    {
        var toasts = new ToastService();
        toasts.Show("Sync failed.", isError: true, details: "Board A\n  404");
        toasts.Show("Saved.");
        var failure = toasts.Toasts[0];
        failure.ShowDetailsCommand.Execute(null);

        toasts.Toasts[1].DismissCommand.Execute(null); // dismiss the OTHER one
        Assert.Same(failure, toasts.DetailsToast);
    }

    [Fact]
    public void Clear_all_closes_an_open_details_sheet()
    {
        var toasts = new ToastService();
        toasts.Show("Sync failed.", isError: true, details: "Board A\n  404");
        toasts.Toasts[0].ShowDetailsCommand.Execute(null);

        toasts.ClearAllCommand.Execute(null);
        Assert.False(toasts.IsDetailsOpen);
    }

    // The cap must not leave the sheet showing a toast it just shed.
    [Fact]
    public void The_cap_closes_the_details_of_a_toast_it_sheds()
    {
        var toasts = new ToastService();
        toasts.Show("Routine 0.", details: "the first one");
        toasts.Toasts[0].ShowDetailsCommand.Execute(null);
        for (var i = 1; i < ToastService.MaxVisible + 1; i++) toasts.Show($"Routine {i}.");

        Assert.DoesNotContain(toasts.Toasts, t => t.Message == "Routine 0.");
        Assert.False(toasts.IsDetailsOpen);
    }
}
