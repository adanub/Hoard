using Hoard.Core.Library;

namespace Hoard.Core.Ingest;

public enum IngestPhase
{
    Starting,
    Downloading,
    Storing,
    Done,
}

/// <summary>
/// A progress tick emitted during an import, suitable for binding to a UI. When a brand-new asset was
/// just imported, <see cref="ImportedAsset"/> carries it so the UI can append it to the grid live.
/// </summary>
public sealed record IngestProgress(
    IngestPhase Phase, int Processed, int Total, string? Message, AssetView? ImportedAsset = null);

/// <summary>Summary returned when an import finishes.</summary>
public sealed record IngestResult(int TotalItems, int NewAssets, int DuplicateAssets, int CollectionsTouched);
