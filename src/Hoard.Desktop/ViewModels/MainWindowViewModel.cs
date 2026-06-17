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

    /// <summary>The shell binds <c>Navigation.Current</c> to show the active page.</summary>
    public NavigationService Navigation { get; } = new();

    /// <summary>App-wide transient toasts; the shell overlays a <c>ToastHost</c> bound to this.</summary>
    public ToastService ToastService { get; } = new();

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

    private void ShowLibrary()
        => Navigation.Push(new LibraryViewModel(_ingest, _library, _curation, _projects, requestSwitchProject: ShowLauncher));
}
