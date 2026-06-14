namespace Hoard.Core.Domain;

/// <summary>
/// A board/collection of assets. Pinterest boards map to top-level collections; Pinterest
/// sections map to child collections via <see cref="ParentId"/>.
/// </summary>
public class Collection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public string SourceConnector { get; set; } = "";

    /// <summary>Stable id on the source side, e.g. the Pinterest board id. Null for manual collections.</summary>
    public string? SourceBoardId { get; set; }
    public string? SourceUrl { get; set; }

    public int? ParentId { get; set; }
    public Collection? Parent { get; set; }
    public List<Collection> Children { get; } = new();

    public DateTimeOffset CreatedAt { get; set; }

    public List<CollectionItem> Items { get; } = new();
}
