using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Contents of the Backup popup (shown inside a SheetHost): the project's remote folder + picker, a
/// Sync action with progress, and remove. Display strings and commands are supplied by the host view —
/// the control stays dumb, like <see cref="ProjectEditSheet"/>.
/// </summary>
public partial class RemoteSheet : UserControl
{
    public static readonly StyledProperty<string?> PathTextProperty =
        AvaloniaProperty.Register<RemoteSheet, string?>(nameof(PathText));

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<RemoteSheet, string?>(nameof(StatusText));

    public static readonly StyledProperty<bool> HasRemoteProperty =
        AvaloniaProperty.Register<RemoteSheet, bool>(nameof(HasRemote));

    public static readonly StyledProperty<bool> IsSyncingProperty =
        AvaloniaProperty.Register<RemoteSheet, bool>(nameof(IsSyncing));

    public static readonly StyledProperty<ICommand?> ChooseCommandProperty =
        AvaloniaProperty.Register<RemoteSheet, ICommand?>(nameof(ChooseCommand));

    public static readonly StyledProperty<ICommand?> SyncCommandProperty =
        AvaloniaProperty.Register<RemoteSheet, ICommand?>(nameof(SyncCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<RemoteSheet, ICommand?>(nameof(RemoveCommand));

    public string? PathText { get => GetValue(PathTextProperty); set => SetValue(PathTextProperty, value); }
    public string? StatusText { get => GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }
    public bool HasRemote { get => GetValue(HasRemoteProperty); set => SetValue(HasRemoteProperty, value); }
    public bool IsSyncing { get => GetValue(IsSyncingProperty); set => SetValue(IsSyncingProperty, value); }
    public ICommand? ChooseCommand { get => GetValue(ChooseCommandProperty); set => SetValue(ChooseCommandProperty, value); }
    public ICommand? SyncCommand { get => GetValue(SyncCommandProperty); set => SetValue(SyncCommandProperty, value); }
    public ICommand? RemoveCommand { get => GetValue(RemoveCommandProperty); set => SetValue(RemoveCommandProperty, value); }

    public RemoteSheet()
    {
        InitializeComponent();
    }
}
