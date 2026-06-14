using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class ProjectLauncherView : UserControl
{
    public ProjectLauncherView()
    {
        InitializeComponent();
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ProjectLauncherViewModel vm && vm.OpenRecentCommand.CanExecute(null))
            vm.OpenRecentCommand.Execute(null);
    }

    private async void OnBrowseLocation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectLauncherViewModel vm && await PickFolderAsync("Choose where to create the project") is { } folder)
            vm.SetNewProjectLocation(folder);
    }

    private async void OnOpenExisting(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectLauncherViewModel vm && await PickFolderAsync("Open an existing project folder") is { } folder)
            await vm.OpenExistingAsync(folder);
    }

    private async void OnDeleteFromDisk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectLauncherViewModel { SelectedRecent: { } target } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var confirm = new ConfirmDialog(
            "Delete project",
            $"Permanently delete “{target.Name}” and ALL of its data?\n\n{target.Path}\n\n" +
            "This removes every downloaded image and the database for this project. It cannot be undone.",
            confirmLabel: "Delete",
            danger: true,
            countdownSeconds: 10);

        if (await confirm.ShowDialog<bool>(owner))
            vm.DeleteSelectedFromDisk();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
