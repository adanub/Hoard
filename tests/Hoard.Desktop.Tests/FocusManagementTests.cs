using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Hoard.Desktop.Infrastructure;
using Xunit;

namespace Hoard.Desktop.Tests;

public class FocusManagementTests
{
    // ── Policy (pure decision) ─────────────────────────────────────────────────

    [Fact]
    public void Clears_when_the_press_path_has_nothing_focusable()
    {
        // Clicked the host directly (empty path) or only non-focusable chrome → clear.
        Assert.True(FocusManagement.ShouldClearFocus(Array.Empty<(bool, bool)>()));
        Assert.True(FocusManagement.ShouldClearFocus(new[] { (false, true), (false, true) }));
    }

    [Fact]
    public void Does_not_clear_when_a_focusable_enabled_control_is_in_the_path()
    {
        Assert.False(FocusManagement.ShouldClearFocus(new[] { (true, true) }));
        Assert.False(FocusManagement.ShouldClearFocus(new[] { (false, true), (true, true), (false, true) }));
    }

    [Fact]
    public void A_disabled_control_does_not_take_focus_so_it_still_clears()
    {
        Assert.True(FocusManagement.ShouldClearFocus(new[] { (true, false) })); // focusable but disabled
    }

    // ── Enforcement (architecture test) ────────────────────────────────────────

    [Fact]
    public void Focus_clearing_primitives_live_only_in_FocusManagement()
    {
        // Guards centralisation: the low-level focus-policy primitives must not be re-implemented in views or
        // other infrastructure. (Plain control.Focus() to focus a specific element is fine and not flagged.)
        var sourceDir = TryLocateDesktopSource();
        if (sourceDir is null)
            return; // source tree not co-located with this run (packaged/relocated/CI artifact) — nothing to scan

        string[] primitives = { "IsKeyboardFocusWithin", "IFocusManager" };

        var sources = Directory
            .EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/"))
            .ToList();

        // Guard against a mis-resolved path silently passing the scan (vacuous success).
        Assert.Contains(sources, f => Path.GetFileName(f) == "FocusManagement.cs");

        var offenders = sources
            .Where(f => Path.GetFileName(f) != "FocusManagement.cs")
            // Scan code only — a primitive mentioned in a comment/doc string is not an offender.
            .Where(f => { var code = StripComments(File.ReadAllText(f)); return primitives.Any(code.Contains); })
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Focus-clearing must go through FocusManagement. Offending files: " + string.Join(", ", offenders));
    }

    /// <summary>Strip // line and /* */ block comments so identifiers mentioned in prose aren't flagged.</summary>
    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^\n]*", " ");
        return code;
    }

    /// <summary>Locate src/Hoard.Desktop from the compile-time source path, or null if the tree isn't present.</summary>
    private static string? TryLocateDesktopSource([CallerFilePath] string thisFile = "")
    {
        // …/tests/Hoard.Desktop.Tests/FocusManagementTests.cs → walk up to the repo, then into src/Hoard.Desktop.
        var dir = Path.GetDirectoryName(thisFile) is { } d ? new DirectoryInfo(d) : null;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        if (dir is null) return null;
        var src = Path.Combine(dir.FullName, "src", "Hoard.Desktop");
        return Directory.Exists(src) ? src : null;
    }
}
