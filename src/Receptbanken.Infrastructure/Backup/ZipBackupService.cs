using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Storage;

namespace Receptbanken.Infrastructure.Backup;

public class BackupLimitsOptions
{
    public long MaxCompressedBytes { get; set; } = 100L * 1024 * 1024; // 100 MB
    public long MaxUncompressedTotalBytes { get; set; } = 500L * 1024 * 1024; // 500 MB
    public long MaxSingleEntryBytes { get; set; } = 50L * 1024 * 1024; // 50 MB
    public int MaxEntryCount { get; set; } = 5_000;
}

public class ZipBackupService : IBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ImageStorageOptions _storageOptions;
    private readonly BackupLimitsOptions _limits;
    private readonly ILogger<ZipBackupService> _logger;
    private readonly ShoppingListNotifier? _shoppingListNotifier;

    /// <summary>
    /// Optional hook for testing transactional + filesystem rollback when an error occurs right before commit.
    /// </summary>
    public Action? BeforeCommitTestHook { get; set; }

    public ZipBackupService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOptions<ImageStorageOptions> storageOptions,
        ILogger<ZipBackupService> logger,
        BackupLimitsOptions? limits = null,
        ShoppingListNotifier? shoppingListNotifier = null)
    {
        _dbFactory = dbFactory;
        _storageOptions = storageOptions.Value;
        _logger = logger;
        _limits = limits ?? new BackupLimitsOptions();
        _shoppingListNotifier = shoppingListNotifier;
    }

    public async Task<byte[]> CreateBackupArchiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var categories = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        var recipes = await db.Recipes
            .AsNoTracking()
            .Include(r => r.Ingredients.OrderBy(i => i.OrderIndex))
            .Include(r => r.Instructions.OrderBy(s => s.StepNumber))
            .Include(r => r.RecipeCategories)
                .ThenInclude(rc => rc.Category)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);

        var shoppingItems = await db.ShoppingListItems
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var weeklyMenus = await db.WeeklyMenus
            .AsNoTracking()
            .Include(m => m.PantryItems.OrderBy(p => p.OrderIndex))
            .Include(m => m.Days.OrderBy(d => d.DayIndex))
                .ThenInclude(d => d.Recipe)
            .Include(m => m.Days)
                .ThenInclude(d => d.SelectedCategories)
                    .ThenInclude(sc => sc.Category)
            .Include(m => m.ShoppingContributions)
            .OrderBy(m => m.WeekStartDate)
            .ToListAsync(cancellationToken);

        var recipeTitleById = recipes.ToDictionary(r => r.Id, r => r.Title);

        var payload = new BackupDataPayload
        {
            Categories = categories.Select(c => new BackupCategoryDto
            {
                Name = c.Name,
                ColorHex = c.ColorHex ?? "#e07a5f"
            }).ToList(),
            Recipes = recipes.Select(r => new BackupRecipeDto
            {
                Title = r.Title,
                Description = r.Description,
                Notes = r.Notes,
                PrepTimeMinutes = r.PrepTimeMinutes,
                CookTimeMinutes = r.CookTimeMinutes,
                Servings = r.Servings,
                ServingsUnit = r.ServingsUnit,
                ImagePath = r.ImagePath,
                SourceUrl = r.SourceUrl,
                SourceHost = r.SourceHost,
                IsFavorite = r.IsFavorite,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                CategoryNames = r.RecipeCategories
                    .Where(rc => rc.Category != null)
                    .Select(rc => rc.Category.Name)
                    .ToList(),
                Ingredients = r.Ingredients.Select(i => new BackupIngredientDto
                {
                    OrderIndex = i.OrderIndex,
                    RawText = i.RawText,
                    Quantity = i.Quantity,
                    QuantityMax = i.QuantityMax,
                    Unit = i.Unit,
                    Name = i.Name,
                    Comment = i.Comment,
                    Preparation = i.Preparation,
                    GroupHeader = i.GroupHeader
                }).ToList(),
                Instructions = r.Instructions.Select(s => new BackupInstructionDto
                {
                    StepNumber = s.StepNumber,
                    InstructionText = s.InstructionText,
                    GroupHeader = s.GroupHeader
                }).ToList()
            }).ToList(),
            ShoppingListItems = shoppingItems.Select(s => new BackupShoppingItemDto
            {
                RawText = s.RawText,
                Quantity = s.Quantity,
                QuantityMax = s.QuantityMax,
                Unit = s.Unit,
                Name = s.Name,
                Comment = s.Comment,
                IsChecked = s.IsChecked,
                SortOrder = s.SortOrder,
                Department = s.Department,
                CreatedAt = s.CreatedAt
            }).ToList(),
            WeeklyMenus = weeklyMenus.Select(m => new BackupWeeklyMenuDto
            {
                WeekStartDate = m.WeekStartDate,
                IsoYear = m.IsoYear,
                IsoWeek = m.IsoWeek,
                Status = m.Status,
                ConfirmedAt = m.ConfirmedAt,
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt,
                PantryIngredients = m.PantryItems
                    .OrderBy(p => p.OrderIndex)
                    .Select(p => p.IngredientName)
                    .ToList(),
                Days = m.Days.OrderBy(d => d.DayIndex).Select(d => new BackupWeeklyMenuDayDto
                {
                    DayIndex = d.DayIndex,
                    Date = d.Date,
                    RecipeTitle = d.Recipe?.Title ?? (d.RecipeId.HasValue && recipeTitleById.TryGetValue(d.RecipeId.Value, out var t) ? t : null),
                    Servings = d.Servings,
                    CategoryNames = d.SelectedCategories
                        .Where(sc => sc.Category != null)
                        .Select(sc => sc.Category!.Name)
                        .ToList()
                }).ToList(),
                ShoppingContributions = m.ShoppingContributions
                    .OrderBy(c => c.DayIndex)
                    .ThenBy(c => c.ContributionKey)
                    .Select(c => new BackupWeeklyMenuContributionDto
                    {
                        DayIndex = c.DayIndex,
                        RecipeTitle = recipeTitleById.TryGetValue(c.RecipeId, out var rt) ? rt : string.Empty,
                        ContributionKey = c.ContributionKey,
                        TargetServings = c.TargetServings,
                        AddedQuantity = c.AddedQuantity,
                        AddedQuantityMax = c.AddedQuantityMax,
                        Unit = c.Unit,
                        UpdatedAt = c.UpdatedAt
                    }).ToList()
            }).ToList()
        };

        var recipesImageFolder = Path.Combine(
            _storageOptions.BaseDataPath,
            _storageOptions.ImageFolderName.Replace('/', Path.DirectorySeparatorChar));

        var imageFiles = Directory.Exists(recipesImageFolder)
            ? Directory.GetFiles(recipesImageFolder)
            : [];

        var manifest = new BackupManifest
        {
            FormatVersion = 1,
            Application = "Receptbanken",
            ExportedAtUtc = DateTimeOffset.UtcNow,
            CategoryCount = payload.Categories.Count,
            RecipeCount = payload.Recipes.Count,
            ShoppingListItemCount = payload.ShoppingListItems.Count,
            ImageCount = imageFiles.Length
        };

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var entryStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, cancellationToken);
            }

            var dataEntry = archive.CreateEntry("data.json", CompressionLevel.Optimal);
            await using (var entryStream = dataEntry.Open())
            {
                await JsonSerializer.SerializeAsync(entryStream, payload, JsonOptions, cancellationToken);
            }

            foreach (var filePath in imageFiles)
            {
                var fileName = Path.GetFileName(filePath);
                var zipEntryPath = $"images/recipes/{fileName}";
                var imgEntry = archive.CreateEntry(zipEntryPath, CompressionLevel.Optimal);
                await using var entryStream = imgEntry.Open();
                await using var fileStream = File.OpenRead(filePath);
                await fileStream.CopyToAsync(entryStream, cancellationToken);
            }
        }

        return memoryStream.ToArray();
    }

    public async Task<BackupRestoreResult> RestoreFromArchiveAsync(
        Stream archiveStream,
        bool replaceExisting = true,
        CancellationToken cancellationToken = default)
    {
        if (archiveStream == null)
        {
            return Fail("Ingen säkerhetskopia angavs.");
        }

        // 1. Buffer and enforce compressed size limit
        using var compressedBuffer = new MemoryStream();
        var copyBuf = new byte[81920];
        long compressedRead = 0;
        int read;
        while ((read = await archiveStream.ReadAsync(copyBuf, cancellationToken)) > 0)
        {
            compressedRead += read;
            if (compressedRead > _limits.MaxCompressedBytes)
            {
                return Fail($"ZIP-filen överskrider maxgränsen ({_limits.MaxCompressedBytes / (1024 * 1024)} MB).");
            }
            compressedBuffer.Write(copyBuf, 0, read);
        }

        if (compressedRead == 0)
        {
            return Fail("ZIP-filen är tom.");
        }

        compressedBuffer.Position = 0;

        string stagingRoot = Path.Combine(Path.GetTempPath(), $"receptbanken_stage_{Guid.NewGuid():N}");
        string shadowImagesDir = Path.Combine(Path.GetTempPath(), $"receptbanken_shadow_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        string canonicalStagingRoot = Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar;

        try
        {
            // 2. Inspect & extract archive safely with Zip-Slip and Zip-Bomb guards
            using var archive = new ZipArchive(compressedBuffer, ZipArchiveMode.Read, leaveOpen: true);

            if (archive.Entries.Count > _limits.MaxEntryCount)
            {
                return Fail($"ZIP-arkivet innehåller för många filer ({archive.Entries.Count}). Max {_limits.MaxEntryCount} tillåts.");
            }

            long totalUncompressed = 0;
            int extractedImageCount = 0;

            foreach (var entry in archive.Entries)
            {
                var normalizedEntryName = entry.FullName.Replace('\\', '/');

                // Reject Zip-Slip traversal attempts
                if (normalizedEntryName.Contains("..") ||
                    Path.IsPathRooted(normalizedEntryName) ||
                    normalizedEntryName.StartsWith('/'))
                {
                    return Fail($"Säkerhetsavvisning (Zip-Slip): Otillåten filsökväg i arkivet ('{entry.FullName}').");
                }

                // Skip directory entries
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var destinationPath = Path.GetFullPath(Path.Combine(stagingRoot, normalizedEntryName));
                if (!destinationPath.StartsWith(canonicalStagingRoot, StringComparison.Ordinal))
                {
                    return Fail($"Säkerhetsavvisning (Zip-Slip): Filsökvägen pekar utanför målmappen ('{entry.FullName}').");
                }

                // Whitelist allowed entries inside archive
                bool isAllowedMetadata = normalizedEntryName is "manifest.json" or "data.json";
                bool isAllowedImage = normalizedEntryName.StartsWith("images/recipes/", StringComparison.Ordinal) &&
                                      normalizedEntryName.Count(c => c == '/') == 2 &&
                                      IsAllowedImageExtension(Path.GetExtension(normalizedEntryName));

                if (!isAllowedMetadata && !isAllowedImage)
                {
                    return Fail($"Otillåten fil i säkerhetskopian: '{normalizedEntryName}'.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                await using var entryStream = entry.Open();
                await using var outputFile = File.Create(destinationPath);

                long entryBytes = 0;
                int bytesRead;
                while ((bytesRead = await entryStream.ReadAsync(copyBuf, cancellationToken)) > 0)
                {
                    entryBytes += bytesRead;
                    totalUncompressed += bytesRead;

                    if (entryBytes > _limits.MaxSingleEntryBytes)
                    {
                        return Fail($"Filen '{entry.FullName}' överskrider maxstorleken per fil i säkerhetskopian.");
                    }

                    if (totalUncompressed > _limits.MaxUncompressedTotalBytes)
                    {
                        return Fail("Den uppackade datamängden överskrider säkerhetsgränsen (Zip-Bomb-skydd).");
                    }

                    await outputFile.WriteAsync(copyBuf.AsMemory(0, bytesRead), cancellationToken);
                }

                if (isAllowedImage)
                {
                    extractedImageCount++;
                }
            }

            // 3. Validate manifest.json and data.json
            var manifestPath = Path.Combine(stagingRoot, "manifest.json");
            var dataPath = Path.Combine(stagingRoot, "data.json");

            if (!File.Exists(manifestPath) || !File.Exists(dataPath))
            {
                return Fail("Säkerhetskopian saknar manifest.json eller data.json.");
            }

            BackupManifest? manifest;
            BackupDataPayload? payload;

            await using (var ms = File.OpenRead(manifestPath))
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, JsonOptions, cancellationToken);
            }
            await using (var ds = File.OpenRead(dataPath))
            {
                payload = await JsonSerializer.DeserializeAsync<BackupDataPayload>(ds, JsonOptions, cancellationToken);
            }

            if (manifest == null || manifest.Application != "Receptbanken" || payload == null)
            {
                return Fail("Ogiltigt format i säkerhetskopians manifest eller datafil.");
            }

            // 4. Prepare shadow backup of existing live images folder for atomic rollback
            var liveImagesDir = Path.Combine(
                _storageOptions.BaseDataPath,
                _storageOptions.ImageFolderName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(liveImagesDir);
            Directory.CreateDirectory(shadowImagesDir);

            foreach (var existingImg in Directory.GetFiles(liveImagesDir))
            {
                File.Copy(existingImg, Path.Combine(shadowImagesDir, Path.GetFileName(existingImg)), overwrite: true);
            }

            // 5. Execute Database + Filesystem changes inside an atomic transaction with rollback recovery
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                if (replaceExisting)
                {
                    db.WeeklyMenuShoppingContributions.RemoveRange(await db.WeeklyMenuShoppingContributions.ToListAsync(cancellationToken));
                    db.WeeklyMenuDayCategories.RemoveRange(await db.WeeklyMenuDayCategories.ToListAsync(cancellationToken));
                    db.WeeklyMenuPantryItems.RemoveRange(await db.WeeklyMenuPantryItems.ToListAsync(cancellationToken));
                    db.WeeklyMenuDays.RemoveRange(await db.WeeklyMenuDays.ToListAsync(cancellationToken));
                    db.WeeklyMenus.RemoveRange(await db.WeeklyMenus.ToListAsync(cancellationToken));
                    db.ShoppingListItems.RemoveRange(await db.ShoppingListItems.ToListAsync(cancellationToken));
                    db.RecipeCategories.RemoveRange(await db.RecipeCategories.ToListAsync(cancellationToken));
                    db.Ingredients.RemoveRange(await db.Ingredients.ToListAsync(cancellationToken));
                    db.Instructions.RemoveRange(await db.Instructions.ToListAsync(cancellationToken));
                    db.Recipes.RemoveRange(await db.Recipes.ToListAsync(cancellationToken));
                    db.Categories.RemoveRange(await db.Categories.ToListAsync(cancellationToken));
                    await db.SaveChangesAsync(cancellationToken);
                }

                // Restore Categories
                var existingCategories = await db.Categories.ToDictionaryAsync(c => c.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
                foreach (var catDto in payload.Categories)
                {
                    if (!existingCategories.TryGetValue(catDto.Name, out var category))
                    {
                        category = new Category
                        {
                            Name = catDto.Name,
                            ColorHex = catDto.ColorHex
                        };
                        db.Categories.Add(category);
                        existingCategories[catDto.Name] = category;
                    }
                }
                await db.SaveChangesAsync(cancellationToken);

                // Restore Recipes
                foreach (var recDto in payload.Recipes)
                {
                    var recipe = new Recipe
                    {
                        Title = recDto.Title,
                        Description = recDto.Description,
                        Notes = recDto.Notes,
                        PrepTimeMinutes = recDto.PrepTimeMinutes,
                        CookTimeMinutes = recDto.CookTimeMinutes,
                        Servings = recDto.Servings,
                        ServingsUnit = recDto.ServingsUnit,
                        ImagePath = recDto.ImagePath,
                        SourceUrl = recDto.SourceUrl,
                        SourceHost = recDto.SourceHost,
                        IsFavorite = recDto.IsFavorite,
                        CreatedAt = recDto.CreatedAt,
                        UpdatedAt = recDto.UpdatedAt,
                        Ingredients = recDto.Ingredients.Select(i => new Ingredient
                        {
                            OrderIndex = i.OrderIndex,
                            RawText = i.RawText,
                            Quantity = i.Quantity,
                            QuantityMax = i.QuantityMax,
                            Unit = i.Unit,
                            Name = i.Name,
                            Comment = i.Comment,
                            Preparation = i.Preparation,
                            GroupHeader = i.GroupHeader
                        }).ToList(),
                        Instructions = recDto.Instructions.Select(s => new InstructionStep
                        {
                            StepNumber = s.StepNumber,
                            InstructionText = s.InstructionText,
                            GroupHeader = s.GroupHeader
                        }).ToList()
                    };

                    foreach (var catName in recDto.CategoryNames.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (existingCategories.TryGetValue(catName, out var cat))
                        {
                            recipe.RecipeCategories.Add(new RecipeCategory { Category = cat });
                        }
                    }

                    db.Recipes.Add(recipe);
                }

                // Restore Shopping List Items
                int fallbackSortOrder = 0;
                foreach (var shopDto in payload.ShoppingListItems)
                {
                    fallbackSortOrder++;
                    db.ShoppingListItems.Add(new ShoppingListItem
                    {
                        RawText = shopDto.RawText,
                        Quantity = shopDto.Quantity,
                        QuantityMax = shopDto.QuantityMax,
                        Unit = shopDto.Unit,
                        Name = shopDto.Name,
                        Comment = shopDto.Comment,
                        IsChecked = shopDto.IsChecked,
                        SortOrder = shopDto.SortOrder > 0 ? shopDto.SortOrder : fallbackSortOrder,
                        Department = shopDto.Department,
                        CreatedAt = shopDto.CreatedAt
                    });
                }

                await db.SaveChangesAsync(cancellationToken);

                // Restore Weekly Menus and their Shopping Contributions when present in backup
                if (payload.WeeklyMenus is { Count: > 0 })
                {
                    var restoredRecipesByTitle = await db.Recipes
                        .GroupBy(r => r.Title)
                        .ToDictionaryAsync(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

                    foreach (var menuDto in payload.WeeklyMenus)
                    {
                        var existingMenu = await db.WeeklyMenus
                            .FirstOrDefaultAsync(m => m.WeekStartDate == menuDto.WeekStartDate, cancellationToken);
                        if (existingMenu != null)
                            continue;

                        var restoredMenu = new WeeklyMenu
                        {
                            WeekStartDate = menuDto.WeekStartDate,
                            IsoYear = menuDto.IsoYear,
                            IsoWeek = menuDto.IsoWeek,
                            Status = menuDto.Status,
                            ConfirmedAt = menuDto.ConfirmedAt,
                            CreatedAt = menuDto.CreatedAt,
                            UpdatedAt = menuDto.UpdatedAt,
                            PantryItems = (menuDto.PantryIngredients ?? [])
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Select((name, idx) => new WeeklyMenuPantryItem
                                {
                                    OrderIndex = idx + 1,
                                    IngredientName = name.Trim()
                                })
                                .ToList()
                        };

                        foreach (var dayDto in menuDto.Days ?? [])
                        {
                            int? matchedRecipeId = !string.IsNullOrWhiteSpace(dayDto.RecipeTitle) &&
                                                   restoredRecipesByTitle.TryGetValue(dayDto.RecipeTitle, out var rid)
                                ? rid
                                : null;

                            var restoredDay = new WeeklyMenuDay
                            {
                                DayIndex = dayDto.DayIndex,
                                Date = dayDto.Date,
                                RecipeId = matchedRecipeId,
                                Servings = matchedRecipeId.HasValue ? dayDto.Servings : null
                            };

                            foreach (var catName in (dayDto.CategoryNames ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (existingCategories.TryGetValue(catName, out var cat))
                                {
                                    restoredDay.SelectedCategories.Add(new WeeklyMenuDayCategory { Category = cat });
                                }
                            }

                            restoredMenu.Days.Add(restoredDay);
                        }

                        foreach (var contribDto in menuDto.ShoppingContributions ?? [])
                        {
                            if (!string.IsNullOrWhiteSpace(contribDto.RecipeTitle) &&
                                !string.IsNullOrWhiteSpace(contribDto.ContributionKey) &&
                                restoredRecipesByTitle.TryGetValue(contribDto.RecipeTitle, out var contribRecipeId))
                            {
                                restoredMenu.ShoppingContributions.Add(new WeeklyMenuShoppingContribution
                                {
                                    WeekStartDate = menuDto.WeekStartDate,
                                    DayIndex = contribDto.DayIndex,
                                    RecipeId = contribRecipeId,
                                    ContributionKey = contribDto.ContributionKey,
                                    TargetServings = contribDto.TargetServings,
                                    AddedQuantity = contribDto.AddedQuantity,
                                    AddedQuantityMax = contribDto.AddedQuantityMax,
                                    Unit = contribDto.Unit,
                                    UpdatedAt = contribDto.UpdatedAt
                                });
                            }
                        }

                        db.WeeklyMenus.Add(restoredMenu);
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }

                // Swap filesystem images
                if (replaceExisting)
                {
                    foreach (var oldFile in Directory.GetFiles(liveImagesDir))
                    {
                        File.Delete(oldFile);
                    }
                }

                var stagedImagesDir = Path.Combine(stagingRoot, "images", "recipes");
                if (Directory.Exists(stagedImagesDir))
                {
                    foreach (var stagedFile in Directory.GetFiles(stagedImagesDir))
                    {
                        var destFile = Path.Combine(liveImagesDir, Path.GetFileName(stagedFile));
                        File.Copy(stagedFile, destFile, overwrite: true);
                    }
                }

                // Invoke optional test hook before committing transaction
                BeforeCommitTestHook?.Invoke();

                await transaction.CommitAsync(cancellationToken);
                _shoppingListNotifier?.NotifyChanged();

                return new BackupRestoreResult
                {
                    Success = true,
                    RestoredCategories = payload.Categories.Count,
                    RestoredRecipes = payload.Recipes.Count,
                    RestoredImages = extractedImageCount
                };
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Fel under återställning av säkerhetskopia; rullar tillbaka databas och bildfiler.");

                await transaction.RollbackAsync(CancellationToken.None);

                // Rollback filesystem images from shadow copy
                try
                {
                    foreach (var currentFile in Directory.GetFiles(liveImagesDir))
                    {
                        File.Delete(currentFile);
                    }
                    foreach (var shadowFile in Directory.GetFiles(shadowImagesDir))
                    {
                        File.Copy(shadowFile, Path.Combine(liveImagesDir, Path.GetFileName(shadowFile)), overwrite: true);
                    }
                }
                catch (Exception fsEx)
                {
                    _logger.LogCritical(fsEx, "Kunde inte återställa skuggkopia av bildmappen vid rollback.");
                }

                return Fail($"Återställningen avbröts och rullades tillbaka: {innerEx.Message}");
            }
        }
        catch (InvalidDataException)
        {
            return Fail("Filen är inte ett giltigt ZIP-arkiv.");
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(shadowImagesDir);
        }
    }

    private static bool IsAllowedImageExtension(string ext) =>
        ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".png", StringComparison.OrdinalIgnoreCase);

    private static BackupRestoreResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors in temp directory
        }
    }

    private sealed class BackupDataPayload
    {
        public List<BackupCategoryDto> Categories { get; set; } = new();
        public List<BackupRecipeDto> Recipes { get; set; } = new();
        public List<BackupShoppingItemDto> ShoppingListItems { get; set; } = new();
        public List<BackupWeeklyMenuDto> WeeklyMenus { get; set; } = new();
    }

    private sealed class BackupCategoryDto
    {
        public string Name { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#e07a5f";
    }

    private sealed class BackupRecipeDto
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Notes { get; set; }
        public int? PrepTimeMinutes { get; set; }
        public int? CookTimeMinutes { get; set; }
        public int Servings { get; set; } = 4;
        public string ServingsUnit { get; set; } = "portioner";
        public string? ImagePath { get; set; }
        public string? SourceUrl { get; set; }
        public string? SourceHost { get; set; }
        public bool IsFavorite { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<string> CategoryNames { get; set; } = new();
        public List<BackupIngredientDto> Ingredients { get; set; } = new();
        public List<BackupInstructionDto> Instructions { get; set; } = new();
    }

    private sealed class BackupIngredientDto
    {
        public int OrderIndex { get; set; }
        public string RawText { get; set; } = string.Empty;
        public decimal? Quantity { get; set; }
        public decimal? QuantityMax { get; set; }
        public string? Unit { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Comment { get; set; }
        public string? Preparation { get; set; }
        public string? GroupHeader { get; set; }
    }

    private sealed class BackupInstructionDto
    {
        public int StepNumber { get; set; }
        public string InstructionText { get; set; } = string.Empty;
        public string? GroupHeader { get; set; }
    }

    private sealed class BackupShoppingItemDto
    {
        public string RawText { get; set; } = string.Empty;
        public decimal? Quantity { get; set; }
        public decimal? QuantityMax { get; set; }
        public string? Unit { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Comment { get; set; }
        public bool IsChecked { get; set; }
        public int SortOrder { get; set; }
        public string? Department { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class BackupWeeklyMenuDto
    {
        public DateOnly WeekStartDate { get; set; }
        public int IsoYear { get; set; }
        public int IsoWeek { get; set; }
        public WeeklyMenuStatus Status { get; set; }
        public DateTimeOffset? ConfirmedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<string> PantryIngredients { get; set; } = new();
        public List<BackupWeeklyMenuDayDto> Days { get; set; } = new();
        public List<BackupWeeklyMenuContributionDto> ShoppingContributions { get; set; } = new();
    }

    private sealed class BackupWeeklyMenuDayDto
    {
        public int DayIndex { get; set; }
        public DateOnly Date { get; set; }
        public string? RecipeTitle { get; set; }
        public int? Servings { get; set; }
        public List<string> CategoryNames { get; set; } = new();
    }

    private sealed class BackupWeeklyMenuContributionDto
    {
        public int DayIndex { get; set; }
        public string RecipeTitle { get; set; } = string.Empty;
        public string ContributionKey { get; set; } = string.Empty;
        public int TargetServings { get; set; }
        public decimal? AddedQuantity { get; set; }
        public decimal? AddedQuantityMax { get; set; }
        public string? Unit { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}

