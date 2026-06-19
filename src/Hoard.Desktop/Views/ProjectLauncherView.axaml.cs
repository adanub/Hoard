using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

public partial class ProjectLauncherView : UserControl
{
    public ProjectLauncherView()
    {
        InitializeComponent();
        WireEditSheet();
    }

    private ProjectLauncherViewModel? Vm => DataContext as ProjectLauncherViewModel;

    // The Edit popup's actions are orchestrated here (sheets + the delete confirm); the view model owns the
    // data. The lambdas read Vm/EditTarget lazily, so they pick up the DataContext set after construction.
    private void WireEditSheet()
    {
        EditSheet.OpenFolderCommand = new RelayCommand(() =>
        {
            if (Vm?.EditTarget is { } r) _ = OpenFolderAsync(r.Path);
        });
        EditSheet.RenameCommand = new RelayCommand<object?>(name => Vm?.RenameEditTarget(name as string));
        EditSheet.ClearCacheCommand = new RelayCommand(() =>
        {
            if (Vm?.EditTarget is { } r) _ = Vm.ClearCacheAsync(r);
        });
        EditSheet.RemoveCommand = new RelayCommand(() =>
        {
            if (Vm is { EditTarget: { } r } vm) { vm.Forget(r); vm.CloseEditSheetCommand.Execute(null); }
        });
        EditSheet.DeleteCommand = new RelayCommand(() =>
        {
            if (Vm?.EditTarget is { } r) ShowDeleteConfirm(r);
        });
        ConfirmHost.DismissCommand = new RelayCommand(() => ConfirmHost.IsOpen = false);
    }

    // Configure + open the confirm popup (on top of the Edit popup); a 10s cooldown guards the deletion.
    private void ShowDeleteConfirm(RecentProjectRef r)
    {
        ConfirmContent.Title = "Delete project?";
        ConfirmContent.Message = $"Move “{r.Name}” and all of its data to your recycle bin?\n\n{r.Path}";
        ConfirmContent.ConfirmLabel = "Delete";
        ConfirmContent.ConfirmCommand = new RelayCommand(() =>
        {
            ConfirmHost.IsOpen = false;
            if (Vm is { } vm) { vm.CloseEditSheetCommand.Execute(null); vm.DeleteFromDisk(r); }
        });
        ConfirmContent.CancelCommand = new RelayCommand(() => ConfirmHost.IsOpen = false);
        ConfirmContent.Begin(10);
        ConfirmHost.IsOpen = true;
    }

    private async Task OpenFolderAsync(string path)
    {
        if (TopLevel.GetTopLevel(this) is { } top && Directory.Exists(path))
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

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
