using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Core.Library;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// App-wide state for an in-flight import, shared by the Library (the importing board card) and the Board
/// screen (so it shows the same live progress and streams new pins in as they land). One instance lives on the
/// shell and is handed to both view models.
/// </summary>
public partial class ImportStatus : ObservableObject
{
    [ObservableProperty] private bool _isImporting;

    /// <summary>
    /// True while a background remote (Backup) sync is replicating the archive's files. Shared here so
    /// EVERY archive-writing entry point can refuse to overlap it — the replicator copies the very files
    /// (store blobs, the active op chapter) that an import/board-sync writes, and the interlock must be
    /// two-way: the Backup sheet already refuses while an import runs; this flag lets imports refuse
    /// while a sync runs, even from a different screen after the sheet was dismissed.
    /// </summary>
    [ObservableProperty] private bool _isRemoteSyncing;

    /// <summary>The board (collection) being imported into.</summary>
    [ObservableProperty] private int? _collectionId;

    /// <summary>The live status line, e.g. "Importing… 42 so far".</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>The most recently imported new pin, so an open Board screen can append it live.</summary>
    [ObservableProperty] private AssetView? _lastImported;

    /// <summary>Which collection <see cref="LastImported"/> was filed into — the board itself, or one of its
    /// child folders (a Pinterest section) — so the open Board screen shows it in the right place, not the root
    /// grid. Set <i>before</i> <see cref="LastImported"/> so it's current when the pin change is handled.</summary>
    [ObservableProperty] private int? _lastImportedCollectionId;

    public void Begin(int collectionId)
    {
        CollectionId = collectionId;
        Text = "Importing… starting";
        LastImported = null;
        LastImportedCollectionId = null;
        IsImporting = true;
    }

    public void End()
    {
        IsImporting = false;
        LastImported = null;
        LastImportedCollectionId = null;
    }
}
