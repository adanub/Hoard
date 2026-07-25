using System.Security.Cryptography;
using Hoard.Core.Domain;
using Hoard.Core.Library;
using Xunit;

namespace Hoard.Core.Tests;

public class ProjectVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-verify-test", Guid.NewGuid().ToString("N"));

    public ProjectVerifierTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Reports_missing_altered_and_orphaned_blobs()
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, "verify.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        var storeRoot = Path.Combine(_dir, "store");

        await using (var db = factory.CreateDbContext())
        {
            db.Assets.AddRange(
                WriteAsset(storeRoot, "healthy"),                        // blob present, hash matches
                WriteAsset(storeRoot, "altered", corruptAfter: true),    // blob present, bytes changed
                MakeAsset("gone-content"));                              // row only — blob never written
            var tombstoned = WriteAsset(storeRoot, "tombstoned-leftover"); // freed-by-design blob left behind
            tombstoned.DeletedAt = DateTimeOffset.UtcNow;
            tombstoned.DeletionNote = "dup";
            db.Assets.Add(tombstoned);
            await db.SaveChangesAsync();
        }

        // A store file no row references at all.
        var orphanPath = Path.Combine(storeRoot, "ff", "ee", "orphan.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        File.WriteAllText(orphanPath, "orphan");

        await using (var db = factory.CreateDbContext())
        {
            var report = await ProjectVerifier.VerifyAsync(db, storeRoot);

            Assert.Equal(3, report.LiveAssets);
            Assert.Equal(2, report.BlobsHashed); // healthy + altered; the missing one can't be hashed
            Assert.Equal(1, report.Missing);
            Assert.Equal(1, report.Altered);
            Assert.Equal(2, report.Orphans);     // the unreferenced file AND the tombstone's leftover blob
            Assert.Contains(report.Issues, i => i.Kind == VerifyIssueKind.AlteredBlob && i.Title == "Title altered");
            Assert.Contains(report.Issues, i => i.Kind == VerifyIssueKind.OrphanBlob && i.Path == "ff/ee/orphan.jpg");
        }
    }

    [Fact]
    public async Task Clean_project_reports_no_issues()
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, "clean.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        var storeRoot = Path.Combine(_dir, "clean-store");

        await using (var db = factory.CreateDbContext())
        {
            db.Assets.Add(WriteAsset(storeRoot, "only"));
            await db.SaveChangesAsync();
        }

        await using (var db = factory.CreateDbContext())
        {
            var report = await ProjectVerifier.VerifyAsync(db, storeRoot);
            Assert.True(report.IsClean);
            Assert.Equal(1, report.BlobsHashed);
        }
    }

    /// <summary>An asset row whose blob really exists in the store with matching content hash.</summary>
    private static Asset WriteAsset(string storeRoot, string seed, bool corruptAfter = false)
    {
        var content = "content-" + seed;
        var sha = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        var asset = MakeAsset(seed, sha);
        var absolute = Path.Combine(storeRoot, asset.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        if (corruptAfter) File.WriteAllText(absolute, content + "-tampered");
        return asset;
    }

    private static Asset MakeAsset(string seed, string? sha = null)
    {
        sha ??= Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("absent-" + seed)));
        return new Asset
        {
            Sha256 = sha,
            RelativePath = $"{sha[..2]}/{sha[2..4]}/{sha}.jpg",
            MimeType = "image/jpeg",
            Kind = MediaKind.Image,
            Bytes = 1,
            SourceConnector = "pinterest",
            Title = "Title " + seed,
            ImportedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
