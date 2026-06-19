namespace Hoard.Core.Domain;

/// <summary>Join row placing an <see cref="Asset"/> in a <see cref="Collection"/>, preserving order.</summary>
public class CollectionItem
{
    public int Id { get; set; }

    public int CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;

    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    /// <summary>
    /// Which merged source (<see cref="CollectionSource"/>) this pin was imported from, so a source can be
    /// un-merged together with its own images. Null for links made before per-pin provenance (and cleared to
    /// null if that source is later removed without its images).
    /// </summary>
    public int? CollectionSourceId { get; set; }
    public CollectionSource? CollectionSource { get; set; }

    /// <summary>Position within the board, when the source exposes ordering.</summary>
    public int SortOrder { get; set; }

    /// <summary>Per-pin note/description, which can differ from the asset's own description.</summary>
    public string? Note { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
