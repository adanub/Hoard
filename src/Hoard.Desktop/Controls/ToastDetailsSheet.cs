using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Contents of the popup behind a toast's ⋯ button (shown in a SheetHost): the toast's own line, then its
/// long form in a scrollable, copyable box. The DataContext is the <see cref="Services.ToastItem"/> being
/// expanded, so the sheet needs no view model of its own — a toast card is 360px wide and some messages
/// (a whole sync run's per-board failures, a stack trace) simply don't fit one.
/// </summary>
public partial class ToastDetailsSheet : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ToastDetailsSheet, string?>(nameof(Title), "Details");

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<ToastDetailsSheet, ICommand?>(nameof(CloseCommand));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    public ToastDetailsSheet() => InitializeComponent();

    // The read-only TextBox's own Copy() — Avalonia 12 moved the clipboard to DataTransfer/DataFormat, and
    // the built-in command sidesteps that entirely (CLAUDE.md).
    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        DetailText.SelectAll();
        DetailText.Copy();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseCommand?.CanExecute(null) == true) CloseCommand.Execute(null);
    }
}
