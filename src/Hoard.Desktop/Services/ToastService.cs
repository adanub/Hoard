using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hoard.Desktop.Services;

/// <summary>A single status message shown as a toast, with optional long-form detail behind its ⋯ button.</summary>
public sealed partial class ToastItem : ObservableObject
{
    /// <summary>The one-line-ish message. Keep it to what fits a 360px card — the rest belongs in
    /// <see cref="Details"/>, which the toast's ⋯ button opens in a sheet.</summary>
    public string Message { get; }

    public bool IsError { get; }

    /// <summary>The full story behind the message (a whole run's per-board failures, an exception's raw tail):
    /// shown in a scrollable, copyable sheet rather than crammed into the card. Null when there's no more
    /// to say — the ⋯ button hides itself.</summary>
    public string? Details { get; }

    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);

    /// <summary>Close this toast. Wired by the owning <see cref="ToastService"/> so the item template can
    /// bind straight to it (<c>{Binding DismissCommand}</c>) instead of walking up to the host's DataContext.</summary>
    public IRelayCommand DismissCommand { get; }

    /// <summary>Open <see cref="Details"/> in the shell's sheet. Same wiring as <see cref="DismissCommand"/>.</summary>
    public IRelayCommand ShowDetailsCommand { get; }

    internal ToastItem(string message, bool isError, string? details, Action<ToastItem> dismiss,
        Action<ToastItem> showDetails)
    {
        Message = message;
        IsError = isError;
        Details = details;
        DismissCommand = new RelayCommand(() => dismiss(this));
        ShowDetailsCommand = new RelayCommand(() => showDetails(this));
    }
}

/// <summary>
/// App-wide status messages, shown by <see cref="Controls.ToastHost"/> as a stack of cards in the bottom-right
/// corner. Owned by the shell and handed to page view models so any screen can surface feedback.
/// <para><b>Toasts do not self-dismiss</b> — each one waits for its ✕ (or "Clear all"). They used to fade after
/// four seconds, which lost the one report a long run gives: a project-wide sync that failed on every board
/// announced itself for four seconds at the end of a run the user had walked away from, and was then gone.
/// A message that has to be caught in a window isn't a message.</para>
/// <para>The stack is capped so it can't fill the window, and the cap sheds the oldest <i>non-error</i> first:
/// routine confirmations are what pile up, and an error is exactly the thing that must not disappear
/// unread.</para>
/// Call on the UI thread.
/// </summary>
public sealed partial class ToastService : ObservableObject
{
    /// <summary>How many cards may stand at once. Deliberately generous — the cap exists to stop an unbounded
    /// pile, not to ration messages, and the column scrolls rather than running off the top of a short window
    /// (see ToastHost). Shedding starts only past this.</summary>
    internal const int MaxVisible = 15;

    public ObservableCollection<ToastItem> Toasts { get; } = new();

    /// <summary>"Clear all" is only offered once there's a pile — a second dismiss button under a single
    /// toast that already has its own ✕ is just noise.</summary>
    public bool ShowClearAll => Toasts.Count > 1;

    /// <summary>The toast whose details the sheet is showing (null = closed).</summary>
    [ObservableProperty] private ToastItem? _detailsToast;

    public bool IsDetailsOpen => DetailsToast is not null;

    partial void OnDetailsToastChanged(ToastItem? value) => OnPropertyChanged(nameof(IsDetailsOpen));

    public ToastService() => Toasts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowClearAll));

    /// <param name="message">The card's text — keep it short enough to read at a glance.</param>
    /// <param name="isError">Renders in the destructive accent, and survives the stack cap longest.</param>
    /// <param name="details">Optional long form for the ⋯ button: the log-grade version of the message.</param>
    public void Show(string message, bool isError = false, string? details = null)
    {
        Trim();
        Toasts.Add(new ToastItem(message, isError, details, Dismiss, ShowDetails));
    }

    public void Dismiss(ToastItem toast)
    {
        Toasts.Remove(toast);
        if (ReferenceEquals(DetailsToast, toast)) DetailsToast = null; // its sheet outliving it would be a ghost
    }

    [RelayCommand]
    public void ClearAll()
    {
        Toasts.Clear();
        DetailsToast = null;
    }

    public void ShowDetails(ToastItem toast) => DetailsToast = toast;

    [RelayCommand]
    private void CloseDetails() => DetailsToast = null;

    /// <summary>Make room for one more: drop the oldest routine toast, and only fall back to dropping an error
    /// when errors are all there is (they're the messages worth keeping, so they leave last).</summary>
    private void Trim()
    {
        while (Toasts.Count >= MaxVisible)
        {
            var victim = Toasts.FirstOrDefault(t => !t.IsError) ?? Toasts[0];
            Dismiss(victim);
        }
    }
}
