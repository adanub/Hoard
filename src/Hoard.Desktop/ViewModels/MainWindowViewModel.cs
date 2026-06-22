using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Projects;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// Shell that hosts one page at a time via a <see cref="NavigationService"/> back-stack: the project
/// launcher (root), then the in-project library, and (later slices) board + image-detail pushed above it.
/// Acts as the page factory so the page view models stay independent and don't construct each other.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IngestService _ingest;
    private readonly LibraryService _library;
    private readonly CurationService _curation;
    private readonly ProjectManager _projects;
    private readonly ProjectDbContextFactory _dbFactory;

    // One thumbnail cache per opened project, shared by the Library (board covers) and Board (tiles) screens.
    private ThumbnailCache? _thumbnails;

    /// <summary>The shell binds <c>Navigation.Current</c> to show the active page.</summary>
    public NavigationService Navigation { get; } = new();

    /// <summary>App-wide transient toasts; the shell overlays a <c>ToastHost</c> bound to this.</summary>
    public ToastService ToastService { get; } = new();

    /// <summary>Shared in-flight import state, so the Library card and the Board screen show the same progress.</summary>
    public ImportStatus ImportStatus { get; } = new();

    public MainWindowViewModel(
        IngestService ingest, LibraryService library, CurationService curation,
        ProjectManager projects, ProjectDbContextFactory dbFactory)
    {
        _ingest = ingest;
        _library = library;
        _curation = curation;
        _projects = projects;
        _dbFactory = dbFactory;
        if (projects is not null) ShowLauncher(); // skip in the design-time (null) ctor
    }

    // Design-time constructor for the XAML previewer.
    public MainWindowViewModel() : this(null!, null!, null!, null!, null!) { }

    // The launcher is always the root, so opening/switching a project resets the stack to a fresh launcher
    // (reloading the recents list). Opening a project pushes the library above it.
    private void ShowLauncher()
        => Navigation.Reset(new ProjectLauncherViewModel(_projects, _dbFactory, ToastService, onProjectOpened: ShowLibrary));

    // Opening a project pushes the Library (board grid) above the launcher; opening a board pushes the Board
    // screen above the Library; the back chevrons pop back down the stack. The Library is part of the normal
    // back/forward stack — its back pops to the (refreshed) launcher, and the same factory is the forward-rebuild
    // thunk, so the mouse forward button can re-enter a project after backing out to Projects.
    private void ShowLibrary()
    {
        LibraryViewModel Create()
        {
            _thumbnails = _projects.Current is { } p ? new ThumbnailCache(p.ThumbnailsRoot) : null;
            return new LibraryViewModel(
                _ingest, _library, _curation, _projects, _thumbnails, ToastService, ImportStatus,
                openBoard: ShowBoard, requestBack: Navigation.Pop);
        }
        // The forward-rebuild thunk returns null if the project was closed/deleted while backed out, so GoForward
        // drops the dead entry instead of rebuilding a blank Library for a project that no longer exists.
        Navigation.Push(Create(), () => _projects.Current is null ? null : Create());
    }

    // Drilling into a child folder pushes another Board screen for that folder (openBoard = ShowBoard), so
    // nesting works to any depth via the back-stack. The same factory is the forward-rebuild thunk, so the mouse
    // forward button can return to a board that was backed out of (rebuilt fresh — the popped one was disposed).
    private void ShowBoard(BoardTarget target)
    {
        BoardViewModel Create() => new(
            _library, _curation, _ingest, _thumbnails, ToastService, ImportStatus, _projects,
            target, requestBack: Navigation.Pop, openBoard: ShowBoard);
        // Null thunk when the project is gone, so forward drops the dead entry instead of rebuilding a board
        // against a closed/deleted project.
        Navigation.Push(Create(), () => _projects.Current is null ? null : Create());
    }

    /// <summary>Mouse back button (XButton1): do exactly what the current page's own ← button does — a Board pops,
    /// the Library switches project — so the gesture matches the on-screen control. No-op on a page with no back
    /// (the launcher).</summary>
    public void NavigateBack()
    {
        if (Navigation.Current is IHasBack page) page.BackCommand.Execute(null);
    }

    /// <summary>Mouse forward button (XButton2): return to a board that was backed out of.</summary>
    public void NavigateForward() => Navigation.GoForward();
}
