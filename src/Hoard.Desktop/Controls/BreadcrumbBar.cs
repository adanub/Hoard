using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Controls;

/// <summary>
/// The shell's thin breadcrumb strip: the navigation trail (Projects › project › board › folder …) rendered
/// as clickable ancestor crumbs around a plain current-page crumb. Fitting is delegated to the pure
/// <see cref="BreadcrumbTrimmer"/> — when the trail overflows, the ellipsis eats from the BASE end
/// ("…est Backup › Terrain Ideas › Buildings") so the current page always keeps its name. Children are
/// rebuilt only when the trail or the available width actually changes (guarded, so the mid-measure rebuild
/// converges instead of looping).
/// </summary>
public sealed class BreadcrumbBar : Panel
{
    public static readonly StyledProperty<IReadOnlyList<Crumb>?> TrailProperty =
        AvaloniaProperty.Register<BreadcrumbBar, IReadOnlyList<Crumb>?>(nameof(Trail));

    public static readonly StyledProperty<ICommand?> NavigateCommandProperty =
        AvaloniaProperty.Register<BreadcrumbBar, ICommand?>(nameof(NavigateCommand));

    /// <summary>The crumb trail, root first (bound to <c>ShellChromeViewModel.Crumbs</c>).</summary>
    public IReadOnlyList<Crumb>? Trail
    {
        get => GetValue(TrailProperty);
        set => SetValue(TrailProperty, value);
    }

    /// <summary>Invoked with the clicked ancestor's <see cref="Crumb"/> (the current crumb isn't clickable).</summary>
    public ICommand? NavigateCommand
    {
        get => GetValue(NavigateCommandProperty);
        set => SetValue(NavigateCommandProperty, value);
    }

    // Control-internal metrics (code constants, like the MasonryPacker's): crumb inner padding, separator
    // padding, and a little slack subtracted from the fit width for button template chrome.
    private const double CrumbHPad = 7;
    private const double SepHPad = 3;
    private const double FitSlack = 10;
    private const string SeparatorGlyph = "›";

    private IReadOnlyList<Crumb>? _builtTrail;
    private double _builtWidth = -1;
    private BreadcrumbSegment[]? _builtFit; // what the children currently render — skip no-op rebuilds

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrailProperty || change.Property == NavigateCommandProperty)
        {
            _builtWidth = -1;  // force a re-fit on the next measure
            _builtFit = null;  // and a real child rebuild (the buttons capture NavigateCommand)
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        RebuildIfNeeded(availableSize.Width);
        double w = 0, h = 0;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            w += child.DesiredSize.Width;
            h = Math.Max(h, child.DesiredSize.Height);
        }
        return new Size(Math.Min(w, availableSize.Width), Math.Min(h, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            child.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width;
        }
        return finalSize;
    }

    // Re-run the trimmer + rebuild the crumb controls when the trail or the width changed. Called from
    // MeasureOverride (the width constraint lives there); the (trail, width) guard makes the re-measure that
    // a child mutation triggers a no-op, so layout converges.
    private void RebuildIfNeeded(double width)
    {
        var trail = Trail;
        if (ReferenceEquals(trail, _builtTrail) && Math.Abs(width - _builtWidth) < 0.5) return;
        _builtTrail = trail;
        _builtWidth = width;

        if (trail is null || trail.Count == 0)
        {
            _builtFit = null;
            Children.Clear();
            return;
        }

        var fontSize = TextElement.GetFontSize(this);
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        double Text(string t) => TextWidth(t, typeface, fontSize);

        var titles = trail.Select(c => c.Title).ToArray();
        var fitWidth = double.IsFinite(width) ? Math.Max(0, width - FitSlack) : double.PositiveInfinity;
        var fit = BreadcrumbTrimmer.Fit(titles, fitWidth, Text(SeparatorGlyph) + SepHPad * 2,
            t => Text(t) + CrumbHPad * 2);

        // A width change that didn't change the trim (the common everything-still-fits resize tick) keeps the
        // existing children — clearing and rebuilding them per layout pass was visible resize churn.
        if (_builtFit is not null && Children.Count > 0 && fit.SequenceEqual(_builtFit)) return;
        _builtFit = fit.ToArray();

        Children.Clear();
        for (var i = 0; i < fit.Count; i++)
        {
            if (i > 0) Children.Add(NewSeparator(fontSize));
            var segment = fit[i];
            Children.Add(segment.Index == trail.Count - 1
                ? NewCurrent(segment.Text, fontSize)
                : NewLink(segment.Text, trail[segment.Index], fontSize));
        }
    }

    private static double TextWidth(string text, Typeface typeface, double fontSize)
        => new TextLayout(text, typeface, fontSize, null).WidthIncludingTrailingWhitespace;

    // An ancestor crumb: a small ghost button (hover lifts it into the pill, like every tappable) that
    // navigates back to its page. The label is a TextBlock so the muted style applies; the explicit FontSize
    // keeps the rendered width equal to the measured one.
    private Control NewLink(string text, Crumb crumb, double fontSize)
    {
        var label = new TextBlock { Text = text, FontSize = fontSize };
        label.Classes.Add("muted");
        label.FontSize = fontSize; // local value — the muted class must not re-size it away from the measure

        var button = new Button
        {
            Content = label,
            Padding = new Thickness(CrumbHPad, 0),
            MinWidth = 0,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Command = NavigateCommand,
            CommandParameter = crumb,
        };
        if (this.TryFindResource("HoardButton", ActualThemeVariant, out var theme) && theme is ControlTheme ct)
            button.Theme = ct;
        button.Classes.Add("ghost");
        return button;
    }

    // The current page's crumb: plain text in the primary foreground — present, not tappable.
    private Control NewCurrent(string text, double fontSize)
        => new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Padding = new Thickness(CrumbHPad, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

    private Control NewSeparator(double fontSize)
    {
        var sep = new TextBlock
        {
            Text = SeparatorGlyph,
            FontSize = fontSize,
            Padding = new Thickness(SepHPad, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        sep.Classes.Add("muted");
        sep.FontSize = fontSize;
        return sep;
    }
}
