using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Backup;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Infrastructure.Storage;
using Receptbanken.Tests.Infrastructure;

namespace Receptbanken.Tests.Backup;

public class ZipBackupServiceTests : IDisposable
{
    private readonly string _tempDataDir;
    private readonly TestDbContextFactory _sourceDbFactory;
    private readonly TestDbContextFactory _targetDbFactory;

    public ZipBackupServiceTests()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"rb_backup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDataDir, "source", "images", "recipes"));
        Directory.CreateDirectory(Path.Combine(_tempDataDir, "target", "images", "recipes"));

        _sourceDbFactory = new TestDbContextFactory();
        _targetDbFactory = new TestDbContextFactory();
    }

    private ZipBackupService CreateBackupService(
        TestDbContextFactory dbFactory,
        string subFolder,
        BackupLimitsOptions? limits = null)
    {
        var opts = Options.Create(new ImageStorageOptions
        {
            BaseDataPath = Path.Combine(_tempDataDir, subFolder),
            ImageFolderName = "images/recipes"
        });
        return new ZipBackupService(dbFactory, opts, NullLogger<ZipBackupService>.Instance, limits);
    }

    [Fact]
    public async Task ExportAndRestore_RoundTrip_ShouldRestoreRecipesCategoriesShoppingListAndImages()
    {
        var catService = new CategoryService(_sourceDbFactory, NullLogger<CategoryService>.Instance);
        var cat = await catService.CreateCategoryAsync(new Category { Name = "Husmanskost", ColorHex = "#e07a5f" });

        var recipeService = new RecipeService(_sourceDbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Kåldolmar",
                Description = "Klassiska kåldolmar med gräddsås.",
                Servings = 4,
                ImagePath = "images/recipes/kaldolmar.webp",
                IsFavorite = true,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "blandfärs", RawText = "500 g blandfärs" }
                ],
                Instructions =
                [
                    new InstructionStep { StepNumber = 1, InstructionText = "Förväll vitkålsbladen." }
                ]
            },
            [cat.Id]);

        var sourceImgPath = Path.Combine(_tempDataDir, "source", "images", "recipes", "kaldolmar.webp");
        await File.WriteAllBytesAsync(sourceImgPath, [1, 2, 3, 4, 5]);

        var exporter = CreateBackupService(_sourceDbFactory, "source");
        var zipBytes = await exporter.CreateBackupArchiveAsync();
        zipBytes.Should().NotBeEmpty();

        var restorer = CreateBackupService(_targetDbFactory, "target");
        using var zipStream = new MemoryStream(zipBytes);
        var restoreResult = await restorer.RestoreFromArchiveAsync(zipStream, replaceExisting: true);

        restoreResult.Success.Should().BeTrue();
        restoreResult.RestoredRecipes.Should().Be(1);
        restoreResult.RestoredImages.Should().Be(1);

        var targetRecipeService = new RecipeService(_targetDbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        var restoredRecipes = await targetRecipeService.GetRecipesAsync();
        restoredRecipes.Should().ContainSingle(r => r.Title == "Kåldolmar" && r.IsFavorite);

        var targetImgPath = Path.Combine(_tempDataDir, "target", "images", "recipes", "kaldolmar.webp");
        File.Exists(targetImgPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(targetImgPath)).Should().Equal([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task RestoreFromArchiveAsync_WhenZipContainsPathTraversal_ShouldRejectWithZipSlipError()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var traversalEntry = archive.CreateEntry("../../outside.txt");
            await using var s = traversalEntry.Open();
            await s.WriteAsync("malicious"u8.ToArray());
        }
        ms.Position = 0;

        var restorer = CreateBackupService(_targetDbFactory, "target");
        var result = await restorer.RestoreFromArchiveAsync(ms);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Zip-Slip");
    }

    [Fact]
    public async Task RestoreFromArchiveAsync_WhenEntryCountExceedsLimit_ShouldRejectArchive()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < 6; i++)
            {
                var entry = archive.CreateEntry($"images/recipes/img{i}.webp");
                await using var s = entry.Open();
                await s.WriteAsync(new byte[] { 1, 2 });
            }
        }
        ms.Position = 0;

        var limits = new BackupLimitsOptions { MaxEntryCount = 5 };
        var restorer = CreateBackupService(_targetDbFactory, "target", limits);
        var result = await restorer.RestoreFromArchiveAsync(ms);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("för många filer");
    }

    [Fact]
    public async Task RestoreFromArchiveAsync_WhenUncompressedSizeExceedsLimit_ShouldRejectZipBomb()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("manifest.json");
            await using var s = entry.Open();
            await s.WriteAsync(new byte[2048]);
        }
        ms.Position = 0;

        var limits = new BackupLimitsOptions { MaxUncompressedTotalBytes = 1024 };
        var restorer = CreateBackupService(_targetDbFactory, "target", limits);
        var result = await restorer.RestoreFromArchiveAsync(ms);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Zip-Bomb");
    }

    [Fact]
    public async Task RestoreFromArchiveAsync_WhenFailureOccursMidRestore_ShouldRollbackDatabaseAndImages()
    {
        // 1. Seed existing target database and target image file
        var targetRecipeService = new RecipeService(_targetDbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        await targetRecipeService.CreateRecipeAsync(
            new Recipe { Title = "Ursprungligt Recept", Servings = 2 },
            []);

        var existingImageFile = Path.Combine(_tempDataDir, "target", "images", "recipes", "original.webp");
        await File.WriteAllBytesAsync(existingImageFile, [9, 9, 9]);

        // 2. Create a valid backup from source containing a different recipe
        var sourceRecipeService = new RecipeService(_sourceDbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        await sourceRecipeService.CreateRecipeAsync(
            new Recipe { Title = "Nytt Recept Från Backup", Servings = 4 },
            []);

        var exporter = CreateBackupService(_sourceDbFactory, "source");
        var zipBytes = await exporter.CreateBackupArchiveAsync();

        // 3. Configure restorer to throw right before commit
        var restorer = CreateBackupService(_targetDbFactory, "target");
        restorer.BeforeCommitTestHook = () => throw new IOException("Simulerat diskfel före commit");

        using var stream = new MemoryStream(zipBytes);
        var result = await restorer.RestoreFromArchiveAsync(stream, replaceExisting: true);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("rullades tillbaka");

        // 4. Verify database rolled back and still contains "Ursprungligt Recept"
        var remainingRecipes = await targetRecipeService.GetRecipesAsync();
        remainingRecipes.Should().ContainSingle(r => r.Title == "Ursprungligt Recept");

        // 5. Verify filesystem shadow rollback restored "original.webp"
        File.Exists(existingImageFile).Should().BeTrue();
        (await File.ReadAllBytesAsync(existingImageFile)).Should().Equal([9, 9, 9]);
    }

    [Fact]
    public async Task ExportAndRestore_I3_24_ShouldPreserveShoppingListCommentAndQuantityMax_AndRestoreLegacyBackupWithoutComment()
    {
        await using (var sourceDb = _sourceDbFactory.CreateDbContext())
        {
            sourceDb.ShoppingListItems.AddRange(
                new ShoppingListItem
                {
                    Name = "grädde",
                    Quantity = 3m,
                    Unit = "dl",
                    Comment = "Extra vispad grädde till garnering, mängd ej angiven",
                    RawText = "3 dl grädde",
                    SortOrder = 1,
                    IsChecked = false
                },
                new ShoppingListItem
                {
                    Name = "spiskummin",
                    Quantity = 2m,
                    QuantityMax = 4m,
                    Unit = "tsk",
                    Comment = "malen",
                    RawText = "2–4 tsk spiskummin",
                    SortOrder = 2,
                    IsChecked = true
                });
            await sourceDb.SaveChangesAsync();
        }

        var exporter = CreateBackupService(_sourceDbFactory, "source");
        var zipBytes = await exporter.CreateBackupArchiveAsync();

        var restorer = CreateBackupService(_targetDbFactory, "target");
        using (var zipStream = new MemoryStream(zipBytes))
        {
            var restoreResult = await restorer.RestoreFromArchiveAsync(zipStream, replaceExisting: true);
            restoreResult.Success.Should().BeTrue();
        }

        await using (var targetDb = _targetDbFactory.CreateDbContext())
        {
            var items = targetDb.ShoppingListItems.OrderBy(x => x.SortOrder).ToList();
            items.Should().HaveCount(2);

            items[0].Name.Should().Be("grädde");
            items[0].Quantity.Should().Be(3m);
            items[0].Comment.Should().Be("Extra vispad grädde till garnering, mängd ej angiven");

            items[1].Name.Should().Be("spiskummin");
            items[1].Quantity.Should().Be(2m);
            items[1].QuantityMax.Should().Be(4m);
            items[1].Comment.Should().Be("malen");
            items[1].RawText.Should().Be("2–4 tsk spiskummin");
        }

        // Also verify that an older backup archive without "comment" or "quantityMax" on shoppingListItems restores cleanly
        using var legacyZipMs = new MemoryStream();
        using (var archive = new ZipArchive(legacyZipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            await using (var s = manifestEntry.Open())
            {
                await s.WriteAsync(Encoding.UTF8.GetBytes("""
                    {
                      "formatVersion": 1,
                      "application": "Receptbanken",
                      "exportedAtUtc": "2026-11-01T12:00:00Z",
                      "categoryCount": 0,
                      "recipeCount": 0,
                      "shoppingListItemCount": 1,
                      "imageCount": 0
                    }
                    """));
            }

            var dataEntry = archive.CreateEntry("data.json");
            await using (var s = dataEntry.Open())
            {
                await s.WriteAsync(Encoding.UTF8.GetBytes("""
                    {
                      "categories": [],
                      "recipes": [],
                      "shoppingListItems": [
                        {
                          "rawText": "2 dl mjölk",
                          "quantity": 2.0,
                          "unit": "dl",
                          "name": "mjölk",
                          "isChecked": false,
                          "sortOrder": 1,
                          "department": null,
                          "createdAt": "2026-11-01T12:00:00Z"
                        }
                      ]
                    }
                    """));
            }
        }
        legacyZipMs.Position = 0;

        var legacyRestoreResult = await restorer.RestoreFromArchiveAsync(legacyZipMs, replaceExisting: true);
        legacyRestoreResult.Success.Should().BeTrue();

        await using (var targetDbAfterLegacy = _targetDbFactory.CreateDbContext())
        {
            var legacyItems = targetDbAfterLegacy.ShoppingListItems.ToList();
            legacyItems.Should().ContainSingle();
            legacyItems[0].Name.Should().Be("mjölk");
            legacyItems[0].Quantity.Should().Be(2m);
            legacyItems[0].QuantityMax.Should().BeNull();
            legacyItems[0].Comment.Should().BeNull();
        }
    }

    public void Dispose()
    {
        _sourceDbFactory.Dispose();
        _targetDbFactory.Dispose();
        try
        {
            if (Directory.Exists(_tempDataDir))
                Directory.Delete(_tempDataDir, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup errors
        }
    }
}

