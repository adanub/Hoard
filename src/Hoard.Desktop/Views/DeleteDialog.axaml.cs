using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hoard.Desktop.Views;

/// <summary>
/// A delete confirmation that requires a reason. <c>ShowDialog&lt;string?&gt;</c> returns the trimmed note
/// if confirmed, or null if cancelled. The Delete button stays disabled until a note is entered.
/// </summary>
public partial class DeleteDialog : Window
{
    public DeleteDialog()
    {
        InitializeComponent();
        NoteBox.TextChanged += (_, _) => ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NoteBox.Text);
    }

    public DeleteDialog(string itemName) : this()
    {
        MessageText.Text =
            $"Delete “{itemName}” from the archive?\n\n" +
            "The image file is removed from disk, but it's kept as a tombstone (showing your reason) and " +
            "can be restored from its source later.";
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var note = NoteBox.Text?.Trim();
        if (string.IsNullOrEmpty(note)) return; // guard: button shouldn't be enabled, but never confirm blank
        Close(note);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
