namespace Hoard.Core.Domain;

/// <summary>
/// A board/collection of assets. Pinterest boards map to top-level collections; Pinterest
/// sections map to child collections via <see cref="ParentId"/>.
/// </summary>
public class Collection
{
    public int Id { get; set; }

    /// <summary>
    /// The board's source/original name (the first source board's name, or what the user typed when
    /// creating a local board). <see cref="DisplayName"/> overrides this for the shown name without
    /// losing the provenance — so the displayed title is <c>DisplayName ?? Name</c>.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Local rename override; null = show <see cref="Name"/>. Set when the user renames the board.</summary>
    public string? DisplayName { get; set; }

    public string SourceConnector { get; set; } = "";

    /// <summary>
    /// The <i>primary</i> source's board id — kept as a denormalised pointer to the first merged source
    /// (and for older single-source DBs). The full, authoritative list of merged sources lives in
    /// <see cref="Sources"/>. Null for a manual/local board with no source yet.
    /// </summary>
    public string? SourceBoardId { get; set; }
    public string? SourceUrl { get; set; }

    public int? ParentId { get; set; }
    public Collection? Parent { get; set; }
    public List<Collection> Children { get; } = new();

    /// <summary>
    /// For a child folder imported from a Pinterest <i>section</i>: the section's stable source id, so a
    /// re-import can re-find the same folder rather than duplicating it. Null for a top-level board or a
    /// folder the user created locally (which has no source section).
    /// </summary>
    public string? SourceSectionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Cross-device identity for archive ops (schema v8, see <c>SYNC-DESIGN.md</c>): local int ids mean
    /// nothing on another machine. Guid "N" format; minted at creation, backfilled for pre-v8 rows.
    /// Nullable only because SQLite's additive ADD COLUMN can't be NOT NULL — treat it as always present
    /// after the v8 upgrade. Declared last so a fresh model's column order matches an upgraded DB's
    /// (ADD COLUMN appends), keeping the DDL-parity tests exact.
    /// </summary>
    public string? Uid { get; set; }

    public List<CollectionItem> Items { get; } = new();

    /// <summary>The Pinterest source boards merged into this local board (one board ↔ many sources).</summary>
    public List<CollectionSource> Sources { get; } = new();
}
