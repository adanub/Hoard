using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Hoard.Desktop.Services;

/// <summary>A single transient status message shown as a toast.</summary>
public sealed class ToastItem
{
    public string Message { get; }
    public bool IsError { get; }

    public ToastItem(string message, bool isError)
    {
        Message = message;
        IsError = isError;
    }
}

/// <summary>
/// Transient, auto-dismissing status messages (DESIGN.md — "Toast"). Owned by the shell and handed to page
/// view models so any screen can surface feedback; the <see cref="Controls.ToastHost"/> renders
/// <see cref="Toasts"/>. Each toast removes itself after <see cref="Lifetime"/>. Call on the UI thread.
/// </summary>
public sealed class ToastService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);

    public ObservableCollection<ToastItem> Toasts { get; } = new();

    public void Show(string message, bool isError = false)
    {
        var toast = new ToastItem(message, isError);
        Toasts.Add(toast);
        // Self-dismiss; if it was already cleared (e.g. a future "dismiss all") the Remove is a harmless no-op.
        DispatcherTimer.RunOnce(() => Toasts.Remove(toast), Lifetime);
    }
}
