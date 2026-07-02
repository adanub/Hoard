using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Navigation;

/// <summary>A page that wants to refresh when it's revealed again by a <see cref="NavigationService.Back"/> (e.g.
/// a board reloading its folder row after a child folder was renamed/deleted from inside it, or the launcher
/// reloading its recents). Not called by <see cref="NavigationService.Forward"/> — that rebuilds the page fresh,
/// so its constructor already loads.</summary>
public interface IResumable
{
    void OnResumed();
}

/// <summary>A page with its own "back" action (its ← button), so a global gesture (the mouse back button) can
/// trigger the <i>same</i> navigation the on-screen control does — a Board pops, the Library pops to Projects.</summary>
public interface IHasBack
{
    IRelayCommand BackCommand { get; }
}

/// <summary>A page that can absorb a <see cref="NavigationService.Back"/> OR <see cref="NavigationService.Forward"/>
/// while it's mid-transition (e.g. a board animating its detail band closed). Without this, a rapid second Back/Esc —
/// fired after the band's history step is already reverted but before its collapse animation clears the selection —
/// would pop the whole page out from under the animation; and a Forward in the same window would re-apply the band
/// step as a setter-equality no-op, leaving history claiming a band the animation then closes.</summary>
public interface IAbsorbsBack
{
    bool AbsorbBack();
}

/// <summary>
/// The shell's browser-style back/forward history. A <b>step</b> is either a full <see cref="PageStep"/> (swaps the
/// shown page — Projects → Library → Board → …) or an in-page <see cref="StateStep"/> (an overlay state on the
/// <i>current</i> page — the Board's image-detail band, or the fullscreen zoom). <see cref="Back"/> reverts the
/// topmost step; <see cref="Forward"/> re-applies it. State steps apply to <see cref="Current"/> (the rebuilt page)
/// rather than a captured instance, so forward re-opens a band/zoom even after the page was disposed and rebuilt.
/// <see cref="Back"/> still <b>disposes</b> a page it leaves (releasing its tiles/subscriptions), keeping only a
/// rebuild <i>thunk</i> so forward can return a fresh copy.
/// </summary>
public partial class NavigationService : ObservableObject
{
    private abstract record Step;
    // A page leaves the shell showing it; its rebuild thunk lets Forward return a fresh copy after a Back disposed it.
    private sealed record PageStep(ViewModelBase? Page, Func<ViewModelBase?>? Recreate) : Step;
    // An in-page overlay state on whatever page is Current; Apply/Revert take Current so they bind to the *rebuilt*
    // page after a cross-page forward (e.g. re-expand image 42 on a freshly-rebuilt Board). Apply returns whether the
    // state took effect (or was validly deferred) — Forward drops a step whose Apply fails instead of stacking a ghost.
    private sealed record StateStep(Func<ViewModelBase?, bool> Apply, Action<ViewModelBase?> Revert) : Step;

