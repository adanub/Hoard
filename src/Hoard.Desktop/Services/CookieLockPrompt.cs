using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Controls;

namespace Hoard.Desktop.Services;

/// <summary>
/// Drives the "your browser is holding its cookies" warning over a view's existing confirm popup. Shared by
/// the Library and Board screens so the two say the same thing — the wording is the whole value here, and a
/// second copy of it would drift.
/// </summary>
internal static class CookieLockPrompt
{
    /// <summary>Show the warning; the task resolves true to import anyway, false to back out. Cancelling and
    /// dismissing (Esc / scrim) are the same answer — the sheet that triggered this stays open behind it, so
    /// backing out lands the user where they can close the browser or pick another one.</summary>
    public static Task<bool> ShowAsync(SheetHost host, ConfirmSheet sheet, string browser)
    {
        var answer = new TaskCompletionSource<bool>();

        void Close(bool proceed)
        {
            host.IsOpen = false;
            answer.TrySetResult(proceed); // Try: dismiss can race the buttons
        }

        // A task nobody completes would suspend the calling import forever, and its state machine would hold
        // the whole screen alive with it. The shell's Back/Esc dismisses the topmost sheet before it
        // navigates, so this shouldn't be reachable - but the cost of being wrong is a leaked page.
        void OnDetached(object? _, VisualTreeAttachmentEventArgs __)
        {
            host.DetachedFromVisualTree -= OnDetached;
            answer.TrySetResult(false);
        }

        host.DetachedFromVisualTree += OnDetached;
        answer.Task.ContinueWith(_ => host.DetachedFromVisualTree -= OnDetached,
            TaskScheduler.FromCurrentSynchronizationContext());

        sheet.Title = $"{browser} is open";
        sheet.Message =
            $"{browser} holds its cookie database open while it's running, so Hoard can't read your " +
            $"Pinterest login from it and private boards will come back empty. Close {browser} completely, " +
            "then run this again.";
        sheet.ConfirmLabel = "Import anyway";
        sheet.ConfirmCommand = new RelayCommand(() => Close(true));
        sheet.CancelCommand = new RelayCommand(() => Close(false));
        host.DismissCommand = new RelayCommand(() => Close(false));
        sheet.Begin(0); // nothing is destroyed here, so no cooldown
        host.IsOpen = true;
        return answer.Task;
    }
}
