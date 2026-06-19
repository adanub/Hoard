using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Hoard.Desktop.Controls;

/// <summary>
/// The leading "+ New project" action card in the Projects grid. A raised, clickable tile (convex bevel, drop
/// shadow, expand-on-hover / shrink-on-press) sized to the <see cref="ProjectCard"/>'s footprint, with the +
/// and label centred inside — so it sits flush beside the (inset) project cards but reads clearly as an
/// action. The board grid uses <see cref="NewCard"/> instead (which mirrors the board card's silhouette).
/// The whole tile is the button; tapping it runs <see cref="Command"/>.
/// </summary>
public partial class NewProjectCard : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<NewProjectCard, string?>(nameof(Label), "New project");

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<NewProjectCard, ICommand?>(nameof(Command));

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public NewProjectCard()
    {
        InitializeComponent();
    }

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (Command is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    // Press feedback toggles a "pressed" class on the card (:pointerover is automatic).
    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => SetPressed(true);
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
