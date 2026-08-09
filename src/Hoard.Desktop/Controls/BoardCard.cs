using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A board card: the 3-up collage IS the card (full-bleed cover) — clicking it opens the board
/// (<see cref="OpenCommand"/>), with expand-on-hover / shrink-on-press feedback — and the name + metadata sit
/// below it with a pencil Edit button (<see cref="EditCommand"/>) to the right. The collage is built from the
/// board's own items.
/// </summary>
public partial class BoardCard : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<BoardCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MetaTextProperty =
        AvaloniaProperty.Register<BoardCard, string?>(nameof(MetaText));

    public static readonly StyledProperty<Bitmap?> Thumb0Property =
        AvaloniaProperty.Register<BoardCard, Bitmap?>(nameof(Thumb0));

    public static readonly StyledProperty<Bitmap?> Thumb1Property =
        AvaloniaProperty.Register<BoardCard, Bitmap?>(nameof(Thumb1));

    public static readonly StyledProperty<Bitmap?> Thumb2Property =
        AvaloniaProperty.Register<BoardCard, Bitmap?>(nameof(Thumb2));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<BoardCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<BoardCard, ICommand?>(nameof(EditCommand));

    /// <summary>While true, the card shows a pinned inline import strip (animated bar + <see cref="ImportStatusText"/>).</summary>
    public static readonly StyledProperty<bool> IsImportingProperty =
        AvaloniaProperty.Register<BoardCard, bool>(nameof(IsImporting));

    /// <summary>
    /// While true, this board is <i>waiting its turn</i> in a multi-board sync. It gets the same accent status
    /// line as the running board — it belongs to the same run — but not the animated strip: motion is what
    /// separates "downloading now" from "queued", and a grid full of running bars would read as "everything is
    /// downloading at once" (leaving a dozen indeterminate animations ticking for a queue that is by
    /// definition idle). Mutually exclusive with <see cref="IsImporting"/> — the board that starts stops
    /// being queued.
    /// </summary>
    public static readonly StyledProperty<bool> IsQueuedProperty =
        AvaloniaProperty.Register<BoardCard, bool>(nameof(IsQueued));

    public static readonly StyledProperty<string?> ImportStatusTextProperty =
        AvaloniaProperty.Register<BoardCard, string?>(nameof(ImportStatusText));

    /// <summary>Either state occupies the meta row, so the normal metadata stands down for both.</summary>
    public static readonly DirectProperty<BoardCard, bool> HasStatusProperty =
        AvaloniaProperty.RegisterDirect<BoardCard, bool>(nameof(HasStatus), o => o.HasStatus);

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? MetaText { get => GetValue(MetaTextProperty); set => SetValue(MetaTextProperty, value); }
    public Bitmap? Thumb0 { get => GetValue(Thumb0Property); set => SetValue(Thumb0Property, value); }
    public Bitmap? Thumb1 { get => GetValue(Thumb1Property); set => SetValue(Thumb1Property, value); }
    public Bitmap? Thumb2 { get => GetValue(Thumb2Property); set => SetValue(Thumb2Property, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? EditCommand { get => GetValue(EditCommandProperty); set => SetValue(EditCommandProperty, value); }
    public bool IsImporting { get => GetValue(IsImportingProperty); set => SetValue(IsImportingProperty, value); }
    public bool IsQueued { get => GetValue(IsQueuedProperty); set => SetValue(IsQueuedProperty, value); }
    public string? ImportStatusText { get => GetValue(ImportStatusTextProperty); set => SetValue(ImportStatusTextProperty, value); }

    private bool _hasStatus;
    public bool HasStatus { get => _hasStatus; private set => SetAndRaise(HasStatusProperty, ref _hasStatus, value); }

    public BoardCard()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsImportingProperty || change.Property == IsQueuedProperty)
            HasStatus = IsImporting || IsQueued;
    }

    // True while the in-progress press is the primary (left) button — so only a primary click opens the card,
    // not a right / middle / thumb-button tap (the Tapped gesture fires for every button).
    private bool _primaryPressed;

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (!_primaryPressed) return;
        if (OpenCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    // Press feedback toggles a "pressed" class on the card (:pointerover is automatic). Handlers are on the
    // card Border itself, so the pencil (a separate control below) never triggers the card's press/scale.
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        _primaryPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (_primaryPressed) SetPressed(true);
    }
    private void OnCardReleased(object? sender, PointerReleasedEventArgs e) => SetPressed(false);
    private void OnCardExited(object? sender, PointerEventArgs e) => SetPressed(false);
    private void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e) => SetPressed(false);

    private void SetPressed(bool value)
    {
        if (value)
        {
            if (!CardRoot.Classes.Contains("pressed")) CardRoot.Classes.Add("pressed");
        }
        else
        {
            CardRoot.Classes.Remove("pressed");
        }
    }
}
