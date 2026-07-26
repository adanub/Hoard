using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Contents of the Export popup (inside a SheetHost on the Board screen): pick a destination folder and
/// materialise the board's subtree as a browsable Board/Folder/image tree. Dumb control — state and
/// commands come from the hosting view model; the folder picker command is assigned by the view
/// (it needs the visual tree's TopLevel).
/// </summary>
public partial class ExportSheet : UserControl
{
    public static readonly StyledProperty<string?> PathTextProperty =
        AvaloniaProperty.Register<ExportSheet, string?>(nameof(PathText));

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<ExportSheet, string?>(nameof(StatusText));

    public static readonly StyledProperty<bool> IsExportingProperty =
        AvaloniaProperty.Register<ExportSheet, bool>(nameof(IsExporting));

    public static readonly StyledProperty<ICommand?> ChooseCommandProperty =
        AvaloniaProperty.Register<ExportSheet, ICommand?>(nameof(ChooseCommand));

    public static readonly StyledProperty<ICommand?> ExportCommandProperty =
        AvaloniaProperty.Register<ExportSheet, ICommand?>(nameof(ExportCommand));

    public string? PathText
    {
        get => GetValue(PathTextProperty);
        set => SetValue(PathTextProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public bool IsExporting
    {
        get => GetValue(IsExportingProperty);
        set => SetValue(IsExportingProperty, value);
    }

    public ICommand? ChooseCommand
    {
        get => GetValue(ChooseCommandProperty);
        set => SetValue(ChooseCommandProperty, value);
    }

    public ICommand? ExportCommand
    {
        get => GetValue(ExportCommandProperty);
        set => SetValue(ExportCommandProperty, value);
    }

    public ExportSheet()
    {
        InitializeComponent();
    }
}
