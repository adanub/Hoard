using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A reusable in-app modal sheet: dims the page with a scrim and centres its <see cref="ContentControl.Content"/>
/// in a floating card (DESIGN.md — "Sheet / Dialog = card + ShadowMd + scrim"; mobile-ready, unlike an OS
/// dialog window). The whole host is hidden while closed. Clicking the scrim or pressing Esc raises
/// <see cref="DismissCommand"/>. Page-local for now; lift to the shell when more screens need sheets.
/// </summary>
public class SheetHost : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<SheetHost, bool>(nameof(IsOpen));

    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<SheetHost, ICommand?>(nameof(DismissCommand));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public ICommand? DismissCommand
    {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // The scrim (the dimmed backdrop) is the click-away target; the card sits above it and swallows clicks.
        if (e.NameScope.Find<Control>("PART_Scrim") is { } scrim)
            scrim.PointerPressed += (_, _) => Dismiss();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsOpen && e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void Dismiss()
    {
        if (DismissCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }
}
