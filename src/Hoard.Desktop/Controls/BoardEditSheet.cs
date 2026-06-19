using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace Hoard.Desktop.Controls;

/// <summary>One Pinterest source board merged into a local board (a row in the board Edit popup's source list).</summary>
public sealed class BoardSourceRef
{
    /// <summary>The <c>CollectionSource</c> row id, so the host can remove this exact source.</summary>
    public int Id { get; }
    public string Name { get; }
    public string Url { get; }
    /// <summary>The board's live images attributed to this source — what un-merging "with images" would remove.</summary>
    public int ImageCount { get; }

    public BoardSourceRef(int id, string name, string url, int imageCount)
    {
        Id = id;
        Name = name;
        Url = url;
        ImageCount = imageCount;
    }
}

/// <summary>
/// Contents of the board Edit popup (shown in a SheetHost): rename (a local display name), the list of merged
/// Pinterest <see cref="SourceBoards"/> (open / remove each + add another), detailed info, and clear-cache /
/// delete actions. Rename toggles read-only ↔ editable with ✓/✕, like the project popup. Display values are
/// pre-formatted strings so the host owns the wording.
/// </summary>
public partial class BoardEditSheet : UserControl
{
    public static readonly StyledProperty<string?> BoardNameProperty =
        AvaloniaProperty.Register<BoardEditSheet, string?>(nameof(BoardName), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<BoardEditSheet, bool>(nameof(IsEditing));

    public static readonly StyledProperty<IEnumerable?> SourceBoardsProperty =
        AvaloniaProperty.Register<BoardEditSheet, IEnumerable?>(nameof(SourceBoards));

    public static readonly StyledProperty<string?> CountsTextProperty =
        AvaloniaProperty.Register<BoardEditSheet, string?>(nameof(CountsText));

    public static readonly StyledProperty<string?> CacheTextProperty =
        AvaloniaProperty.Register<BoardEditSheet, string?>(nameof(CacheText));

    public static readonly StyledProperty<string?> AddedTextProperty =
        AvaloniaProperty.Register<BoardEditSheet, string?>(nameof(AddedText));

    public static readonly StyledProperty<string?> ImportedTextProperty =
        AvaloniaProperty.Register<BoardEditSheet, string?>(nameof(ImportedText));

    public static readonly StyledProperty<ICommand?> RenameCommandProperty =
        AvaloniaProperty.Register<BoardEditSheet, ICommand?>(nameof(RenameCommand));

    public static readonly StyledProperty<ICommand?> OpenSourceCommandProperty =
        AvaloniaProperty.Register<BoardEditSheet, ICommand?>(nameof(OpenSourceCommand));

    public static readonly StyledProperty<ICommand?> RemoveSourceCommandProperty =
        AvaloniaProperty.Register<BoardEditSheet, ICommand?>(nameof(RemoveSourceCommand));

    public static readonly StyledProperty<ICommand?> AddSourceCommandProperty =
        AvaloniaProperty.Register<BoardEditSheet, ICommand?>(nameof(AddSourceCommand));

    public static readonly StyledProperty<ICommand?> ClearCacheCommandProperty =
        AvaloniaProperty.Register<BoardEditSheet, ICommand?>(nameof(ClearCacheCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<BoardEditSheet, ICommand?>(nameof(DeleteCommand));

    public string? BoardName { get => GetValue(BoardNameProperty); set => SetValue(BoardNameProperty, value); }
    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }
    public IEnumerable? SourceBoards { get => GetValue(SourceBoardsProperty); set => SetValue(SourceBoardsProperty, value); }
    public string? CountsText { get => GetValue(CountsTextProperty); set => SetValue(CountsTextProperty, value); }
    public string? CacheText { get => GetValue(CacheTextProperty); set => SetValue(CacheTextProperty, value); }
    public string? AddedText { get => GetValue(AddedTextProperty); set => SetValue(AddedTextProperty, value); }
    public string? ImportedText { get => GetValue(ImportedTextProperty); set => SetValue(ImportedTextProperty, value); }
    public ICommand? RenameCommand { get => GetValue(RenameCommandProperty); set => SetValue(RenameCommandProperty, value); }
    public ICommand? OpenSourceCommand { get => GetValue(OpenSourceCommandProperty); set => SetValue(OpenSourceCommandProperty, value); }
    public ICommand? RemoveSourceCommand { get => GetValue(RemoveSourceCommandProperty); set => SetValue(RemoveSourceCommandProperty, value); }
    public ICommand? AddSourceCommand { get => GetValue(AddSourceCommandProperty); set => SetValue(AddSourceCommandProperty, value); }
    public ICommand? ClearCacheCommand { get => GetValue(ClearCacheCommandProperty); set => SetValue(ClearCacheCommandProperty, value); }
    public ICommand? DeleteCommand { get => GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }

    private string? _originalName;

    public BoardEditSheet()
    {
        InitializeComponent();
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        _originalName = BoardName;
        IsEditing = true;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnConfirmRename(object? sender, RoutedEventArgs e)
    {
        IsEditing = false;
        if (RenameCommand is { } cmd && cmd.CanExecute(BoardName))
            cmd.Execute(BoardName);
    }

    private void OnCancelRename(object? sender, RoutedEventArgs e)
    {
        BoardName = _originalName; // revert
        IsEditing = false;
    }
}
