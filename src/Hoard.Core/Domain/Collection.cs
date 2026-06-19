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

    public DateTimeOffset CreatedAt { get; set; }

    public List<CollectionItem> Items { get; } = new();

    /// <summary>The Pinterest source boards merged into this local board (one board ↔ many sources).</summary>
    public List<CollectionSource> Sources { get; } = new();
}
