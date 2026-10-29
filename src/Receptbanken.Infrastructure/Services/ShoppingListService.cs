using Microsoft.EntityFrameworkCore;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

public class ShoppingListService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ShoppingListAggregator _aggregator;
    private readonly SwedishIngredientParser _ingredientParser;

    public ShoppingListService(
        IDbContextFactory<AppDbContext> dbFactory,
        ShoppingListAggregator aggregator,
        SwedishIngredientParser ingredientParser)
    {
        _dbFactory = dbFactory;
        _aggregator = aggregator;
        _ingredientParser = ingredientParser;
    }

    public async Task<List<ShoppingListItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ShoppingListItems
            .AsNoTracking()
            .Include(x => x.Recipe)
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> AddRecipeIngredientsAsync(int recipeId, int? targetServings = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var recipe = await db.Recipes
            .AsNoTracking()
            .Include(r => r.Ingredients.OrderBy(i => i.OrderIndex))
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recept med ID {recipeId} hittades inte.");

        var servings = targetServings.HasValue && targetServings.Value > 0
            ? targetServings.Value
            : recipe.Servings;

        var incoming = recipe.Ingredients
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => _aggregator.CreateFromRecipeIngredient(i, recipe.Servings, servings, recipe.Id))
            .ToList();

        if (incoming.Count == 0)
            return 0;

        await MergeAndPersistAsync(db, incoming, cancellationToken);
        return incoming.Count;
    }

    public async Task<ShoppingListItem> AddManualItemAsync(string rawInput, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            throw new ArgumentException("Ange en vara att lägga till i inköpslistan.", nameof(rawInput));

        var parsed = _ingredientParser.Parse(rawInput.Trim());
        var item = new ShoppingListItem
        {
            Name = parsed.Name,
            Quantity = parsed.Quantity,
            Unit = parsed.Unit,
            RawText = _aggregator.FormatRawText(parsed.Quantity, parsed.Unit, parsed.Name),
            IsChecked = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var mergedList = await MergeAndPersistAsync(db, [item], cancellationToken);
        return mergedList.Last();
    }

    public async Task UpdateItemAsync(int id, decimal? quantity, string? unit, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Varans namn får inte vara tomt.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Varan hittades inte i inköpslistan.");

        existing.Quantity = quantity;
        existing.Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        existing.Name = name.Trim();
        existing.RawText = _aggregator.FormatRawText(existing.Quantity, existing.Unit, existing.Name);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ToggleCheckedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Varan hittades inte i inköpslistan.");

        existing.IsChecked = !existing.IsChecked;
        await db.SaveChangesAsync(cancellationToken);
        return existing.IsChecked;
    }

    public async Task DeleteItemAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (existing != null)
        {
            db.ShoppingListItems.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> ClearCheckedItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var checkedItems = await db.ShoppingListItems
            .Where(x => x.IsChecked)
            .ToListAsync(cancellationToken);

        if (checkedItems.Count == 0)
            return 0;

        db.ShoppingListItems.RemoveRange(checkedItems);
        await db.SaveChangesAsync(cancellationToken);
        return checkedItems.Count;
    }

    public async Task ClearAllItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var allItems = await db.ShoppingListItems.ToListAsync(cancellationToken);
        if (allItems.Count > 0)
        {
            db.ShoppingListItems.RemoveRange(allItems);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<List<ShoppingListItem>> MergeAndPersistAsync(
        AppDbContext db,
        List<ShoppingListItem> incoming,
        CancellationToken cancellationToken)
    {
        var trackedExisting = await db.ShoppingListItems
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var merged = _aggregator.MergeItems(trackedExisting, incoming);

        foreach (var item in merged)
        {
            if (item.Id == 0 && !trackedExisting.Contains(item))
            {
                db.ShoppingListItems.Add(item);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return merged;
    }
}

