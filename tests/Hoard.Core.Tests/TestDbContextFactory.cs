using Hoard.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Tests;

/// <summary>Minimal <see cref="IDbContextFactory{T}"/> over a file-based SQLite database for tests.</summary>
internal sealed class TestDbContextFactory : IDbContextFactory<HoardDbContext>
{
    private readonly DbContextOptions<HoardDbContext> _options;

    public TestDbContextFactory(string dbPath)
    {
        _options = new DbContextOptionsBuilder<HoardDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    public HoardDbContext CreateDbContext() => new(_options);
}
