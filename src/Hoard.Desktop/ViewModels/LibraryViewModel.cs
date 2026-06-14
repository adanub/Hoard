using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Connectors;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Projects;
using Hoard.Desktop.Services;
using Hoard.Ingest.GalleryDl;

namespace Hoard.Desktop.ViewModels;

/// <summary>The in-project UI: import, browse boards, and view a project's images.</summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly IngestService _ingest;
    private readonly LibraryService _library;
    private readonly ProjectManager _projects;
    private readonly Action _requestSwitchProject;
    private readonly ThumbnailCache? _thumbnails;
    private CancellationTokenSource? _searchDebounce;

    // GIFs the user has clicked keep autoplaying in the grid; bounded (LRU) so memory stays sane.
    private const int MaxPlayingGifs = 12;
    private readonly LinkedList<AssetTileViewModel> _playing = new();

    public LibraryViewModel(
        IngestService ingest, LibraryService library, ProjectManager projects, Action requestSwitchProject)
    {
        _ingest = ingest;
        _library = library;
        _projects = projects;
        _requestSwitchProject = requestSwitchProject;
        _thumbnails = projects?.Current is { } p ? new ThumbnailCache(p.ThumbnailsRoot) : null;
        _ = RefreshAsync();
    }

    // Design-time constructor for the XAML previewer.
    public LibraryViewModel() : this(null!, null!, null!, () => { }) { }

    [ObservableProperty] private string _boardUrl = "";
    [ObservableProperty] private string _cookiesBrowser = BrowserCookies.None;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMaximum = 1;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private AssetTileViewModel? _selectedAsset;
    [ObservableProperty] private AssetDetailViewModel? _details;

    // Load full metadata for the detail panel whenever the selected tile changes.
    partial void OnSelectedAssetChanged(AssetTileViewModel? value) => _ = LoadDetailsAsync(value);

    private async Task LoadDetailsAsync(AssetTileViewModel? tile)
    {
        if (_library is null || tile is null) { Details = null; return; }
        var detail = await _library.GetAssetDetailAsync(tile.Model.Id);
        // Ignore if selection changed again while we were loading.
        if (ReferenceEquals(SelectedAsset, tile))
            Details = detail is null ? null : new AssetDetailViewModel(detail);
    }

    [RelayCommand]
    private void CloseDetails() => SelectedAsset = null;

    /// <summary>Clicking a tile selects it (opens the detail panel) and, for a GIF, starts it autoplaying
    /// in the grid. It keeps playing after the detail panel is closed, until pushed out by newer plays.</summary>
    public void ActivateTile(AssetTileViewModel tile)
    {
        SelectedAsset = tile;
        if (!tile.IsGif) return;

        var existing = _playing.Find(tile);
        if (existing is not null) { _playing.Remove(existing); _playing.AddFirst(existing); return; }

        tile.IsPlaying = true;
        _playing.AddFirst(tile);
        while (_playing.Count > MaxPlayingGifs)
        {
            var oldest = _playing.Last!.Value;
            _playing.RemoveLast();
            oldest.IsPlaying = false; // stop the least-recently-played GIF
        }
    }

    /// <summary>
    /// Manually unload a playing GIF: stop it and (if it's the selected one) close the detail panel.
    /// Both drop their cache leases, and the refcounted cache frees the frames once the last lease goes.
    /// </summary>
    public void UnloadGif(AssetTileViewModel tile)
    {
        tile.IsPlaying = false; // tile's control releases its lease + stops the timer
        var node = _playing.Find(tile);
        if (node is not null) _playing.Remove(node);

        // If the detail panel is showing this GIF it also holds a lease — close it so that one drops too.
        if (ReferenceEquals(SelectedAsset, tile)) SelectedAsset = null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryTitle))]
    private CollectionView? _selectedCollection;

    [ObservableProperty] private string _searchQuery = "";

    // Reload after a short pause so we don't query on every keystroke.
    partial void OnSearchQueryChanged(string value) => _ = DebouncedSearchAsync();

    private async Task DebouncedSearchAsync()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _searchDebounce, cts)?.Cancel();
        try { await Task.Delay(220, cts.Token); }
        catch (TaskCanceledException) { return; }
        await LoadAssetsAsync();
    }

    public System.Collections.Generic.IReadOnlyList<string> CookieBrowsers { get; } = BrowserCookies.Choices;

    public ObservableCollection<CollectionView> Collections { get; } = new();
    public ObservableCollection<AssetTileViewModel> Assets { get; } = new();

    public string ProjectName => _projects?.Current?.Name ?? "";
    public string? ProjectPath => _projects?.Current?.Root;
    public string LibraryTitle => SelectedCollection is null ? "All images" : SelectedCollection.Name;

    [RelayCommand]
    private void SwitchProject() => _requestSwitchProject();

    // ── Import ──────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (_ingest is null || _projects?.Current is null) return;
        IsImporting = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        StatusText = "Starting…";

        var url = NormalizeUrl(BoardUrl);

        var cookies = BrowserCookies.Resolve(CookiesBrowser);
        if (!cookies.Found)
        {
            StatusText = cookies.Error!;
            IsImporting = false;
            ProgressIndeterminate = false;
            return;
        }
        var options = new ConnectorOptions
        {
            CookiesFromBrowser = cookies.Spec,
            // Skip re-downloading pins already saved in this project on previous imports.
            DownloadArchivePath = _projects.Current.DownloadArchivePath,
        };

        var transcript = new StringBuilder();
        transcript.AppendLine($"Import {DateTime.Now:O}");
        transcript.AppendLine($"Project: {ProjectName} ({ProjectPath})");
        transcript.AppendLine($"URL: {url}");
        transcript.AppendLine($"Cookies: {CookiesBrowser}");
        transcript.AppendLine(new string('-', 60));

        var progress = new Progress<IngestProgress>(p =>
        {
            if (p.Message is not null) transcript.AppendLine($"[{p.Phase}] {p.Message}");
            StatusText = p.Message ?? p.Phase.ToString();
            if (p.Total > 0)
            {
                ProgressIndeterminate = false;
                ProgressMaximum = p.Total;
                ProgressValue = p.Processed;
            }

            // Show each newly imported image immediately, but only on the unfiltered "All images" view
            // (a streamed asset may not match an active board/search filter); the final RefreshAsync
            // reconciles ordering, counts, and the board list.
            if (p.ImportedAsset is { } asset && SelectedCollection is null && string.IsNullOrWhiteSpace(SearchQuery))
                Assets.Insert(0, new AssetTileViewModel(asset, _thumbnails));
        });

        try
        {
            var result = await _ingest.ImportAsync(url, options, progress);
            transcript.AppendLine($"RESULT: {result.NewAssets} new, {result.DuplicateAssets} duplicate, {result.CollectionsTouched} board(s).");
            StatusText = result.TotalItems == 0
                ? "Already up to date — nothing new to download."
                : $"Done — {result.NewAssets} new, {result.DuplicateAssets} already-had, {result.CollectionsTouched} board(s).";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            transcript.AppendLine("EXCEPTION:");
            transcript.AppendLine(ex.ToString());
            var logPath = WriteImportLog(transcript);
            StatusText = $"Import failed: {ex.Message}".Trim();
            if (logPath is not null) StatusText += $"  (log: {logPath})";
            return;
        }
        finally
        {
            IsImporting = false;
            ProgressIndeterminate = false;
        }

        WriteImportLog(transcript);
    }

    private string? WriteImportLog(StringBuilder transcript)
    {
        try
        {
            var logsRoot = _projects.Current?.LogsRoot;
            if (logsRoot is null) return null;
            var path = Path.Combine(logsRoot, $"import-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, transcript.ToString());
            return path;
        }
        catch
        {
            return null; // logging must never break an import
        }
    }

    private bool CanImport() => !IsImporting && Uri.IsWellFormedUriString(NormalizeUrl(BoardUrl), UriKind.Absolute);

    partial void OnBoardUrlChanged(string value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnIsImportingChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();

    /// <summary>Accept "pinterest.com/…" by prepending https:// when the user omits the scheme.</summary>
    private static string NormalizeUrl(string raw)
    {
        var url = raw.Trim();
        if (url.Length == 0) return url;
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        return url;
    }

    // ── Library browsing ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowAll() => SelectedCollection = null;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_library is null || _projects?.Current is null) return;
        var previouslySelectedId = SelectedCollection?.Id;

        Collections.Clear();
        foreach (var c in await _library.GetCollectionsAsync())
            Collections.Add(c);

        SelectedCollection = previouslySelectedId is int id
            ? Collections.FirstOrDefault(c => c.Id == id)
            : null;

        await LoadAssetsAsync();
    }

    partial void OnSelectedCollectionChanged(CollectionView? value) => _ = LoadAssetsAsync();

    private async Task LoadAssetsAsync()
    {
        if (_library is null || _projects?.Current is null) return;
        var search = SearchQuery;
        var views = await _library.GetAssetsAsync(SelectedCollection?.Id, search);
        // Tiles are being recreated: close the detail panel and forget what was playing so their GIF
        // cache leases are dropped and the frames are freed (memory tracks what's actually on screen).
        SelectedAsset = null;
        _playing.Clear();
        Assets.Clear();
        foreach (var v in views)
            Assets.Add(new AssetTileViewModel(v, _thumbnails));
        StatusText = string.IsNullOrWhiteSpace(search)
            ? $"{Assets.Count} image(s) in “{LibraryTitle}”."
            : $"{Assets.Count} result(s) for “{search.Trim()}” in “{LibraryTitle}”.";
    }
}
