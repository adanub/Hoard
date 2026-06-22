using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Hoard.Desktop.Controls;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Views;

/// <summary>
/// The Library screen: the project's board grid (+ New board / All images / boards), and the import + board-Edit
/// sheets. Mostly view-model driven; this code-behind orchestrates the board Edit popup's actions (sheets +
/// the delete confirm + opening a source URL), like the launcher's project Edit popup.
/// </summary>
public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        WireBoardEditSheet();
    }

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void WireBoardEditSheet()
    {
        BoardEditSheet.RenameCommand = new RelayCommand<object?>(name =>
        {
            if (Vm is { } vm) _ = vm.BoardEditor.RenameAsync(name as string);
        });
        BoardEditSheet.ClearCacheCommand = new RelayCommand(() =>
        {
            if (Vm is { } vm) _ = vm.BoardEditor.ClearCacheAsync();
        });
        BoardEditSheet.AddSourceCommand = new RelayCommand(() => Vm?.AddSourceToEditTarget());
        BoardEditSheet.OpenSourceCommand = new RelayCommand<BoardSourceRef?>(s =>
        {
            if (s is { } src) _ = OpenUrlAsync(src.Url);
        });
        BoardEditSheet.RemoveSourceCommand = new RelayCommand<BoardSourceRef?>(ShowRemoveSourceConfirm);
        BoardEditSheet.DeleteCommand = new RelayCommand(ShowBoardDeleteConfirm);
        BoardConfirmHost.DismissCommand = new RelayCommand(() => BoardConfirmHost.IsOpen = false);
    }

    private void ShowRemoveSourceConfirm(BoardSourceRef? source)
    {
        if (source is null) return;
        BoardConfirmContent.Title = "Remove source board?";
        BoardConfirmContent.Message = source.ImageCount > 0
            ? $"Stop merging “{source.Name}” and delete its {source.ImageCount} image(s) — files go to your recycle bin (any also in another board are removed there too)."
            : $"Stop merging “{source.Name}” into this board. It won't be listed or re-synced.";
        BoardConfirmContent.ConfirmLabel = "Remove";
        BoardConfirmContent.ConfirmCommand = new RelayCommand(() =>
        {
            BoardConfirmHost.IsOpen = false;
            if (Vm is { } vm) _ = vm.RemoveSource(source);
        });
        BoardConfirmContent.CancelCommand = new RelayCommand(() => BoardConfirmHost.IsOpen = false);
        BoardConfirmContent.Begin(source.ImageCount > 0 ? 3 : 0); // a brief cooldown when it deletes images
        BoardConfirmHost.IsOpen = true;
    }

    private void ShowBoardDeleteConfirm()
    {
        if (Vm?.BoardEditor.EditTarget is not { } r) return;
        BoardConfirmContent.Title = "Delete board?";
        BoardConfirmContent.Message =
            $"Delete the board “{r.Name}” and its images — files go to your recycle bin (any also in another board are removed there too).";
        BoardConfirmContent.ConfirmLabel = "Delete";
        BoardConfirmContent.ConfirmCommand = new RelayCommand(() =>
        {
            BoardConfirmHost.IsOpen = false;
            if (Vm is { } vm) { vm.BoardEditor.CloseCommand.Execute(null); _ = vm.BoardEditor.DeleteAsync(); }
        });
        BoardConfirmContent.CancelCommand = new RelayCommand(() => BoardConfirmHost.IsOpen = false);
        BoardConfirmContent.Begin(5);
        BoardConfirmHost.IsOpen = true;
    }

    private async Task OpenUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                await top.Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Nothing actionable if the shell refuses to open it.
        }
    }
}
