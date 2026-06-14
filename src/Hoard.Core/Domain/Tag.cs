namespace Hoard.Core.Domain;

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<AssetTag> AssetTags { get; } = new();
}

public class AssetTag
{
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
