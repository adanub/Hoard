using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hoard.Desktop.Views;

/// <summary>A simple scrollable, selectable, copyable text dialog — used to view long status messages.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public MessageDialog(string title, string message) : this()
    {
        Title = title;
        MessageText.Text = message;
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        // Use the TextBox's own clipboard handling (version-stable across Avalonia's clipboard API churn).
        MessageText.SelectAll();
        MessageText.Copy();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
