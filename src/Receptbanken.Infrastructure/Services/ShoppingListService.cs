using Microsoft.EntityFrameworkCore;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

public class ShoppingListService
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ShoppingListAggregator _aggregator;
    private readonly SwedishIngredientParser _ingredientParser;
    private readonly ShoppingListNotifier _notifier;
    private readonly HashSet<int> _deletedItemIds = new();

    public ShoppingListService(
        IDbContextFactory<AppDbContext> dbFactory,
        ShoppingListAggregator aggregator,
        SwedishIngredientParser ingredientParser,
        ShoppingListNotifier? notifier = null)
    {
        _dbFactory = dbFactory;
        _aggregator = aggregator;
        _ingredientParser = ingredientParser;
        _notifier = notifier ?? new ShoppingListNotifier();
    }

    public ShoppingListNotifier Notifier => _notifier;

    public long CurrentVersion => _notifier.Version;

    public long CurrentOrderingVersion => _notifier.OrderingVersion;

    public event Action<long>? OnChanged
    {
        add => _notifier.OnChanged += value;
        remove => _notifier.OnChanged -= value;
    }

    public async Task<List<ShoppingListItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
    }

    public async Task<int> AddRecipeIngredientsAsync(int recipeId, int? targetServings = null, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
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
            _notifier.NotifyChanged();
            return incoming.Count;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<ShoppingListItem> AddManualItemAsync(string rawInput, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            throw new ArgumentException("Ange en vara att lägga till i inköpslistan.", nameof(rawInput));

        var parsed = _ingredientParser.Parse(rawInput.Trim());
        var item = _aggregator.CreateFromRecipeIngredient(parsed, baseServings: 1, targetServings: 1, recipeId: null);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var mergedList = await MergeAndPersistAsync(db, [item], cancellationToken);
            _notifier.NotifyChanged();

            var matched = mergedList.LastOrDefault(x =>
                !x.IsChecked &&
                _aggregator.AreSameIngredient(x.Name, item.Name));

            return matched ?? mergedList.Last();
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Updates an existing shopping list item while preserving its existing <see cref="ShoppingListItem.Comment"/>.
    /// Returns <c>true</c> if the item existed and was updated, or <c>false</c> if the item had already been deleted.
    /// </summary>
    public async Task<bool> UpdateItemAsync(int id, decimal? quantity, string? unit, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Varans namn får inte vara tomt.", nameof(name));

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (existing == null)
            {
                return false;
            }

            if (existing.Quantity != quantity)
            {
                existing.QuantityMax = null;
            }

            existing.Quantity = quantity;
            existing.Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
            existing.Name = name.Trim();
            existing.RawText = _aggregator.FormatRawText(existing.Quantity, existing.QuantityMax, existing.Unit, existing.Name);

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Updates an existing shopping list item including optional interval max quantity and comment.
    /// Returns <c>true</c> if the item existed and was updated, or <c>false</c> if the item had already been deleted.
    /// </summary>
    public async Task<bool> UpdateItemAsync(
        int id,
        decimal? quantity,
        decimal? quantityMax,
        string? unit,
        string name,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Varans namn får inte vara tomt.", nameof(name));

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (existing == null)
            {
                return false;
            }

            existing.Quantity = quantity;
            existing.QuantityMax = (quantity.HasValue && quantityMax.HasValue && quantityMax.Value > quantity.Value)
                ? quantityMax
                : null;
            existing.Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
            existing.Name = name.Trim();
            existing.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
            existing.RawText = _aggregator.FormatRawText(existing.Quantity, existing.QuantityMax, existing.Unit, existing.Name);

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Idempotently sets the checked state of a shopping list item to <paramref name="isChecked"/>.
    /// Returns <c>true</c> if the item existed (even if already in the desired state),
    /// or <c>false</c> if the item had already been deleted by another session.
    /// Preserves <see cref="ShoppingListItem.SortOrder"/> so restoring an item places it back in its saved position.
    /// </summary>
    public async Task<bool> SetCheckedAsync(int id, bool isChecked, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (existing == null)
            {
                return false;
            }

            if (existing.IsChecked != isChecked)
            {
                existing.IsChecked = isChecked;
                await db.SaveChangesAsync(cancellationToken);
                _notifier.NotifyChanged();
            }

            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<bool> ToggleCheckedAsync(int id, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("Varan hittades inte i inköpslistan.");

            existing.IsChecked = !existing.IsChecked;
            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return existing.IsChecked;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<bool> DeleteItemAsync(int id, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.ShoppingListItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (existing == null)
            {
                return false;
            }

            _deletedItemIds.Add(existing.Id);
            db.ShoppingListItems.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Restores a previously deleted shopping list item from an in-memory snapshot (Undo delete),
    /// preserving its original identity, metadata, checked state, and sort order without merging
    /// or overwriting concurrent items.
    /// </summary>
    public async Task<bool> RestoreDeletedItemAsync(ShoppingListItem snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.Name))
        {
            return false;
        }

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            if (snapshot.Id > 0 &&
                await db.ShoppingListItems.AnyAsync(x => x.Id == snapshot.Id, cancellationToken))
            {
                return false;
            }

            int? validRecipeId = null;
            if (snapshot.RecipeId.HasValue &&
                await db.Recipes.AnyAsync(r => r.Id == snapshot.RecipeId.Value, cancellationToken))
            {
                validRecipeId = snapshot.RecipeId.Value;
            }

            int sortOrder = snapshot.SortOrder;
            if (sortOrder <= 0)
            {
                var maxSortOrder = await db.ShoppingListItems
                    .Select(x => (int?)x.SortOrder)
                    .MaxAsync(cancellationToken) ?? 0;
                sortOrder = maxSortOrder + 1;
            }

            var trimmedName = snapshot.Name.Trim();
            var trimmedUnit = string.IsNullOrWhiteSpace(snapshot.Unit) ? null : snapshot.Unit.Trim();
            var trimmedComment = string.IsNullOrWhiteSpace(snapshot.Comment) ? null : snapshot.Comment.Trim();
            var qtyMax = (snapshot.Quantity.HasValue && snapshot.QuantityMax.HasValue && snapshot.QuantityMax.Value > snapshot.Quantity.Value)
                ? snapshot.QuantityMax
                : null;
            var rawText = !string.IsNullOrWhiteSpace(snapshot.RawText)
                ? snapshot.RawText.Trim()
                : _aggregator.FormatRawText(snapshot.Quantity, qtyMax, trimmedUnit, trimmedName);

            var restored = new ShoppingListItem
            {
                Id = snapshot.Id > 0 ? snapshot.Id : 0,
                Name = trimmedName,
                RawText = rawText,
                Quantity = snapshot.Quantity,
                QuantityMax = qtyMax,
                Unit = trimmedUnit,
                Comment = trimmedComment,
                RecipeId = validRecipeId,
                Department = string.IsNullOrWhiteSpace(snapshot.Department) ? null : snapshot.Department.Trim(),
                IsChecked = snapshot.IsChecked,
                SortOrder = sortOrder,
                CreatedAt = snapshot.CreatedAt != default ? snapshot.CreatedAt : DateTimeOffset.UtcNow
            };

            db.ShoppingListItems.Add(restored);
            await db.SaveChangesAsync(cancellationToken);
            _deletedItemIds.Remove(restored.Id);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<int> ClearCheckedItemsAsync(CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var checkedItems = await db.ShoppingListItems
                .Where(x => x.IsChecked)
                .ToListAsync(cancellationToken);

            if (checkedItems.Count == 0)
                return 0;

            foreach (var item in checkedItems)
            {
                _deletedItemIds.Add(item.Id);
            }

            db.ShoppingListItems.RemoveRange(checkedItems);
            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return checkedItems.Count;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task ClearAllItemsAsync(CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var allItems = await db.ShoppingListItems.ToListAsync(cancellationToken);
            if (allItems.Count > 0)
            {
                foreach (var item in allItems)
                {
                    _deletedItemIds.Add(item.Id);
                }

                db.ShoppingListItems.RemoveRange(allItems);
                await db.SaveChangesAsync(cancellationToken);
                _notifier.NotifyChanged();
            }
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Moves a single unchecked shopping list item to <paramref name="targetIndex"/> (0-based within Att handla),
    /// shifting intermediate unchecked items while preserving their relative order.
    /// Validates <paramref name="expectedOrderingVersion"/> when provided to prevent stale concurrent overwrites.
    /// </summary>
    public async Task<ShoppingListReorderResult> MoveItemAsync(
        int itemId,
        int targetIndex,
        long? expectedOrderingVersion = null,
        CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            if (itemId <= 0 || targetIndex < 0)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.InvalidOperation,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Ogiltig flytt av vara i inköpslistan.");
            }

            if (expectedOrderingVersion.HasValue && expectedOrderingVersion.Value != _notifier.OrderingVersion)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.VersionConflict,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Ordningen ändrades nyss på en annan enhet. Listan har lästs in på nytt.");
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var allTracked = await db.ShoppingListItems
                .OrderBy(x => x.IsChecked)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            var targetItem = allTracked.FirstOrDefault(x => x.Id == itemId);
            if (targetItem == null)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.InvalidOperation,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Varan som skulle flyttas finns inte längre i inköpslistan.");
            }

            if (targetItem.IsChecked)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.InvalidOperation,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Endast varor under Att handla kan sorteras om.");
            }

            var uncheckedItems = allTracked.Where(x => !x.IsChecked).ToList();
            var currentIndex = uncheckedItems.FindIndex(x => x.Id == itemId);
            var clampedTargetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, uncheckedItems.Count - 1));

            bool hadLegacyZeroSortOrder = uncheckedItems.Any(x => x.SortOrder <= 0);
            if (currentIndex == clampedTargetIndex && !hadLegacyZeroSortOrder)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.NoChange,
                    _notifier.OrderingVersion,
                    currentItems);
            }

            uncheckedItems.RemoveAt(currentIndex);
            uncheckedItems.Insert(clampedTargetIndex, targetItem);

            for (int i = 0; i < uncheckedItems.Count; i++)
            {
                uncheckedItems[i].SortOrder = i + 1;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _notifier.NotifyOrderingChanged();

            var updatedItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
            return new ShoppingListReorderResult(
                ShoppingListReorderStatus.Success,
                _notifier.OrderingVersion,
                updatedItems);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Validates and persists the desired ordering of unchecked items in Att handla atomically.
    /// Rejects stale ordering versions, duplicate/invalid IDs, and preserves concurrent check-offs,
    /// deletions, and newly added items without overwriting item fields other than <see cref="ShoppingListItem.SortOrder"/>.
    /// </summary>
    public async Task<ShoppingListReorderResult> ReorderItemsAsync(
        IReadOnlyList<int> orderedUncheckedIds,
        long? expectedOrderingVersion = null,
        CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            if (orderedUncheckedIds == null || orderedUncheckedIds.Count == 0)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.InvalidOperation,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Ingen giltig sorteringsordning skickades.");
            }

            if (orderedUncheckedIds.Any(id => id <= 0) ||
                orderedUncheckedIds.Distinct().Count() != orderedUncheckedIds.Count)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.InvalidOperation,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Sorteringsordningen innehåller ogiltiga eller dubblerade varu-ID:n.");
            }

            if (expectedOrderingVersion.HasValue && expectedOrderingVersion.Value != _notifier.OrderingVersion)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.VersionConflict,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Ordningen ändrades nyss på en annan enhet. Listan har lästs in på nytt.");
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var allTracked = await db.ShoppingListItems
                .OrderBy(x => x.IsChecked)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            var allById = allTracked.ToDictionary(x => x.Id);

            // Reject IDs that neither exist in DB nor were previously deleted from the shopping list.
            foreach (var requestedId in orderedUncheckedIds)
            {
                if (!allById.ContainsKey(requestedId) && !_deletedItemIds.Contains(requestedId))
                {
                    var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                    return new ShoppingListReorderResult(
                        ShoppingListReorderStatus.InvalidOperation,
                        _notifier.OrderingVersion,
                        currentItems,
                        "En eller flera varor i sorteringen tillhör inte inköpslistan.");
                }
            }

            var currentUnchecked = allTracked.Where(x => !x.IsChecked).ToList();
            var currentUncheckedById = currentUnchecked.ToDictionary(x => x.Id);

            // Build the new ordered sequence of unchecked items:
            // 1. Items in orderedUncheckedIds that are still unchecked in DB (deleted or checked-off items are safely skipped).
            var newUncheckedOrder = new List<ShoppingListItem>(currentUnchecked.Count);
            var placedIds = new HashSet<int>();

            foreach (var id in orderedUncheckedIds)
            {
                if (currentUncheckedById.TryGetValue(id, out var uncheckedItem) && placedIds.Add(id))
                {
                    newUncheckedOrder.Add(uncheckedItem);
                }
            }

            if (newUncheckedOrder.Count == 0)
            {
                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.InvalidOperation,
                    _notifier.OrderingVersion,
                    currentItems,
                    "Inga återstående varor under Att handla kunde sorteras.");
            }

            // 2. Preserve any unchecked items in DB not present in orderedUncheckedIds (e.g. concurrently added items) at the end.
            foreach (var existingUnchecked in currentUnchecked)
            {
                if (placedIds.Add(existingUnchecked.Id))
                {
                    newUncheckedOrder.Add(existingUnchecked);
                }
            }

            bool orderChanged = false;
            for (int i = 0; i < newUncheckedOrder.Count; i++)
            {
                int desiredSortOrder = i + 1;
                if (newUncheckedOrder[i].Id != currentUnchecked[i].Id ||
                    newUncheckedOrder[i].SortOrder != desiredSortOrder)
                {
                    if (newUncheckedOrder[i].Id != currentUnchecked[i].Id)
                    {
                        orderChanged = true;
                    }
                    newUncheckedOrder[i].SortOrder = desiredSortOrder;
                }
            }

            if (!orderChanged)
            {
                // Even if order didn't change, persist normalized SortOrder if any item had 0
                if (db.ChangeTracker.HasChanges())
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }

                var currentItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
                return new ShoppingListReorderResult(
                    ShoppingListReorderStatus.NoChange,
                    _notifier.OrderingVersion,
                    currentItems);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _notifier.NotifyOrderingChanged();

            var updatedItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
            return new ShoppingListReorderResult(
                ShoppingListReorderStatus.Success,
                _notifier.OrderingVersion,
                updatedItems);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static Task<List<ShoppingListItem>> QueryOrderedItemsNoTrackingAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        return db.ShoppingListItems
            .AsNoTracking()
            .Include(x => x.Recipe)
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<ShoppingListItem>> MergeAndPersistAsync(
        AppDbContext db,
        List<ShoppingListItem> incoming,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var trackedExisting = await db.ShoppingListItems
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        // Ensure deterministic SortOrder for any legacy rows that still have SortOrder <= 0
        int maxSortOrder = trackedExisting.Count == 0 ? 0 : trackedExisting.Max(x => x.SortOrder);
        if (trackedExisting.Any(x => x.SortOrder <= 0))
        {
            int seq = 0;
            foreach (var existing in trackedExisting
                .OrderBy(x => x.SortOrder <= 0 ? int.MaxValue : x.SortOrder)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id))
            {
                seq++;
                if (existing.SortOrder <= 0)
                {
                    existing.SortOrder = Math.Max(seq, maxSortOrder + 1);
                }
                if (existing.SortOrder > maxSortOrder)
                {
                    maxSortOrder = existing.SortOrder;
                }
            }
        }

        var merged = _aggregator.MergeItems(trackedExisting, incoming);

        foreach (var item in merged)
        {
            if (item.Id == 0 && !trackedExisting.Contains(item))
            {
                item.SortOrder = ++maxSortOrder;
                db.ShoppingListItems.Add(item);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return merged;
    }
}
