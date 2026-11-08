using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    [Fact]
    public async Task Migrate_I3_19_WhenExistingShoppingListItemsPresentBefore3B1_ShouldApplyCleanlyWithNullCommentAndQuantityMax()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var context = new AppDbContext(options);

        // Migrate up to the previous migration (before AddShoppingListItemCommentAndQuantityMax)
        var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261106155000_AddShoppingListItemSortOrder");

        // Insert a legacy ShoppingListItem using raw SQL before Comment and QuantityMax columns exist
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO ShoppingListItems (RawText, Quantity, Unit, Name, IsChecked, SortOrder, CreatedAt)
            VALUES ('2 dl mjölk', '2.0', 'dl', 'mjölk', 0, 1, 639297000000000000);
            """);

        // Now apply the latest migration (AddShoppingListItemCommentAndQuantityMax)
        await context.Database.MigrateAsync();

        var appliedAfter = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        appliedAfter.Should().Contain(m => m.Contains("AddShoppingListItemCommentAndQuantityMax"));

        var items = await context.ShoppingListItems.AsNoTracking().ToListAsync();
        items.Should().ContainSingle();
        items[0].Name.Should().Be("mjölk");
        items[0].Quantity.Should().Be(2m);
        items[0].QuantityMax.Should().BeNull();
        items[0].Comment.Should().BeNull();
    }
}

