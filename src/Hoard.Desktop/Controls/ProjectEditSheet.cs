using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Contents of the project Edit popup (shown inside a SheetHost): project info + actions. Rename toggles the
/// read-only name field to editable and swaps the Rename button for confirm/cancel (✓/✕) icon buttons;
/// confirming raises <see cref="RenameCommand"/> with the new name, cancelling reverts it. The other actions
/// (open folder / clear cache / remove / delete) are plain commands the host supplies. Display values are
/// pre-formatted strings so the host owns the wording.
/// </summary>
public partial class ProjectEditSheet : UserControl
{
    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(ProjectName), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> FolderPathProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(FolderPath));

    public static readonly StyledProperty<string?> CountsTextProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(CountsText));

    public static readonly StyledProperty<string?> BoardsTextProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(BoardsText));

    public static readonly StyledProperty<string?> CacheTextProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(CacheText));

    public static readonly StyledProperty<string?> OnDiskTextProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(OnDiskText));

    public static readonly StyledProperty<string?> AddedTextProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(AddedText));

    public static readonly StyledProperty<string?> ModifiedTextProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(ModifiedText));

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<ProjectEditSheet, bool>(nameof(IsEditing));

    public static readonly StyledProperty<ICommand?> OpenFolderCommandProperty =
        AvaloniaProperty.Register<ProjectEditSheet, ICommand?>(nameof(OpenFolderCommand));

    public static readonly StyledProperty<ICommand?> RenameCommandProperty =
        AvaloniaProperty.Register<ProjectEditSheet, ICommand?>(nameof(RenameCommand));

    public static readonly StyledProperty<string?> VerifyTextProperty =
        AvaloniaProperty.Register<ProjectEditSheet, string?>(nameof(VerifyText));

    public static readonly StyledProperty<ICommand?> VerifyCommandProperty =
        AvaloniaProperty.Register<ProjectEditSheet, ICommand?>(nameof(VerifyCommand));

    public static readonly StyledProperty<ICommand?> ClearCacheCommandProperty =
        AvaloniaProperty.Register<ProjectEditSheet, ICommand?>(nameof(ClearCacheCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<ProjectEditSheet, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<ProjectEditSheet, ICommand?>(nameof(DeleteCommand));

    public string? ProjectName { get => GetValue(ProjectNameProperty); set => SetValue(ProjectNameProperty, value); }
    public string? FolderPath { get => GetValue(FolderPathProperty); set => SetValue(FolderPathProperty, value); }
    public string? CountsText { get => GetValue(CountsTextProperty); set => SetValue(CountsTextProperty, value); }
    public string? BoardsText { get => GetValue(BoardsTextProperty); set => SetValue(BoardsTextProperty, value); }
    public string? CacheText { get => GetValue(CacheTextProperty); set => SetValue(CacheTextProperty, value); }
    public string? OnDiskText { get => GetValue(OnDiskTextProperty); set => SetValue(OnDiskTextProperty, value); }
    public string? AddedText { get => GetValue(AddedTextProperty); set => SetValue(AddedTextProperty, value); }
    public string? ModifiedText { get => GetValue(ModifiedTextProperty); set => SetValue(ModifiedTextProperty, value); }
    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }
    public ICommand? OpenFolderCommand { get => GetValue(OpenFolderCommandProperty); set => SetValue(OpenFolderCommandProperty, value); }
    public ICommand? RenameCommand { get => GetValue(RenameCommandProperty); set => SetValue(RenameCommandProperty, value); }
    public string? VerifyText { get => GetValue(VerifyTextProperty); set => SetValue(VerifyTextProperty, value); }
    public ICommand? VerifyCommand { get => GetValue(VerifyCommandProperty); set => SetValue(VerifyCommandProperty, value); }
    public ICommand? ClearCacheCommand { get => GetValue(ClearCacheCommandProperty); set => SetValue(ClearCacheCommandProperty, value); }
    public ICommand? RemoveCommand { get => GetValue(RemoveCommandProperty); set => SetValue(RemoveCommandProperty, value); }
    public ICommand? DeleteCommand { get => GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }

    private string? _originalName;

    public ProjectEditSheet()
    {
        InitializeComponent();
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        _originalName = ProjectName;
        IsEditing = true;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnConfirmRename(object? sender, RoutedEventArgs e)
    {
        IsEditing = false;
        if (RenameCommand is { } cmd && cmd.CanExecute(ProjectName))
            cmd.Execute(ProjectName);
    }

    private void OnCancelRename(object? sender, RoutedEventArgs e)
    {
        ProjectName = _originalName; // revert
        IsEditing = false;
    }
}
