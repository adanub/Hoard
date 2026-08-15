using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.Controls;

public partial class ToastHost : UserControl
{
    private INotifyCollectionChanged? _watching;

    public ToastHost()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rewatch();
    }

    // Once the column is tall enough to scroll, a new toast lands BELOW the viewport unless we follow it —
    // and a message that arrives off-screen is the failure this whole design is about. Newest is at the
    // bottom (the pile grows upwards), so "follow the newest" is simply "stay at the end".
    private void Rewatch()
    {
        if (_watching is not null) _watching.CollectionChanged -= OnToastsChanged;
        _watching = (DataContext as ToastService)?.Toasts;
        if (_watching is not null) _watching.CollectionChanged += OnToastsChanged;
    }

    private void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // Posted: the new card hasn't been measured yet, so the scrollable extent it needs doesn't exist here.
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Loaded);
    }
}
