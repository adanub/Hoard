using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Navigation;

/// <summary>
/// The shell's page back-stack. The shell binds <see cref="Current"/>; screens push the next page and pop to
/// return. Built now for the Projects ↔ Library swap, but as the spine for the deeper
/// Projects → Library → Board → Image-detail flow later slices add (replaces the old pair of swap callbacks).
/// </summary>
public partial class NavigationService : ObservableObject
{
    private readonly Stack<ViewModelBase> _stack = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private ViewModelBase? _current;

    /// <summary>True when a page sits beneath the current one (something to go <see cref="Pop"/> back to).</summary>
    public bool CanGoBack => _stack.Count > 1;

    /// <summary>Start a fresh stack at <paramref name="root"/> (e.g. returning to the launcher), disposing the
    /// pages that were on the stack so their subscriptions/resources are released.</summary>
    public void Reset(ViewModelBase root)
    {
        foreach (var page in _stack) (page as IDisposable)?.Dispose();
        _stack.Clear();
        _stack.Push(root);
        Current = root;
    }

    /// <summary>Navigate forward, keeping the current page beneath the new one.</summary>
    public void Push(ViewModelBase page)
    {
        _stack.Push(page);
        Current = page;
    }

    /// <summary>Return to the previous page; a no-op at the root. Disposes the page being left.</summary>
    public void Pop()
    {
        if (_stack.Count <= 1) return;
        var popped = _stack.Pop();
        Current = _stack.Peek();
        (popped as IDisposable)?.Dispose();
    }
}
