using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Hoard.Desktop.Controls;

/// <summary>
/// The leading "+ New …" action card in a grid of cards (new project, new board). Mirrors the
/// <see cref="BoardCard"/> silhouette — a raised, clickable cover well (here holding a centred +) with a
/// label row beneath — so it sits flush beside the data cards, with the same expand-on-hover / shrink-on-press
/// feedback. The whole tile is the button; tapping it runs <see cref="Command"/>.
/// </summary>
public partial class NewCard : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<NewCard, string?>(nameof(Label), "New");

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<NewCard, ICommand?>(nameof(Command));

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public NewCard()
    {
        InitializeComponent();
    }

    // True while the in-progress press is the primary (left) button — so only a primary click runs the command,
    // not a right / middle / thumb-button tap (the Tapped gesture fires for every button).
    private bool _primaryPressed;

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (!_primaryPressed) return;
        if (Command is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    // Press feedback toggles a "pressed" class on the card (:pointerover is automatic).
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
