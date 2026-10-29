using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Tests.Infrastructure;

public class MigrationTests : IDisposable
{
    private readonly string _dbPath;

    public MigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"receptbanken_mig_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch { }
    }

    [Fact]
    public async Task InitializeAsync_ShouldApplyInitialCreateMigrationAndSeedData()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var context = new AppDbContext(options);

        var pendingBefore = (await context.Database.GetPendingMigrationsAsync()).ToList();
        pendingBefore.Should().Contain(m => m.Contains("InitialCreate"));

        await DbInitializer.InitializeAsync(context, NullLogger.Instance);

        var appliedAfter = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        appliedAfter.Should().Contain(m => m.Contains("InitialCreate"));

        var categoriesCount = await context.Categories.CountAsync();
        categoriesCount.Should().BeGreaterThanOrEqualTo(6);

        var recipesCount = await context.Recipes.CountAsync();
        recipesCount.Should().Be(1);
    }
}

