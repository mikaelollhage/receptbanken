using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

public class RecipeService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IImageStorageService _imageStorage;
    private readonly ILogger<RecipeService> _logger;

    public RecipeService(
        IDbContextFactory<AppDbContext> contextFactory,
        IImageStorageService imageStorage,
        ILogger<RecipeService> logger)
    {
        _contextFactory = contextFactory;
        _imageStorage = imageStorage;
        _logger = logger;
    }

    public async Task<List<Recipe>> GetRecipesAsync(
        string? searchQuery = null,
        int? categoryId = null,
        bool? favoritesOnly = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Recipes
            .AsNoTracking()
            .Include(r => r.RecipeCategories)
                .ThenInclude(rc => rc.Category)
            .Include(r => r.Ingredients)
            .AsQueryable();

        if (favoritesOnly == true)
        {
            query = query.Where(r => r.IsFavorite);
        }

        if (categoryId.HasValue && categoryId.Value > 0)
        {
            query = query.Where(r => r.RecipeCategories.Any(rc => rc.CategoryId == categoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var term = searchQuery.Trim().ToLower();
            query = query.Where(r =>
                r.Title.ToLower().Contains(term) ||
                (r.Description != null && r.Description.ToLower().Contains(term)) ||
                r.Ingredients.Any(i => i.Name.ToLower().Contains(term) || i.RawText.ToLower().Contains(term)) ||
                r.RecipeCategories.Any(rc => rc.Category.Name.ToLower().Contains(term)));
        }

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Recipe?> GetRecipeByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Recipes
            .AsNoTracking()
            .Include(r => r.RecipeCategories)
                .ThenInclude(rc => rc.Category)
            .Include(r => r.Ingredients.OrderBy(i => i.OrderIndex))
            .Include(r => r.Instructions.OrderBy(s => s.StepNumber))
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<Recipe> CreateRecipeAsync(Recipe recipe, List<int>? categoryIds = null, CancellationToken cancellationToken = default)
    {
        if (recipe.Servings < 1)
        {
            throw new InvalidOperationException("Antal portioner måste vara minst 1.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        recipe.CreatedAt = DateTimeOffset.UtcNow;
        recipe.UpdatedAt = DateTimeOffset.UtcNow;

        if (categoryIds != null && categoryIds.Count > 0)
        {
            recipe.RecipeCategories = categoryIds
                .Distinct()
                .Select(catId => new RecipeCategory { CategoryId = catId })
                .ToList();
        }

        // Ensure order indices
        for (int i = 0; i < recipe.Ingredients.Count; i++)
        {
            recipe.Ingredients[i].OrderIndex = i + 1;
        }

        for (int i = 0; i < recipe.Instructions.Count; i++)
        {
            recipe.Instructions[i].StepNumber = i + 1;
        }

        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Skapade recept {Title} med id {Id}", recipe.Title, recipe.Id);
        return recipe;
    }

    public async Task<Recipe> UpdateRecipeAsync(Recipe recipe, List<int>? categoryIds = null, CancellationToken cancellationToken = default)
    {
        if (recipe.Servings < 1)
        {
            throw new InvalidOperationException("Antal portioner måste vara minst 1.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.Recipes
            .Include(r => r.RecipeCategories)
            .Include(r => r.Ingredients)
            .Include(r => r.Instructions)
            .FirstOrDefaultAsync(r => r.Id == recipe.Id, cancellationToken);

        if (existing == null)
        {
            throw new InvalidOperationException($"Recept med id {recipe.Id} hittades inte.");
        }

        // Update basic fields
        existing.Title = recipe.Title;
        existing.Description = recipe.Description;
        existing.SourceUrl = recipe.SourceUrl;
        existing.SourceHost = recipe.SourceHost;
        existing.PrepTimeMinutes = recipe.PrepTimeMinutes;
        existing.CookTimeMinutes = recipe.CookTimeMinutes;
        existing.Servings = recipe.Servings;
        existing.ServingsUnit = recipe.ServingsUnit;
        existing.Notes = recipe.Notes;
        existing.IsFavorite = recipe.IsFavorite;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        // If image was replaced, remove the old one
        if (existing.ImagePath != recipe.ImagePath)
        {
            if (!string.IsNullOrWhiteSpace(existing.ImagePath))
            {
                await _imageStorage.DeleteImageAsync(existing.ImagePath, cancellationToken);
            }
            existing.ImagePath = recipe.ImagePath;
        }

        // Update ingredients
        context.Ingredients.RemoveRange(existing.Ingredients);
        for (int i = 0; i < recipe.Ingredients.Count; i++)
        {
            recipe.Ingredients[i].Id = 0;
            recipe.Ingredients[i].RecipeId = existing.Id;
            recipe.Ingredients[i].OrderIndex = i + 1;
        }
        existing.Ingredients = recipe.Ingredients;

        // Update instructions
        context.Instructions.RemoveRange(existing.Instructions);
        for (int i = 0; i < recipe.Instructions.Count; i++)
        {
            recipe.Instructions[i].Id = 0;
            recipe.Instructions[i].RecipeId = existing.Id;
            recipe.Instructions[i].StepNumber = i + 1;
        }
        existing.Instructions = recipe.Instructions;

        // Update categories
        context.RecipeCategories.RemoveRange(existing.RecipeCategories);
        if (categoryIds != null && categoryIds.Count > 0)
        {
            existing.RecipeCategories = categoryIds
                .Distinct()
                .Select(catId => new RecipeCategory { RecipeId = existing.Id, CategoryId = catId })
                .ToList();
        }
        else
        {
            existing.RecipeCategories.Clear();
        }

        await context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Uppdaterade recept {Title} ({Id})", existing.Title, existing.Id);
        return existing;
    }

    public async Task DeleteRecipeAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var recipe = await context.Recipes.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe == null) return;

        var imagePath = recipe.ImagePath;
        context.Recipes.Remove(recipe);
        await context.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            await _imageStorage.DeleteImageAsync(imagePath, cancellationToken);
        }

        _logger.LogInformation("Raderade recept {Id}", id);
    }

    public async Task<bool> ToggleFavoriteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var recipe = await context.Recipes.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe == null) return false;

        recipe.IsFavorite = !recipe.IsFavorite;
        recipe.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        return recipe.IsFavorite;
    }

    public async Task<Recipe> DuplicateRecipeAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var original = await context.Recipes
            .AsNoTracking()
            .Include(r => r.RecipeCategories)
            .Include(r => r.Ingredients)
            .Include(r => r.Instructions)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (original == null)
        {
            throw new InvalidOperationException($"Recept med id {id} hittades inte.");
        }

        var copy = new Recipe
        {
            Title = $"{original.Title} (Kopia)",
            Description = original.Description,
            SourceUrl = original.SourceUrl,
            SourceHost = original.SourceHost,
            ImagePath = original.ImagePath, // shares image reference
            PrepTimeMinutes = original.PrepTimeMinutes,
            CookTimeMinutes = original.CookTimeMinutes,
            Servings = original.Servings,
            ServingsUnit = original.ServingsUnit,
            Notes = original.Notes,
            IsFavorite = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Ingredients = original.Ingredients.Select(i => new Ingredient
            {
                RawText = i.RawText,
                Quantity = i.Quantity,
                QuantityMax = i.QuantityMax,
                Unit = i.Unit,
                Name = i.Name,
                Comment = i.Comment,
                Preparation = i.Preparation,
                GroupHeader = i.GroupHeader,
                OrderIndex = i.OrderIndex
            }).ToList(),
            Instructions = original.Instructions.Select(s => new InstructionStep
            {
                StepNumber = s.StepNumber,
                InstructionText = s.InstructionText,
                GroupHeader = s.GroupHeader
            }).ToList(),
            RecipeCategories = original.RecipeCategories.Select(rc => new RecipeCategory
            {
                CategoryId = rc.CategoryId
            }).ToList()
        };

        context.Recipes.Add(copy);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Duplicerade recept {OriginalId} -> nytt id {NewId}", id, copy.Id);
        return copy;
    }
}

