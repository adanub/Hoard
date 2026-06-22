using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Navigation;

/// <summary>A page that wants to refresh when it's revealed again by a <see cref="NavigationService.Pop"/> (e.g.
/// a board reloading its folder row after a child folder was renamed/deleted from inside it, or the launcher
/// reloading its recents). Not called by <see cref="NavigationService.GoForward"/> — that rebuilds the page
/// fresh, so its constructor already loads.</summary>
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

/// <summary>
/// The shell's browser-style page back/forward stack. The shell binds <see cref="Current"/>; screens push the
/// next page and pop to return. <see cref="Pop"/> still <b>disposes</b> the page it leaves (so a backed-out board
/// releases its subscriptions + tiles — no leak), keeping only a lightweight rebuild <i>thunk</i> so
/// <see cref="GoForward"/> can return to a fresh copy. A new <see cref="Push"/> clears the forward history, like a
/// browser. Built for Projects → Library → Board → … drill-down.
/// </summary>
public partial class NavigationService : ObservableObject
{
    private readonly record struct Entry(ViewModelBase Page, Func<ViewModelBase?>? Recreate);

    private readonly Stack<Entry> _stack = new();
    // Forward history holds only rebuild thunks, never live pages — so going back never keeps a board (and its
    // decoded thumbnails) alive; forward rebuilds it fresh. A thunk returns null when it can no longer rebuild
    // validly (e.g. its project was deleted while we were backed out), so GoForward drops the dead entry instead
    // of pushing a blank page.
    private readonly Stack<Func<ViewModelBase?>> _forward = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoForward))]
    private ViewModelBase? _current;

    /// <summary>True when a page sits beneath the current one (something to go <see cref="Pop"/> back to).</summary>
    public bool CanGoBack => _stack.Count > 1;

    /// <summary>True when a page was backed out of and can be returned to via <see cref="GoForward"/>.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Start a fresh stack at <paramref name="root"/> (e.g. returning to the launcher), disposing the
    /// pages that were on the stack so their subscriptions/resources are released, and clearing forward history.</summary>
    public void Reset(ViewModelBase root)
    {
        foreach (var e in _stack) (e.Page as IDisposable)?.Dispose();
        _stack.Clear();
        _forward.Clear();
        _stack.Push(new Entry(root, null));
        Current = root;
    }

    /// <summary>Navigate forward to a new page, keeping the current one beneath it. Pass <paramref name="recreate"/>
    /// for any page reachable by the back button so <see cref="GoForward"/> can rebuild it after a
    /// <see cref="Pop"/>. A fresh push clears the forward history, like a browser.</summary>
    public void Push(ViewModelBase page, Func<ViewModelBase?>? recreate = null)
    {
        _forward.Clear();
        _stack.Push(new Entry(page, recreate));
        Current = page;
    }

    /// <summary>Return to the previous page; a no-op at the root. Disposes the page being left (its rebuild thunk,
    /// if any, is kept so <see cref="GoForward"/> can return) and lets the page revealed beneath it refresh
    /// (<see cref="IResumable"/>).</summary>
    public void Pop()
    {
        if (_stack.Count <= 1) return;
        var popped = _stack.Pop();
        var revealed = _stack.Peek().Page;
        // Record the forward thunk BEFORE assigning Current, so the CanGoForward change notification (raised by the
        // Current setter) already sees the new forward entry.
        if (popped.Recreate is not null) _forward.Push(popped.Recreate);
        Current = revealed;
        (popped.Page as IDisposable)?.Dispose();
        (revealed as IResumable)?.OnResumed();
    }

    /// <summary>Return to a page that was backed out of, rebuilt fresh from its thunk (the original instance was
    /// disposed on <see cref="Pop"/>, so its constructor reloads — no <see cref="IResumable.OnResumed"/> needed).
    /// A no-op when there's no forward history.</summary>
    public void GoForward()
    {
        if (_forward.Count == 0) return;
        var recreate = _forward.Pop();
        var page = recreate();
        if (page is null)
        {
            // The thunk can no longer rebuild a valid page (e.g. its project was deleted while we were backed
            // out) — drop the dead entry rather than pushing a blank page. Notify since _forward shrank.
            OnPropertyChanged(nameof(CanGoForward));
            return;
        }
        _stack.Push(new Entry(page, recreate));
        Current = page;
    }
}
