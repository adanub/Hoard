using Hoard.Desktop.Services;
using Hoard.Desktop.ViewModels;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// The <c>BusyBar</c> contract, pinned where it can actually be tested.
///
/// <para>
/// <b>Why this exists.</b> <c>BusyBar</c> only runs its infinite indeterminate animation while its OWN
/// <c>IsVisible</c> is true (<c>Sync() =&gt; IsIndeterminate = IsVisible &amp;&amp; IsAttachedToVisualTree()</c>) —
/// it deliberately cannot observe an ancestor's visibility, because effective visibility raises no change
/// notification. The shell's Settings and update <c>SheetHost</c>s stay ATTACHED when closed (they hide via
/// their own <c>IsVisible</c>), so a bar inside one whose visibility is bound to a plain "is something
/// running" flag keeps animating, invisibly, for the whole operation — re-registering composition visuals
/// every frame, which is the exact leak <c>BusyBar</c> was written to eliminate.
/// </para>
/// <para>
/// Both bars therefore gate on <i>busy AND this sheet is open</i>. There is no test seam at the view layer
/// for that, so the invariant lives on the view models — which is what makes it assertable here. Keep it
/// that way: moving either gate back into XAML as a bare <c>IsBusy</c> binding silently reintroduces the leak
/// and nothing would fail.
/// </para>
/// </summary>
public class BusyGateTests
{
    // No update service: nothing is ever checked, downloaded or applied — these tests drive the flags directly.
    private static UpdateViewModel NewUpdates() => new(null, new UiSettings());

    [Fact]
    public void The_prompts_busy_bar_stays_off_while_the_prompt_is_closed()
    {
        var updates = NewUpdates();

        // An install started from the Settings sheet: busy, but this sheet is shut.
        updates.IsBusy = true;
        updates.IsOpen = false;

        Assert.False(updates.IsPromptBusy);
    }

    [Fact]
    public void The_prompts_busy_bar_runs_only_when_busy_AND_open()
    {
        var updates = NewUpdates();
        Assert.False(updates.IsPromptBusy); // idle, closed

        updates.IsOpen = true;
        Assert.False(updates.IsPromptBusy); // open but idle — nothing to animate

        updates.IsBusy = true;
        Assert.True(updates.IsPromptBusy);

        updates.IsOpen = false;
        Assert.False(updates.IsPromptBusy); // dismissed mid-operation
    }

    [Fact]
    public void The_settings_busy_bar_stays_off_while_the_settings_sheet_is_closed()
    {
        var updates = NewUpdates();
        var settings = new SettingsViewModel(null, null, null, updates);

        // The quiet auto-install download: busy for minutes with no sheet on screen at all.
        updates.IsBusy = true;

        Assert.False(settings.IsOpen);
        Assert.False(settings.ShowUpdateBusy);
    }

    [Fact]
    public void The_settings_busy_bar_runs_only_when_busy_AND_open()
    {
        var updates = NewUpdates();
        var settings = new SettingsViewModel(null, null, null, updates);

        settings.IsOpen = true;
        Assert.False(settings.ShowUpdateBusy); // open but idle

        updates.IsBusy = true;
        Assert.True(settings.ShowUpdateBusy);

        settings.IsOpen = false;
        Assert.False(settings.ShowUpdateBusy);
    }

    [Fact]
    public void The_settings_busy_gate_re_evaluates_when_the_update_starts_and_stops()
    {
        // The gate spans two view models, so it needs a subscription to be live — without it the bar would
        // simply never appear (or never disappear) while Settings sits open.
        var updates = NewUpdates();
        var settings = new SettingsViewModel(null, null, null, updates) { IsOpen = true };

        var raised = 0;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ShowUpdateBusy)) raised++;
        };

        updates.IsBusy = true;
        Assert.Equal(1, raised);

        updates.IsBusy = false;
        Assert.Equal(2, raised);
    }
}
