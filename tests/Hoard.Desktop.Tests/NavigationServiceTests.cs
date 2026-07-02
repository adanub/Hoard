using System;
using System.Collections.Generic;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.ViewModels;
using Xunit;

namespace Hoard.Desktop.Tests;

public class NavigationServiceTests
{
    // A throwaway concrete page (ViewModelBase is abstract).
    private sealed class Page : ViewModelBase { }

    // A page that can absorb a Back/Forward while mid-transition (e.g. a board animating its band closed).
    private sealed class AbsorbingPage : ViewModelBase, IAbsorbsBack
    {
        public bool Absorb;
        public bool AbsorbBack() => Absorb;
    }

    // Records what the navigation showed at the moment it was disposed. The memory fix depends on Back disposing
    // the outgoing page BEFORE swapping Current (its view must still be attached so the ItemsRepeater teardown can
    // run one last layout pass); this pins the ordering so a future refactor can't "simplify" it away silently.
    private sealed class DisposablePage(NavigationService nav) : ViewModelBase, IDisposable
    {
        public int Disposals;
        public ViewModelBase? CurrentAtDispose;
        public void Dispose()
        {
            Disposals++;
            CurrentAtDispose = nav.Current;
        }
    }

    // ── Page steps ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_sets_the_root_and_cannot_go_back()
    {
        var nav = new NavigationService();
        var root = new Page();

        nav.Reset(root);

        Assert.Same(root, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Push_shows_the_new_page_and_enables_back()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var next = new Page();

        nav.Push(next);

        Assert.Same(next, nav.Current);
        Assert.True(nav.CanGoBack);
    }

    [Fact]
    public void Back_returns_to_the_previous_page()
    {
        var nav = new NavigationService();
        var root = new Page();
        nav.Reset(root);
        nav.Push(new Page());

        nav.Back();

        Assert.Same(root, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Back_at_the_root_is_a_no_op()
    {
        var nav = new NavigationService();
        var root = new Page();
        nav.Reset(root);

        nav.Back();

        Assert.Same(root, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Reset_clears_a_deep_stack()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        nav.Push(new Page());
        nav.Push(new Page());

        var fresh = new Page();
        nav.Reset(fresh);

        Assert.Same(fresh, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void CanGoBack_notifies_when_navigation_changes()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());

        var raised = 0;
        nav.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NavigationService.CanGoBack)) raised++; };

        nav.Push(new Page()); // false → true
        nav.Back();           // true → false

        Assert.Equal(2, raised);
    }

    // ── In-page state steps (the image-detail band / zoom) ─────────────────────────

    [Fact]
    public void PushState_applies_immediately_then_Back_reverts_it()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var log = new List<string>();

        nav.PushState(_ => { log.Add("apply"); return true; }, _ => log.Add("revert"));
        Assert.Equal(new[] { "apply" }, log);
        Assert.True(nav.CanGoBack);

        nav.Back();
        Assert.Equal(new[] { "apply", "revert" }, log);
        Assert.False(nav.CanGoBack);
        Assert.True(nav.CanGoForward);
    }

