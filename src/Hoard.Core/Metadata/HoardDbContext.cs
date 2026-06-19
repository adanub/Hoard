using Hoard.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Metadata;

public class HoardDbContext : DbContext
{
    public HoardDbContext(DbContextOptions<HoardDbContext> options) : base(options) { }

    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionSource> CollectionSources => Set<CollectionSource>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AssetTag> AssetTags => Set<AssetTag>();
    public DbSet<SyncOp> SyncOps => Set<SyncOp>();

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

        b.Entity<CollectionSource>(e =>
        {
            // A given source board is merged into a local board at most once.
            e.HasIndex(s => new { s.CollectionId, s.SourceConnector, s.SourceBoardId }).IsUnique();
            e.Property(s => s.SourceUrl).IsRequired();
            e.HasOne(s => s.Collection)
                .WithMany(c => c.Sources)
                .HasForeignKey(s => s.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
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
            // Removing a source un-attributes its links (SET NULL) rather than deleting them, so removing a
            // source without its images leaves the pins in place.
            e.HasOne(ci => ci.CollectionSource)
                .WithMany()
                .HasForeignKey(ci => ci.CollectionSourceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<SyncOp>(e =>
        {
            // The log is read back in chronological order during sync replay; Id is monotonic with append
            // order, and SQLite can't ORDER BY DateTimeOffset, so the index is on Id implicitly (PK).
            e.Property(o => o.EntityKey).IsRequired();
            e.HasIndex(o => o.EntityKey);
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
