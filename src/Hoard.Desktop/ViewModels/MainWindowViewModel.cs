using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Projects;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// Shell that hosts one page at a time: the project launcher, then the in-project library. Owns the
/// navigation between them so the two page view models stay independent.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IngestService _ingest;
    private readonly LibraryService _library;
    private readonly CurationService _curation;
    private readonly ProjectManager _projects;
    private readonly ProjectDbContextFactory _dbFactory;

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

    [ObservableProperty] private ViewModelBase? _currentPage;

    private void ShowLauncher()
        => CurrentPage = new ProjectLauncherViewModel(_projects, _dbFactory, onProjectOpened: ShowLibrary);

    private void ShowLibrary()
        => CurrentPage = new LibraryViewModel(_ingest, _library, _curation, _projects, requestSwitchProject: ShowLauncher);
}
