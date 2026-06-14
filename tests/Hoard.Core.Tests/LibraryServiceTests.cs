using Hoard.Core.Domain;
using Hoard.Core.Library;
using Hoard.Core.Storage;
using Xunit;

namespace Hoard.Core.Tests;

public class LibraryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-library-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public LibraryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    private async Task SeedAsync(string title, string? pinId)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.Assets.Add(new Asset
        {
            Sha256 = Guid.NewGuid().ToString("N"),
            RelativePath = "ab/cd/blob",
            Kind = MediaKind.Image,
            SourceConnector = "pinterest",
            SourceId = pinId,
            Title = title,
            ImportedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Assets_are_ordered_by_pin_id_newest_first_not_import_order()
    {
        // Import order (and Id) deliberately differs from pin-id order.
        await SeedAsync("low", "100");
        await SeedAsync("high", "300");
        await SeedAsync("mid", "200");

        var views = await new LibraryService(_dbFactory, _store).GetAssetsAsync(null);

        Assert.Equal(new[] { "high", "mid", "low" }, views.Select(v => v.Title).ToArray());
    }

    [Fact]
    public async Task Pin_ids_sort_numerically_not_lexically()
    {
        // Lexically "1000" < "9"; numerically 1000 > 9. The bigger number must come first.
        await SeedAsync("nine", "9");
        await SeedAsync("thousand", "1000");

        var views = await new LibraryService(_dbFactory, _store).GetAssetsAsync(null);

        Assert.Equal(new[] { "thousand", "nine" }, views.Select(v => v.Title).ToArray());
    }

    [Fact]
    public async Task Assets_without_a_pin_id_sort_last_keeping_newest_import_first()
    {
        await SeedAsync("has-id", "500");
        await SeedAsync("none-A", null); // lower Id
        await SeedAsync("none-B", null); // higher Id (imported later)

        var views = await new LibraryService(_dbFactory, _store).GetAssetsAsync(null);

        // The pinned one leads; among the id-less ones the later import (higher Id) comes first.
        Assert.Equal(new[] { "has-id", "none-B", "none-A" }, views.Select(v => v.Title).ToArray());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
