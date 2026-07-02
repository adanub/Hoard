using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hoard.Desktop.ViewModels;

// The small per-page contracts the shell chrome (the breadcrumb strip + floating bar) consumes, so the shell
// stays decoupled from concrete page view models and the chrome logic is testable against fakes.

/// <summary>A page that appears in the shell's breadcrumb trail under a human-readable title (the launcher is
/// "Projects", the Library its project name, a Board its title). Read when the page chain changes.</summary>
public interface ICrumbTitled
{
    string CrumbTitle { get; }
}

/// <summary>A page the floating bar's search field drives. <see cref="SearchText"/> is pushed on every
/// keystroke (pages that filter live act on it directly); <see cref="SubmitSearch"/> runs on Enter, for pages
/// where search navigates instead of filtering (the Library opening its results board) — a no-op elsewhere.</summary>
public interface IProvidesSearch
{
    string SearchText { get; set; }
    string SearchPlaceholder { get; }
    void SubmitSearch();
}

/// <summary>A page contributing contextual actions to the floating bar's ＋ menu (Projects → new project,
/// Library → import board, Board → sync / new folder). The list itself is fixed per page; per-action
/// visibility/enablement is live via <see cref="PlusAction"/>'s observable flags.</summary>
public interface IProvidesPlusActions
{
    IReadOnlyList<PlusAction> PlusActions { get; }
}

/// <summary>A page that can enter a fullscreen/immersive state (the Board's lightbox zoom) during which the
/// floating bar hides — chrome recedes while media fills the screen. Change-notified as "IsImmersive".</summary>
public interface IImmersivePage
{
    bool IsImmersive { get; }
}

/// <summary>One entry in the floating bar's ＋ menu: a label over one of the page's existing sheet-opening
/// commands. <see cref="IsVisible"/>/<see cref="IsEnabled"/> are observable so a page can gate an action live
/// (Sync appears only once a board's sources have loaded, and disables while an import runs).</summary>
public sealed partial class PlusAction : ObservableObject
{
    public string Label { get; }
    public ICommand Command { get; }

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isEnabled = true;

    public PlusAction(string label, ICommand command)
    {
        Label = label;
        Command = command;
    }
}
