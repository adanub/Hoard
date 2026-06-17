using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A project board card: a 3-up collage cover (built from the project's thumbnails, picture-icon fallback)
/// over the project name, a metadata line, and <b>Open</b> (primary) + <b>Edit</b> (secondary) buttons, both
/// large. Matches the gallery card; the buttons surface as <see cref="OpenCommand"/> / <see cref="EditCommand"/>.
/// </summary>
public partial class ProjectCard : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ProjectCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MetaTextProperty =
        AvaloniaProperty.Register<ProjectCard, string?>(nameof(MetaText));

    public static readonly StyledProperty<Bitmap?> Thumb0Property =
        AvaloniaProperty.Register<ProjectCard, Bitmap?>(nameof(Thumb0));

    public static readonly StyledProperty<Bitmap?> Thumb1Property =
        AvaloniaProperty.Register<ProjectCard, Bitmap?>(nameof(Thumb1));

    public static readonly StyledProperty<Bitmap?> Thumb2Property =
        AvaloniaProperty.Register<ProjectCard, Bitmap?>(nameof(Thumb2));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<ProjectCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<ProjectCard, ICommand?>(nameof(EditCommand));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? MetaText { get => GetValue(MetaTextProperty); set => SetValue(MetaTextProperty, value); }
    public Bitmap? Thumb0 { get => GetValue(Thumb0Property); set => SetValue(Thumb0Property, value); }
    public Bitmap? Thumb1 { get => GetValue(Thumb1Property); set => SetValue(Thumb1Property, value); }
    public Bitmap? Thumb2 { get => GetValue(Thumb2Property); set => SetValue(Thumb2Property, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? EditCommand { get => GetValue(EditCommandProperty); set => SetValue(EditCommandProperty, value); }

    public ProjectCard()
    {
        InitializeComponent();
    }
}
