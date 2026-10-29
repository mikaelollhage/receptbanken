using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

public class CategoryWithCountDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ColorHex { get; set; }
    public int RecipeCount { get; set; }
}

public class CategoryService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<CategoryService> _logger;

    public CategoryService(IDbContextFactory<AppDbContext> contextFactory, ILogger<CategoryService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<List<CategoryWithCountDto>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Categories
            .AsNoTracking()
            .Select(c => new CategoryWithCountDto
            {
                Id = c.Id,
                Name = c.Name,
                ColorHex = c.ColorHex,
                RecipeCount = c.RecipeCategories.Count
            })
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Category?> GetCategoryByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Category> CreateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var trimmedName = category.Name.Trim();
        var exists = await context.Categories.AnyAsync(c => c.Name.ToLower() == trimmedName.ToLower(), cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"En kategori med namnet '{trimmedName}' finns redan.");
        }

        category.Name = trimmedName;
        context.Categories.Add(category);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Skapade kategori {Name} ({Id})", category.Name, category.Id);
        return category;
    }

    public async Task<Category> UpdateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.Categories.FirstOrDefaultAsync(c => c.Id == category.Id, cancellationToken);
        if (existing == null)
        {
            throw new InvalidOperationException($"Kategori med id {category.Id} hittades inte.");
        }

        var trimmedName = category.Name.Trim();
        var exists = await context.Categories.AnyAsync(c => c.Id != category.Id && c.Name.ToLower() == trimmedName.ToLower(), cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"En kategori med namnet '{trimmedName}' finns redan.");
        }

        existing.Name = trimmedName;
        existing.ColorHex = category.ColorHex;

        await context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Uppdaterade kategori {Name} ({Id})", existing.Name, existing.Id);
        return existing;
    }

    public async Task DeleteCategoryAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var category = await context.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category == null) return;

        context.Categories.Remove(category);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Raderade kategori {Id}", id);
    }
}

