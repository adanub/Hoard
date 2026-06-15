using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A Lucide-style stroked icon. Renders a geometry drawn on the 24×24 Lucide grid with the stroke = the
/// control's <see cref="TemplatedControl.Foreground"/>, scaled uniformly to the control size (default 20px)
/// so the stroke weight stays consistent. Geometries live in <c>Theme/Icons.axaml</c> keyed <c>Icon.&lt;name&gt;</c>.
/// </summary>
public class Icon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    /// <summary>The icon outline, on the 24×24 Lucide coordinate grid.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
