using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// Conventions that hold over the XAML sources themselves. There is no view model to assert against — the
/// rule is about what the markup says — so these parse the repo's <c>.axaml</c> files directly. Source-level
/// guards like this are for rules that are mechanical but have no runtime seam; anything with real logic
/// belongs in a pure, testable type instead (<c>MasonryPacker</c>, <c>BreadcrumbTrimmer</c>, <c>UpdatePolicy</c>).
/// </summary>
public class AxamlConventionTests
{
    /// <summary>
    /// <c>ScrollViewer.Padding</c> is broken in Avalonia 12 and must never be used to inset scrolling content
    /// (see the rule in CLAUDE.md): <c>ScrollContentPresenter</c> ignores Padding when it measures, but Arrange
    /// deflates the child's rect by it and the scroll Extent is taken from the child's arranged bounds — so the
    /// padded region costs the extent twice its own size and simply cannot be scrolled to. This shipped once:
    /// the board's masonry lost its bottom row (and 212px of reach) under the floating bar.
    ///
    /// The fix is always a Margin on the content, which <c>ComputeExtent</c> inflates by. This test exists
    /// because the rule is invisible at review time — <c>Padding</c> on a <c>ScrollViewer</c> is the obvious
    /// thing to write, and it looks right until you scroll to the bottom.
    /// </summary>
    [Fact]
    public void No_ScrollViewer_sets_Padding()
    {
        var files = AxamlFiles().ToList();
        // A source-scanning test that finds no sources passes vacuously and protects nothing — so prove the
        // sweep actually reached the markup before trusting a green result.
        Assert.True(files.Count > 20, $"Expected to scan the app's .axaml files, found {files.Count}.");

        var offenders = new List<string>();

        foreach (var (path, relative) in files)
        {
            // The ONE legitimate use: the stock TextBox template forwards the TextBox's own Padding to its
            // inner ScrollViewer, exactly as Avalonia's own template does. It inherits the same caveat.
            if (relative == "src/Hoard.Desktop/Theme/Controls/Input.axaml") continue;

            var root = XDocument.Load(path).Root;
            if (root is null) continue;

            foreach (var element in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ScrollViewer"))
            {
                // Both spellings reach the same property: the attribute form and the property-element form.
                if (element.Attributes().Any(a => a.Name.LocalName == "Padding")
                    || element.Elements().Any(e => e.Name.LocalName == "ScrollViewer.Padding"))
                    offenders.Add($"{relative}: <ScrollViewer> sets Padding directly");
            }

            // ...and so does a style/theme setter aimed at ScrollViewer, which no amount of reading the views
            // would reveal.
            foreach (var setter in root.DescendantsAndSelf().Where(IsPaddingSetter))
                if (setter.Ancestors().Any(TargetsScrollViewer))
                    offenders.Add($"{relative}: a Style/ControlTheme setter applies Padding to a ScrollViewer");
        }

        Assert.True(offenders.Count == 0,
            "ScrollViewer.Padding does not participate in the scroll extent (Avalonia 12), so the padded "
            + "region cannot be scrolled to. Inset the CONTENT with a Margin instead — see the rule in "
            + "CLAUDE.md and the comment in Views/BoardView.axaml.\n  " + string.Join("\n  ", offenders));
    }

    private static bool IsPaddingSetter(XElement element)
        => element.Name.LocalName == "Setter"
        && element.Attributes().Any(a => a.Name.LocalName == "Property" && a.Value == "Padding");

    private static bool TargetsScrollViewer(XElement element)
        => element.Name.LocalName is "Style" or "ControlTheme"
        && element.Attributes().Any(a =>
            a.Name.LocalName is "Selector" or "TargetType" && a.Value.Contains("ScrollViewer", StringComparison.Ordinal));

    // Every .axaml under the desktop head, as (absolute path, repo-relative '/'-separated path). bin/obj are
    // excluded: they hold copies, which would report the same offence twice under a path nobody can fix.
    private static IEnumerable<(string Path, string Relative)> AxamlFiles()
    {
        var root = RepoRoot();
        var source = Path.Combine(root, "src", "Hoard.Desktop");

        return Directory
            .EnumerateFiles(source, "*.axaml", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Select(p => (p, Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(t => t.Item2, StringComparer.Ordinal);
    }

    // The compiler bakes in this file's path, so the repo is found without a brittle "../../.." from the test
    // binary's output directory (which differs between configurations and between `dotnet test` and an IDE).
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hoard.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException(
            $"Couldn't find Hoard.slnx above '{thisFile}' — this test reads the repo's .axaml sources, so it "
            + "must run from a checkout rather than against a copied-out binary.");
    }
}
