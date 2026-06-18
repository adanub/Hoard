using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Contents of a confirmation popup (shown in a SheetHost): a title, a message, Cancel, and a destructive
/// Confirm button that can be gated behind an N-second cooldown — the Confirm stays disabled and shows the
/// countdown until it elapses (guards irreversible actions). Call <see cref="Begin"/> to (re)configure the
/// cooldown each time the popup is shown.
/// </summary>
public partial class ConfirmSheet : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ConfirmSheet, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<ConfirmSheet, string?>(nameof(Message));

    public static readonly StyledProperty<string?> ConfirmLabelProperty =
        AvaloniaProperty.Register<ConfirmSheet, string?>(nameof(ConfirmLabel), "Delete");

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<ConfirmSheet, ICommand?>(nameof(ConfirmCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ConfirmSheet, ICommand?>(nameof(CancelCommand));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ConfirmLabel { get => GetValue(ConfirmLabelProperty); set => SetValue(ConfirmLabelProperty, value); }
    public ICommand? ConfirmCommand { get => GetValue(ConfirmCommandProperty); set => SetValue(ConfirmCommandProperty, value); }
    public ICommand? CancelCommand { get => GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }

    private DispatcherTimer? _timer;
    private int _remaining;

    public ConfirmSheet()
    {
        InitializeComponent();
    }

    /// <summary>(Re)start the cooldown. <paramref name="countdownSeconds"/> &lt;= 0 enables Confirm immediately.</summary>
    public void Begin(int countdownSeconds)
    {
        _timer?.Stop();
        _remaining = Math.Max(0, countdownSeconds);
        UpdateConfirm();
        if (_remaining > 0)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                // The sheet may have been dismissed (scrim-click / Esc) without Cancel/Confirm running — its
                // host just hides it. Stop ticking on a hidden sheet so it stops mutating + rooting itself.
                if (!IsEffectivelyVisible) { _timer!.Stop(); return; }
                _remaining--;
                UpdateConfirm();
                if (_remaining <= 0) _timer!.Stop();
            };
            _timer.Start();
        }
    }

    private void UpdateConfirm()
    {
        ConfirmButton.IsEnabled = _remaining <= 0;
        ConfirmCountText.Text = _remaining > 0 ? $"({_remaining}s)" : "";
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        if (ConfirmCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        if (CancelCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }
}