    [Fact]
    public void PushState_that_fails_to_apply_records_nothing()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());

        nav.PushState(_ => false, _ => { }); // the state couldn't take effect — no ghost step in history

        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void Forward_reapplies_a_reverted_state()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var applies = 0;

        nav.PushState(_ => { applies++; return true; }, _ => { });
        nav.Back();
        Assert.Equal(1, applies);

        nav.Forward();
        Assert.Equal(2, applies);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void Forward_drops_a_state_whose_apply_reports_failure()
    {
        var nav = new NavigationService();
        var root = new Page();
        nav.Reset(root);
        var valid = true;

        nav.PushState(_ => valid, _ => { });
        nav.Back();                   // the state is in forward
        Assert.True(nav.CanGoForward);

        valid = false;                // its target vanished while backed out (e.g. the asset was deleted)
        nav.Forward();

        Assert.False(nav.CanGoForward); // consumed…
        Assert.False(nav.CanGoBack);    // …but NOT recorded: the next Back isn't eaten by a ghost step
        Assert.Same(root, nav.Current);
    }

    [Fact]
    public void Forward_reapplies_a_state_to_the_rebuilt_page_across_a_page_boundary()
    {
        var nav = new NavigationService();
        var root = new Page();
        nav.Reset(root);
        var pageA1 = new Page();
        var pageA2 = new Page();
        nav.Push(pageA1, () => pageA2); // forward rebuilds the page as a fresh instance

        ViewModelBase? appliedTo = null;
        nav.PushState(cur => { appliedTo = cur; return true; }, _ => { });
        Assert.Same(pageA1, appliedTo);

        nav.Back();                     // revert the state
        nav.Back();                     // pop the page → root
        Assert.Same(root, nav.Current);

        nav.Forward();                  // rebuild the page
        Assert.Same(pageA2, nav.Current);
        nav.Forward();                  // re-apply the state to the CURRENT (rebuilt) page
        Assert.Same(pageA2, appliedTo); // not the original pageA1 — proves cross-page forward
    }

    [Fact]
    public void ReplaceTopState_swaps_the_top_state_without_reverting_it()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var log = new List<string>();

        nav.PushState(_ => { log.Add("applyA"); return true; }, _ => log.Add("revertA"));
        nav.ReplaceTopState(_ => { log.Add("applyB"); return true; }, _ => log.Add("revertB"));

        Assert.Equal(new[] { "applyA", "applyB" }, log); // B applied; A dropped, NOT reverted

        nav.Back();
        Assert.Equal(new[] { "applyA", "applyB", "revertB" }, log); // Back reverts B; A isn't beneath it
        Assert.False(nav.CanGoBack);                                 // only one state was ever on the stack
    }

    [Fact]
    public void DropCurrentStates_removes_open_states_without_reverting_and_clears_forward()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var reverts = 0;

        nav.PushState(_ => true, _ => reverts++);
        nav.PushState(_ => true, _ => reverts++);
        nav.DropCurrentStates();

        Assert.Equal(0, reverts);    // dropped (the page closed them itself), not reverted
        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void Push_collapses_open_states_before_the_new_page()
    {
        var nav = new NavigationService();
        var root = new Page();
        nav.Reset(root);
        var reverts = 0;

        nav.PushState(_ => true, _ => reverts++);
        nav.Push(new Page());

        Assert.Equal(1, reverts);  // the open state was reverted before the page was pushed

        nav.Back();                // pop the new page
        Assert.Same(root, nav.Current); // straight to root — the state wasn't buried beneath the page
    }

    [Fact]
    public void DropCurrentStates_clears_a_backed_out_forward_state_even_with_no_on_stack_states()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        nav.PushState(_ => true, _ => { });
        nav.Back();                   // the state is now in forward, none on the stack
        Assert.True(nav.CanGoForward);

        nav.DropCurrentStates();      // the page's contents changed under it → its forward state is stale
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void DropCurrentStates_preserves_backed_out_forward_pages()
    {
        var nav = new NavigationService();
        var root = new Page();
        nav.Reset(root);
        var rebuilt = new Page();
        nav.Push(new Page(), () => rebuilt);
        nav.Back();                   // forward = [the page's rebuild thunk]
        Assert.True(nav.CanGoForward);

        nav.DropCurrentStates();      // the ROOT's contents changed (search/reload) — the page is still rebuildable

        Assert.True(nav.CanGoForward); // a search keystroke must not kill the forward button
        nav.Forward();
        Assert.Same(rebuilt, nav.Current);
    }

    [Fact]
    public void DropCurrentStates_preserves_forward_states_belonging_to_a_backed_out_page()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var rebuilt = new Page();
        nav.Push(new Page(), () => rebuilt);
        ViewModelBase? appliedTo = null;
        nav.PushState(cur => { appliedTo = cur; return true; }, _ => { });

        nav.Back();                   // state → forward (bottom)
        nav.Back();                   // page → forward (top): the state now belongs to the backed-out page
        nav.DropCurrentStates();      // the ROOT reloaded — must not touch the page or ITS state beneath it

        Assert.True(nav.CanGoForward);
        nav.Forward();                // rebuild the page
        nav.Forward();                // re-apply its state
        Assert.Same(rebuilt, appliedTo);
    }

    [Fact]
    public void Forward_drops_orphaned_states_when_the_rebuild_thunk_returns_null()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        nav.Push(new Page(), () => null); // a page whose thunk can no longer rebuild (project deleted while backed out)
        nav.PushState(_ => true, _ => { }); // a state that lives ON that page

        nav.Back();                   // revert the state → forward
        nav.Back();                   // pop the page → forward (page nulled, thunk kept)
        Assert.True(nav.CanGoForward);

        nav.Forward();                // thunk returns null → drop the dead page AND its orphaned state
        Assert.False(nav.CanGoForward); // no phantom state left to forward into a foreign page
        Assert.False(nav.CanGoBack);    // back at the root only
    }

    [Fact]
    public void Back_is_absorbed_while_the_current_page_wants_to_absorb_it()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var board = new AbsorbingPage();
        nav.Push(board);

        board.Absorb = true;
        nav.Back();                   // page is mid-transition → absorbed, not popped
        Assert.Same(board, nav.Current);
        Assert.True(nav.CanGoBack);

        board.Absorb = false;
        nav.Back();                   // now pops normally
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Forward_is_absorbed_while_the_current_page_wants_to_absorb_it()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var board = new AbsorbingPage();
        nav.Push(board);
        var applies = 0;
        nav.PushState(_ => { applies++; return true; }, _ => { });
        nav.Back();                   // state → forward (the band is animating closed)
        Assert.Equal(1, applies);

        board.Absorb = true;          // mid-collapse: a re-apply would be a setter-equality no-op
        nav.Forward();
        Assert.Equal(1, applies);     // swallowed — the step stays in forward, history stays truthful
        Assert.True(nav.CanGoForward);

        board.Absorb = false;
        nav.Forward();                // after the animation: applies normally
        Assert.Equal(2, applies);
        Assert.False(nav.CanGoForward);
    }

    // ── Page-lifetime contract ──────────────────────────────────────────────────────

    [Fact]
    public void Back_disposes_the_popped_page_exactly_once_and_BEFORE_swapping_current()
    {
        var nav = new NavigationService();
        var root = new Page();
        nav.Reset(root);
        var board = new DisposablePage(nav);
        nav.Push(board);

        nav.Back();

        Assert.Equal(1, board.Disposals);
        // The load-bearing ordering: the page is disposed while it is STILL Current (its view still attached), so
        // its ViewTeardown can force the ItemsRepeater through a final layout pass. Disposing after the swap
        // regresses the board memory leak with every other test green.
        Assert.Same(board, board.CurrentAtDispose);
        Assert.Same(root, nav.Current);
    }

    [Fact]
    public void Reset_disposes_the_pages_it_clears()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        var board = new DisposablePage(nav);
        nav.Push(board);

        nav.Reset(new Page());

        Assert.Equal(1, board.Disposals);
    }
}