    private readonly Stack<Step> _stack = new();
    private readonly Stack<Step> _forward = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoForward))]
    private ViewModelBase? _current;

    /// <summary>True when there's a step (a page beneath, or an in-page state) to go <see cref="Back"/> to.</summary>
    public bool CanGoBack => _stack.Count > 1;

    /// <summary>True when a step was backed out of and can be returned to via <see cref="Forward"/>.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Start a fresh stack at <paramref name="root"/> (e.g. returning to the launcher), disposing the pages
    /// that were on the stack and clearing forward history.</summary>
    public void Reset(ViewModelBase root)
    {
        foreach (var s in _stack) Dispose(s);
        _stack.Clear();
        _forward.Clear();
        _stack.Push(new PageStep(root, null));
        Current = root;
    }

    /// <summary>Navigate forward to a new page, keeping the current one beneath it. Pass <paramref name="recreate"/>
    /// for any page reachable by Back so <see cref="Forward"/> can rebuild it. Clears forward history, like a browser.</summary>
    public void Push(ViewModelBase page, Func<ViewModelBase?>? recreate = null)
    {
        // A new page can't sit above the current page's open in-page states in a clean linear history (and a push
        // clears forward regardless), so collapse any trailing states first.
        while (_stack.Count > 0 && _stack.Peek() is StateStep s) { _stack.Pop(); s.Revert(Current); }
        _forward.Clear();
        _stack.Push(new PageStep(page, recreate));
        Current = page;
    }

    /// <summary>Push an in-page overlay state (the band, or the zoom) onto the history and apply it. <see cref="Current"/>
    /// (the page) is unchanged; <see cref="Back"/> reverts it and <see cref="Forward"/> re-applies it. Clears forward.
    /// <paramref name="apply"/> returns whether the state actually took effect (or was validly deferred until the
    /// page finishes loading) — an apply that reports failure is not recorded, so history can never hold a "ghost"
    /// step whose Back would be a dead press (e.g. re-opening the band on an asset that has since been deleted).</summary>
    public void PushState(Func<ViewModelBase?, bool> apply, Action<ViewModelBase?> revert)
    {
        _forward.Clear();
        if (!apply(Current)) { RaiseCanGoChanged(); return; } // nothing happened — record nothing
        _stack.Push(new StateStep(apply, revert));
        RaiseCanGoChanged();
    }

    /// <summary>Replace the topmost in-page state in place (e.g. the band switching to a different image) so history
    /// doesn't stack one over the other. Falls back to <see cref="PushState"/> when the top isn't a state.</summary>
    public void ReplaceTopState(Func<ViewModelBase?, bool> apply, Action<ViewModelBase?> revert)
    {
        if (_stack.Count > 0 && _stack.Peek() is StateStep) _stack.Pop(); // drop the old one (switching, not closing)
        PushState(apply, revert);
    }

    /// <summary>Go back one step: revert the topmost in-page state, or pop the current page (disposing it, revealing
    /// the one beneath, refreshing it via <see cref="IResumable"/>). No-op at the root. The single "back" for the
    /// mouse button, the ← chevron, and Esc.</summary>
    public void Back()
    {
        if (_stack.Count == 0) return;
        if (_stack.Peek() is StateStep s)
        {
            _stack.Pop();
            _forward.Push(s);
            s.Revert(Current);
            RaiseCanGoChanged();
            return;
        }

        if (_stack.Count <= 1) return; // a lone root page — nothing beneath
        // The current page can hold the back while it's mid-transition (a board animating its band closed): the band
        // step was just reverted but its collapse hasn't cleared the selection, so popping the page now would yank
        // the user off it mid-animation. Absorb this back; the next one (after the animation) pops normally.
        if (Current is IAbsorbsBack g && g.AbsorbBack()) return;
        var popped = (PageStep)_stack.Pop();
        var revealed = ((PageStep)_stack.Peek()).Page; // states never sit beneath a page (Push collapses them)
        _forward.Push(popped with { Page = null });    // forward keeps only the rebuild thunk
        // Dispose the outgoing page BEFORE swapping Current — its view is still attached now, but setting Current
        // detaches it synchronously. A page (the Board) leans on being disposed while attached so it can force its
        // ItemsRepeater through one last layout pass to recycle + detach its realized tiles (ViewTeardown); a
        // detached repeater never runs layout again, so doing this after the swap would strand the tiles (and, via a
        // tile's #Root self-binding, the whole view) in memory. No frame renders between here and the swap, so the
        // briefly-disposed page is never shown.
        (popped.Page as IDisposable)?.Dispose();
        Current = revealed;
        (revealed as IResumable)?.OnResumed();
    }

    /// <summary>Go forward one step: re-apply an in-page state to the current page, or rebuild a backed-out page
    /// (the original was disposed on <see cref="Back"/>, so its constructor reloads). No-op with no forward history.</summary>
    public void Forward()
    {
        if (_forward.Count == 0) return;
        // Mirror Back's absorb: while the current page is mid-transition (the board animating its band closed,
        // SelectedAsset still set), re-applying a band state would be silently equality-skipped by the setter and
        // leave a phantom step on the stack — history saying "open" over a band the animation then closes. Swallow
        // the press; the next one (after the animation) applies normally.
        if (Current is IAbsorbsBack g && g.AbsorbBack()) return;
        var step = _forward.Pop();
        if (step is StateStep s)
        {
            // Only record the step if it actually took effect — the asset it targets may have been deleted while
            // backed out (Apply reports failure), and a recorded-but-inert step would eat the user's next Back.
            if (s.Apply(Current)) _stack.Push(s);
            RaiseCanGoChanged();
            return;
        }

        var p = (PageStep)step;
        var page = p.Recreate?.Invoke();
        if (page is null)
        {
            // The thunk can no longer rebuild a valid page (its project was deleted while backed out) — drop the
            // dead entry rather than pushing a blank page. Also drop the in-page states that belonged to it (now
            // exposed on top of _forward): they can't apply to a foreign page, so a later Forward would surface them
            // as phantom no-op steps on the wrong page. Notify since _forward shrank.
            while (_forward.Count > 0 && _forward.Peek() is StateStep) _forward.Pop();
            OnPropertyChanged(nameof(CanGoForward));
            return;
        }
        _stack.Push(new PageStep(page, p.Recreate));
        Current = page;
    }

    /// <summary>Drop the current page's in-page states from history WITHOUT reverting them (the page already
    /// closed them itself — e.g. a Board reload/move recreated its tiles and collapsed the band). Keeps back/forward
    /// coherent so a later Back doesn't try to revert a state that's already gone, and a later Forward doesn't
    /// re-open one over the changed page. Backed-out PAGES (and the states that belong to them, which sit beneath
    /// their thunk in the forward stack) are untouched — they rebuild fresh, so an in-page change here can't
    /// invalidate them. (An earlier version cleared ALL of forward, so one search keystroke — whose debounced
    /// reload calls this — permanently killed the forward button.)</summary>
    public void DropCurrentStates()
    {
        var changed = false;
        while (_stack.Count > 0 && _stack.Peek() is StateStep) { _stack.Pop(); changed = true; }
        // Only the TOP run of forward StateSteps belongs to the current page: states are pushed to _forward while
        // their page is current, and popping that page stacks its thunk ABOVE them — so any state under a PageStep
        // belongs to that (rebuildable) page, never to this one.
        while (_forward.Count > 0 && _forward.Peek() is StateStep) { _forward.Pop(); changed = true; }
        if (changed) RaiseCanGoChanged();
    }

    private static void Dispose(Step s)
    {
        if (s is PageStep p) (p.Page as IDisposable)?.Dispose();
    }

    private void RaiseCanGoChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}
