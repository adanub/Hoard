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

    public static readonly StyledProperty<string?> ImportStatusTextProperty =
        AvaloniaProperty.Register<BoardCard, string?>(nameof(ImportStatusText));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? MetaText { get => GetValue(MetaTextProperty); set => SetValue(MetaTextProperty, value); }
    public Bitmap? Thumb0 { get => GetValue(Thumb0Property); set => SetValue(Thumb0Property, value); }
    public Bitmap? Thumb1 { get => GetValue(Thumb1Property); set => SetValue(Thumb1Property, value); }
    public Bitmap? Thumb2 { get => GetValue(Thumb2Property); set => SetValue(Thumb2Property, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? EditCommand { get => GetValue(EditCommandProperty); set => SetValue(EditCommandProperty, value); }
    public bool IsImporting { get => GetValue(IsImportingProperty); set => SetValue(IsImportingProperty, value); }
    public string? ImportStatusText { get => GetValue(ImportStatusTextProperty); set => SetValue(ImportStatusTextProperty, value); }

    public BoardCard()
    {
        InitializeComponent();
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
