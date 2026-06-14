using Hoard.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Metadata;

public class HoardDbContext : DbContext
{
    public HoardDbContext(DbContextOptions<HoardDbContext> options) : base(options) { }

    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AssetTag> AssetTags => Set<AssetTag>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Asset>(e =>
        {
            e.HasIndex(a => a.Sha256).IsUnique();
            e.HasIndex(a => new { a.SourceConnector, a.SourceId });
            e.Property(a => a.Sha256).IsRequired();
            e.Property(a => a.RelativePath).IsRequired();
        });

        b.Entity<Collection>(e =>
        {
            e.HasIndex(c => new { c.SourceConnector, c.SourceBoardId });
            e.HasOne(c => c.Parent)
                .WithMany(c => c.Children)
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<CollectionItem>(e =>
        {
            // An asset appears at most once per collection.
            e.HasIndex(ci => new { ci.CollectionId, ci.AssetId }).IsUnique();
            e.HasOne(ci => ci.Collection)
                .WithMany(c => c.Items)
                .HasForeignKey(ci => ci.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ci => ci.Asset)
                .WithMany(a => a.CollectionItems)
                .HasForeignKey(ci => ci.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Tag>(e => e.HasIndex(t => t.Name).IsUnique());

        b.Entity<AssetTag>(e =>
        {
            e.HasKey(at => new { at.AssetId, at.TagId });
            e.HasOne(at => at.Asset)
                .WithMany(a => a.AssetTags)
                .HasForeignKey(at => at.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(at => at.Tag)
                .WithMany(t => t.AssetTags)
                .HasForeignKey(at => at.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
