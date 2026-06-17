using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class ProjectLauncherView : UserControl
{
    public ProjectLauncherView()
    {
        InitializeComponent();
    }

    private ProjectLauncherViewModel? Vm => DataContext as ProjectLauncherViewModel;

    private void OnNewProjectTileTapped(object? sender, TappedEventArgs e) => Vm?.OpenNewProjectSheetCommand.Execute(null);

    private void OnProjectCardTapped(object? sender, TappedEventArgs e)
    {
        // The ⋯ button lives inside the card and opens its own menu — ignore taps that originate on it.
        if ((e.Source as Visual)?.FindAncestorOfType<Button>() is not null) return;
        if (RefOf(sender) is { } r) _ = Vm?.OpenProjectAsync(r);
    }

    private void OnManageOpen(object? sender, RoutedEventArgs e)
    {
        if (RefOf(sender) is { } r) _ = Vm?.OpenProjectAsync(r);
    }

    private void OnManageClearCache(object? sender, RoutedEventArgs e)
    {
        if (RefOf(sender) is { } r) _ = Vm?.ClearCacheAsync(r);
    }

    private void OnManageForget(object? sender, RoutedEventArgs e)
    {
        if (RefOf(sender) is { } r) Vm?.Forget(r);
    }

    private async void OnManageDelete(object? sender, RoutedEventArgs e)
    {
        if (RefOf(sender) is not { } r || Vm is not { } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var confirm = new ConfirmDialog(
            "Delete project",
            $"Permanently delete “{r.Name}” and ALL of its data?\n\n{r.Path}\n\n" +
            "This removes every downloaded image and the database for this project. It cannot be undone.",
            confirmLabel: "Delete",
            danger: true,
            countdownSeconds: 10);

        if (await confirm.ShowDialog<bool>(owner))
            vm.DeleteFromDisk(r);
    }

    // The card body, the ⋯ button, and each menu item all inherit the card's RecentProjectRef DataContext.
    private static RecentProjectRef? RefOf(object? sender) => (sender as Control)?.DataContext as RecentProjectRef;

    private async void OnBrowseLocation(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolderAsync("Choose where to create the project") is { } folder)
            vm.SetNewProjectLocation(folder);
    }

    private async void OnOpenExisting(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolderAsync("Open an existing project folder") is { } folder)
            await vm.OpenExistingAsync(folder);
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
