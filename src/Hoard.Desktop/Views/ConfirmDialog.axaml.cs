using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hoard.Desktop.Views;

/// <summary>A modal yes/no confirmation. <c>ShowDialog&lt;bool&gt;</c> returns true if confirmed.</summary>
public partial class ConfirmDialog : Window
{
    private DispatcherTimer? _countdownTimer;
    private int _remaining;
    private string _confirmLabel = "OK";

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <param name="danger">Style the confirm button red (for destructive actions).</param>
    /// <param name="countdownSeconds">Disable the confirm button for this many seconds first (0 = enabled immediately).</param>
    public ConfirmDialog(string title, string message, string confirmLabel, bool danger = false, int countdownSeconds = 0)
        : this()
    {
        Title = title;
        MessageText.Text = message;
        _confirmLabel = confirmLabel;
        ConfirmButton.Content = confirmLabel;
        if (danger) ConfirmButton.Classes.Add("danger");
        if (countdownSeconds > 0) StartCountdown(countdownSeconds);
    }

    private void StartCountdown(int seconds)
    {
        _remaining = seconds;
        ConfirmButton.IsEnabled = false;
        UpdateCountdownLabel();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _countdownTimer!.Stop();
                ConfirmButton.IsEnabled = true;
                ConfirmButton.Content = _confirmLabel;
            }
            else
            {
                UpdateCountdownLabel();
            }
        };
        _countdownTimer.Start();
    }

    private void UpdateCountdownLabel() => ConfirmButton.Content = $"{_confirmLabel} ({_remaining})";

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    protected override void OnClosed(EventArgs e)
    {
        _countdownTimer?.Stop();
        base.OnClosed(e);
    }
}
