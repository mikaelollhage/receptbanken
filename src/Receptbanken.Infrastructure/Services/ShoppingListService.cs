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

    /// <summary>
    /// Optional hook for testing transactional rollback when an error occurs right before committing weekly menu shopping additions.
    /// </summary>
    public Action? BeforeWeeklyMenuCommitTestHook { get; set; }

    /// <summary>
    /// Optional hook for testing transactional rollback when an error occurs right before committing recipe shopping additions.
    /// </summary>
    public Action? BeforeRecipeShoppingCommitTestHook { get; set; }

    /// <summary>
    /// Optional hook for testing transactional error handling when an error occurs while saving a specific item during voice add.
    /// </summary>
    public Action<ShoppingListItem>? BeforeVoiceItemSaveTestHook { get; set; }

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
    /// Lägger till varor via röststyrning (API-1B). Tolkar och validerar varje vara individuellt,
    /// kontrollerar om varan redan finns i den aktuella inköpslistan (!IsChecked), och sparar
    /// nya varor transaktionellt utan att ändra befintliga poster.
    /// </summary>
    public async Task<VoiceShoppingListAddResponse> AddVoiceItemsAsync(
        VoiceShoppingListRequest request,
        VoiceShoppingListParser parser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parser);

        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ArgumentException("Minst en varufras måste anges i 'items'.", nameof(request));
        }

        if (request.Items.Count > VoiceShoppingListParser.MaxItemsPerRequest)
        {
            throw new ArgumentException($"Maximalt {VoiceShoppingListParser.MaxItemsPerRequest} varufraser kan tolkas per anrop.", nameof(request));
        }

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var currentItems = await db.ShoppingListItems
                .OrderBy(x => x.IsChecked)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            int maxSortOrder = currentItems.Count == 0 ? 0 : currentItems.Max(x => x.SortOrder);
            var results = new List<VoiceShoppingListItemResultDto>();
            int addedCount = 0;
            int alreadyExistsCount = 0;
            int errorCount = 0;

            foreach (var rawPhrase in request.Items)
            {
                // 1. Tolka och validera frasen med VoiceShoppingListParser
                var preview = parser.ParseItem(rawPhrase);
                if (!preview.Success)
                {
                    results.Add(new VoiceShoppingListItemResultDto
                    {
                        RawPhrase = preview.RawPhrase,
                        Status = VoiceShoppingListItemStatus.Error,
                        DisplayText = null,
                        Error = preview.Error
                    });
                    errorCount++;
                    continue;
                }

                // 2. Kontrollera om varan redan finns i den aktuella inköpslistan (!IsChecked)
                var exists = currentItems.Any(existing =>
                    !existing.IsChecked &&
                    _aggregator.AreSameIngredient(existing.Name, preview.Name));

                if (exists)
                {
                    results.Add(new VoiceShoppingListItemResultDto
                    {
                        RawPhrase = preview.RawPhrase,
                        Status = VoiceShoppingListItemStatus.AlreadyExists,
                        DisplayText = preview.DisplayText,
                        Error = null
                    });
                    alreadyExistsCount++;
                    continue;
                }

                // 3. Om den saknas: spara via transaktion för enskild vara
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    var newItem = new ShoppingListItem
                    {
                        Name = preview.Name!,
                        Quantity = preview.Quantity,
                        QuantityMax = preview.QuantityMax,
                        Unit = preview.Unit,
                        Comment = preview.Comment,
                        RawText = preview.DisplayText ?? preview.RawPhrase,
                        IsChecked = false,
                        SortOrder = ++maxSortOrder,
                        CreatedAt = DateTimeOffset.UtcNow
                    };

                    BeforeVoiceItemSaveTestHook?.Invoke(newItem);

                    db.ShoppingListItems.Add(newItem);
                    await db.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);

                    currentItems.Add(newItem);
                    results.Add(new VoiceShoppingListItemResultDto
                    {
                        RawPhrase = preview.RawPhrase,
                        Status = VoiceShoppingListItemStatus.Added,
                        DisplayText = preview.DisplayText,
                        Error = null
                    });
                    addedCount++;
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    results.Add(new VoiceShoppingListItemResultDto
                    {
                        RawPhrase = preview.RawPhrase,
                        Status = VoiceShoppingListItemStatus.Error,
                        DisplayText = null,
                        Error = $"Kunde inte spara varan i inköpslistan: {ex.Message}"
                    });
                    errorCount++;
                }
            }

            if (addedCount > 0)
            {
                _notifier.NotifyChanged();
            }

            return new VoiceShoppingListAddResponse
            {
                Success = errorCount == 0,
                TotalCount = results.Count,
                AddedCount = addedCount,
                AlreadyExistsCount = alreadyExistsCount,
                ErrorCount = errorCount,
                Items = results
            };
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

            db.ShoppingListItems.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            _notifier.RecordDeletedItem(existing.Id);
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
            _notifier.RemoveDeletedItem(restored.Id);
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

            var deletedIds = checkedItems.Select(x => x.Id).ToList();
            db.ShoppingListItems.RemoveRange(checkedItems);
            await db.SaveChangesAsync(cancellationToken);
            _notifier.RecordDeletedItems(deletedIds);
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
                var deletedIds = allItems.Select(x => x.Id).ToList();
                db.ShoppingListItems.RemoveRange(allItems);
                await db.SaveChangesAsync(cancellationToken);
                _notifier.RecordDeletedItems(deletedIds);
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
                if (!allById.ContainsKey(requestedId) && !_notifier.WasItemDeleted(requestedId))
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

    public async Task<WeeklyMenuShoppingPreview> BuildWeeklyMenuPreviewAsync(
        DateOnly dateInWeek,
        CancellationToken cancellationToken = default)
    {
        var week = CalendarWeek.FromDate(dateInWeek);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildWeeklyMenuPreviewInternalAsync(db, week, inMemoryMenuOverride: null, cancellationToken);
    }

    public async Task<WeeklyMenuShoppingPreview> BuildWeeklyMenuPreviewAsync(
        WeeklyMenu menu,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var week = CalendarWeek.FromDate(menu.WeekStartDate);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildWeeklyMenuPreviewInternalAsync(db, week, inMemoryMenuOverride: menu, cancellationToken);
    }

    public async Task<WeeklyMenuShoppingConfirmResult> ConfirmWeeklyMenuAdditionsAsync(
        DateOnly dateInWeek,
        string expectedPreviewStamp,
        IReadOnlyCollection<string> selectedPreviewItemKeys,
        CancellationToken cancellationToken = default)
    {
        var week = CalendarWeek.FromDate(dateInWeek);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var currentPreview = await BuildWeeklyMenuPreviewInternalAsync(db, week, inMemoryMenuOverride: null, cancellationToken);
            if (!currentPreview.HasSavedMenu || !currentPreview.WeeklyMenuId.HasValue)
            {
                return new WeeklyMenuShoppingConfirmResult(
                    WeeklyMenuConfirmStatus.MenuNotFound,
                    AddedItemsCount: 0,
                    AlreadyAddedCount: 0,
                    ConflictCount: 0,
                    UpdatedPreview: currentPreview,
                    Message: $"Ingen sparad veckomeny hittades för vecka {week.IsoWeek}, {week.IsoYear}.");
            }

            if (currentPreview.Status != WeeklyMenuStatus.Confirmed)
            {
                return new WeeklyMenuShoppingConfirmResult(
                    WeeklyMenuConfirmStatus.MenuNotConfirmed,
                    AddedItemsCount: 0,
                    AlreadyAddedCount: currentPreview.AlreadyAddedCount,
                    ConflictCount: 0,
                    UpdatedPreview: currentPreview,
                    Message: "Endast spikade veckomenyer kan användas som underlag för inköpslistan. Spika menyn först.");
            }

            if (!string.Equals(expectedPreviewStamp, currentPreview.PreviewStamp, StringComparison.Ordinal))
            {
                return new WeeklyMenuShoppingConfirmResult(
                    WeeklyMenuConfirmStatus.Conflict,
                    AddedItemsCount: 0,
                    AlreadyAddedCount: currentPreview.AlreadyAddedCount,
                    ConflictCount: 0,
                    UpdatedPreview: currentPreview,
                    Message: "Veckomenyn eller dess inköpstillägg har ändrats sedan förhandsgranskningen öppnades. Förhandsgranskningen har uppdaterats.");
            }

            var selectedSet = (selectedPreviewItemKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToHashSet(StringComparer.Ordinal);

            var selectedItems = currentPreview.Items
                .Where(x => selectedSet.Contains(x.PreviewItemKey))
                .ToList();

            if (selectedItems.Count == 0)
            {
                return new WeeklyMenuShoppingConfirmResult(
                    WeeklyMenuConfirmStatus.NoSelection,
                    AddedItemsCount: 0,
                    AlreadyAddedCount: currentPreview.AlreadyAddedCount,
                    ConflictCount: 0,
                    UpdatedPreview: currentPreview,
                    Message: "Inga varor var valda för tillägg.");
            }

            var incomingShoppingItems = selectedItems
                .Select(x => x.Item)
                .ToList();

            await MergeIntoTrackedShoppingListAsync(db, incomingShoppingItems, cancellationToken);

            var menuId = currentPreview.WeeklyMenuId.Value;
            var trackedContributions = await db.WeeklyMenuShoppingContributions
                .Where(c => c.WeeklyMenuId == menuId)
                .ToListAsync(cancellationToken);

            long maxExistingTicks = trackedContributions.Count > 0
                ? trackedContributions.Max(c => c.UpdatedAt.UtcTicks)
                : 0L;
            long nextTicks = Math.Max(DateTimeOffset.UtcNow.UtcTicks, maxExistingTicks + 1);
            var timestamp = new DateTimeOffset(nextTicks, TimeSpan.Zero);

            foreach (var selectedRow in selectedItems)
            {
                foreach (var contrib in selectedRow.SourceContributions)
                {
                    var targetOnSameDay = trackedContributions.FirstOrDefault(c =>
                        c.DayIndex == contrib.DayIndex &&
                        c.RecipeId == contrib.RecipeId &&
                        string.Equals(c.ContributionKey, contrib.ContributionKey, StringComparison.OrdinalIgnoreCase));

                    WeeklyMenuShoppingContribution? matchedById = contrib.ExistingContributionId.HasValue
                        ? trackedContributions.FirstOrDefault(c => c.Id == contrib.ExistingContributionId.Value)
                        : null;

                    if (targetOnSameDay != null)
                    {
                        if (matchedById != null && matchedById.Id != targetOnSameDay.Id)
                        {
                            db.WeeklyMenuShoppingContributions.Remove(matchedById);
                            trackedContributions.Remove(matchedById);
                        }

                        targetOnSameDay.WeekStartDate = week.WeekStartDate;
                        targetOnSameDay.TargetServings = Math.Max(targetOnSameDay.TargetServings, contrib.PlannedServings);
                        targetOnSameDay.AddedQuantity = contrib.FullQuantity;
                        targetOnSameDay.AddedQuantityMax = contrib.FullQuantityMax;
                        targetOnSameDay.Unit = contrib.Unit;
                        targetOnSameDay.UpdatedAt = timestamp;
                    }
                    else if (matchedById != null)
                    {
                        matchedById.WeekStartDate = week.WeekStartDate;
                        matchedById.DayIndex = contrib.DayIndex;
                        matchedById.RecipeId = contrib.RecipeId;
                        matchedById.ContributionKey = contrib.ContributionKey;
                        matchedById.TargetServings = Math.Max(matchedById.TargetServings, contrib.PlannedServings);
                        matchedById.AddedQuantity = contrib.FullQuantity;
                        matchedById.AddedQuantityMax = contrib.FullQuantityMax;
                        matchedById.Unit = contrib.Unit;
                        matchedById.UpdatedAt = timestamp;
                    }
                    else
                    {
                        var created = new WeeklyMenuShoppingContribution
                        {
                            WeeklyMenuId = menuId,
                            WeekStartDate = week.WeekStartDate,
                            DayIndex = contrib.DayIndex,
                            RecipeId = contrib.RecipeId,
                            ContributionKey = contrib.ContributionKey,
                            TargetServings = contrib.PlannedServings,
                            AddedQuantity = contrib.FullQuantity,
                            AddedQuantityMax = contrib.FullQuantityMax,
                            Unit = contrib.Unit,
                            UpdatedAt = timestamp
                        };
                        db.WeeklyMenuShoppingContributions.Add(created);
                        trackedContributions.Add(created);
                    }
                }
            }

            BeforeWeeklyMenuCommitTestHook?.Invoke();

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _notifier.NotifyChanged();

            var updatedPreview = await BuildWeeklyMenuPreviewInternalAsync(db, week, inMemoryMenuOverride: null, cancellationToken);
            int conflictCount = selectedItems.Count(x => !string.IsNullOrWhiteSpace(x.ConflictWarning));

            var messageParts = new List<string>
            {
                $"{selectedItems.Count} {(selectedItems.Count == 1 ? "vara lades" : "varor lades")} till i inköpslistan från vecka {week.IsoWeek}."
            };
            if (conflictCount > 0)
            {
                messageParts.Add($"{conflictCount} {(conflictCount == 1 ? "vara hölls" : "varor hölls")} som separat rad p.g.a. olika enhet eller förpackning.");
            }

            return new WeeklyMenuShoppingConfirmResult(
                WeeklyMenuConfirmStatus.Success,
                AddedItemsCount: selectedItems.Count,
                AlreadyAddedCount: updatedPreview.AlreadyAddedCount,
                ConflictCount: conflictCount,
                UpdatedPreview: updatedPreview,
                Message: string.Join(" ", messageParts));
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<RecipeShoppingPreview?> BuildRecipeShoppingPreviewAsync(
        int recipeId,
        int? targetServings = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildRecipeShoppingPreviewInternalAsync(db, recipeId, targetServings ?? 0, cancellationToken);
    }

    public async Task<RecipeShoppingConfirmResult> ConfirmRecipeShoppingAdditionsAsync(
        int recipeId,
        int targetServings,
        string expectedPreviewStamp,
        IReadOnlyCollection<string> selectedPreviewItemKeys,
        CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var currentPreview = await BuildRecipeShoppingPreviewInternalAsync(db, recipeId, targetServings, cancellationToken);
            if (currentPreview == null)
            {
                return new RecipeShoppingConfirmResult(
                    RecipeShoppingConfirmStatus.RecipeNotFound,
                    AddedItemsCount: 0,
                    UpdatedPreview: new RecipeShoppingPreview
                    {
                        RecipeId = recipeId,
                        RecipeTitle = string.Empty,
                        BaseServings = 1,
                        TargetServings = targetServings,
                        ServingsUnit = "port",
                        PreviewStamp = string.Empty,
                        Items = []
                    },
                    Message: $"Receptet med ID {recipeId} hittades inte längre.");
            }

            if (!string.Equals(expectedPreviewStamp, currentPreview.PreviewStamp, StringComparison.Ordinal))
            {
                return new RecipeShoppingConfirmResult(
                    RecipeShoppingConfirmStatus.Conflict,
                    AddedItemsCount: 0,
                    UpdatedPreview: currentPreview,
                    Message: "Inköpslistan eller receptet har ändrats sedan förhandsgranskningen öppnades. Förhandsgranskningen har uppdaterats.");
            }

            var selectedSet = (selectedPreviewItemKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToHashSet(StringComparer.Ordinal);

            var selectedItems = currentPreview.Items
                .Where(x => selectedSet.Contains(x.PreviewItemKey))
                .ToList();

            if (selectedItems.Count == 0)
            {
                return new RecipeShoppingConfirmResult(
                    RecipeShoppingConfirmStatus.NoSelection,
                    AddedItemsCount: 0,
                    UpdatedPreview: currentPreview,
                    Message: "Inga varor var valda för tillägg.");
            }

            var incomingShoppingItems = selectedItems
                .Select(x => x.Item)
                .ToList();

            await MergeIntoTrackedShoppingListAsync(db, incomingShoppingItems, cancellationToken);

            BeforeRecipeShoppingCommitTestHook?.Invoke();

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _notifier.NotifyChanged();

            var updatedPreview = await BuildRecipeShoppingPreviewInternalAsync(db, recipeId, targetServings, cancellationToken);
            int conflictCount = selectedItems.Count(x => !string.IsNullOrWhiteSpace(x.ConflictWarning));

            var messageParts = new List<string>
            {
                $"{incomingShoppingItems.Count} {(incomingShoppingItems.Count == 1 ? "vara lades" : "varor lades")} till i inköpslistan från {currentPreview.RecipeTitle}."
            };
            if (conflictCount > 0)
            {
                messageParts.Add($"{conflictCount} {(conflictCount == 1 ? "vara hölls" : "varor hölls")} som separat rad p.g.a. olika enhet eller förpackning.");
            }

            return new RecipeShoppingConfirmResult(
                RecipeShoppingConfirmStatus.Success,
                AddedItemsCount: incomingShoppingItems.Count,
                UpdatedPreview: updatedPreview ?? currentPreview,
                Message: string.Join(" ", messageParts));
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private async Task MergeIntoTrackedShoppingListAsync(
        AppDbContext db,
        List<ShoppingListItem> incoming,
        CancellationToken cancellationToken)
    {
        var trackedExisting = await db.ShoppingListItems
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

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
    }

    private async Task<WeeklyMenuShoppingPreview> BuildWeeklyMenuPreviewInternalAsync(
        AppDbContext db,
        CalendarWeek week,
        WeeklyMenu? inMemoryMenuOverride,
        CancellationToken cancellationToken)
    {
        var dbMenu = await db.WeeklyMenus
            .AsNoTracking()
            .Where(m => m.WeekStartDate == week.WeekStartDate || (m.IsoYear == week.IsoYear && m.IsoWeek == week.IsoWeek))
            .Include(m => m.Days.OrderBy(d => d.DayIndex))
                .ThenInclude(d => d.Recipe)
                    .ThenInclude(r => r!.Ingredients.OrderBy(i => i.OrderIndex))
            .Include(m => m.ShoppingContributions)
            .FirstOrDefaultAsync(cancellationToken);

        WeeklyMenu? effectiveMenu = inMemoryMenuOverride ?? dbMenu;
        if (effectiveMenu == null)
        {
            return new WeeklyMenuShoppingPreview
            {
                CalendarWeek = week,
                WeeklyMenuId = null,
                HasSavedMenu = false,
                Status = WeeklyMenuStatus.Draft,
                PreviewStamp = $"EMPTY:{week.WeekStartDate:yyyy-MM-dd}",
                PlannedMeals = [],
                Items = [],
                HasReplacedOrClearedMealsWithPriorAdditions = false
            };
        }

        var savedContributions = dbMenu?.ShoppingContributions
            .OrderBy(c => c.DayIndex)
            .ThenBy(c => c.Id)
            .ToList() ?? [];

        // Ensure all referenced recipes have their ingredients loaded
        var recipeIdsNeeded = effectiveMenu.Days
            .Where(d => d.RecipeId.HasValue && (d.Recipe == null || d.Recipe.Ingredients.Count == 0))
            .Select(d => d.RecipeId!.Value)
            .Distinct()
            .ToList();

        var recipesById = new Dictionary<int, Recipe>();
        if (recipeIdsNeeded.Count > 0)
        {
            var loadedRecipes = await db.Recipes
                .AsNoTracking()
                .Include(r => r.Ingredients.OrderBy(i => i.OrderIndex))
                .Where(r => recipeIdsNeeded.Contains(r.Id))
                .ToListAsync(cancellationToken);

            foreach (var r in loadedRecipes)
            {
                recipesById[r.Id] = r;
            }
        }

        var orderedDays = effectiveMenu.Days
            .OrderBy(d => d.DayIndex)
            .ToList();

        // Build per-day merged items
        var dayEntries = new List<(WeeklyMenuDay Day, Recipe Recipe, int BaseServings, int PlannedServings, List<(ShoppingListItem FullItem, string ContributionKey)> DayItems)>();
        var activeDayRecipePairs = new HashSet<(int DayIndex, int RecipeId)>();

        foreach (var day in orderedDays)
        {
            if (!day.RecipeId.HasValue)
                continue;

            var recipe = day.Recipe;
            if ((recipe == null || recipe.Ingredients.Count == 0) && recipesById.TryGetValue(day.RecipeId.Value, out var loaded))
            {
                recipe = loaded;
            }
            if (recipe == null)
                continue;

            int baseServings = recipe.Servings > 0 ? recipe.Servings : 4;
            int plannedServings = day.Servings is > 0 ? day.Servings.Value : baseServings;

            var rawDayShoppingItems = recipe.Ingredients
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .OrderBy(i => i.OrderIndex)
                .Select(i => _aggregator.CreateFromRecipeIngredient(i, baseServings, plannedServings, recipe.Id))
                .ToList();

            var mergedWithinDay = _aggregator.MergeItems([], rawDayShoppingItems);
            var keyedDayItems = mergedWithinDay
                .Select(item => (FullItem: item, ContributionKey: _aggregator.GetContributionKey(item)))
                .ToList();

            dayEntries.Add((day, recipe, baseServings, plannedServings, keyedDayItems));
            activeDayRecipePairs.Add((day.DayIndex, recipe.Id));
        }

        // Two-pass matching of saved contributions to active day items:
        // Pass 1: Exact (DayIndex, RecipeId, ContributionKey) match.
        // Pass 2: Unclaimed saved contribution with same (RecipeId, ContributionKey) from a DayIndex where RecipeId is no longer planned (recipe moved/swapped).
        var claimedContributionIds = new HashSet<int>();
        var matchedByDayAndKey = new Dictionary<(int DayIndex, int RecipeId, string Key), WeeklyMenuShoppingContribution>();

        foreach (var entry in dayEntries)
        {
            foreach (var (_, key) in entry.DayItems)
            {
                var exactMatch = savedContributions.FirstOrDefault(c =>
                    !claimedContributionIds.Contains(c.Id) &&
                    c.DayIndex == entry.Day.DayIndex &&
                    c.RecipeId == entry.Recipe.Id &&
                    string.Equals(c.ContributionKey, key, StringComparison.OrdinalIgnoreCase));

                if (exactMatch != null)
                {
                    claimedContributionIds.Add(exactMatch.Id);
                    matchedByDayAndKey[(entry.Day.DayIndex, entry.Recipe.Id, key)] = exactMatch;
                }
            }
        }

        foreach (var entry in dayEntries)
        {
            foreach (var (_, key) in entry.DayItems)
            {
                var lookupKey = (entry.Day.DayIndex, entry.Recipe.Id, key);
                if (matchedByDayAndKey.ContainsKey(lookupKey))
                    continue;

                var movedMatch = savedContributions.FirstOrDefault(c =>
                    !claimedContributionIds.Contains(c.Id) &&
                    !activeDayRecipePairs.Contains((c.DayIndex, c.RecipeId)) &&
                    c.RecipeId == entry.Recipe.Id &&
                    string.Equals(c.ContributionKey, key, StringComparison.OrdinalIgnoreCase));

                if (movedMatch != null)
                {
                    claimedContributionIds.Add(movedMatch.Id);
                    matchedByDayAndKey[lookupKey] = movedMatch;
                }
            }
        }

        bool hasReplacedOrClearedMealsWithPriorAdditions = savedContributions.Any(c => !claimedContributionIds.Contains(c.Id));

        // Evaluate each day's item into pending vs already-added contributions
        var plannedMeals = new List<WeeklyMenuPlannedMealDto>(dayEntries.Count);
        var pendingDayItems = new List<(ShoppingListItem PendingItem, ShoppingListItem FullItem, WeeklyMenuDayContributionDto Contrib, string DayLabel)>();
        var alreadyAddedDayItems = new List<(ShoppingListItem FullItem, WeeklyMenuDayContributionDto Contrib, string DayLabel)>();

        foreach (var entry in dayEntries)
        {
            var dayName = CalendarWeek.GetSwedishDayName(entry.Day.DayIndex);
            int dayTotalItems = entry.DayItems.Count;
            int dayFullyAddedCount = 0;
            int dayPartiallyAddedCount = 0;

            foreach (var (fullItem, key) in entry.DayItems)
            {
                matchedByDayAndKey.TryGetValue((entry.Day.DayIndex, entry.Recipe.Id, key), out var matchedContrib);
                int alreadyAddedServings = matchedContrib?.TargetServings ?? 0;

                if (alreadyAddedServings <= 0)
                {
                    var contribDto = new WeeklyMenuDayContributionDto(
                        ExistingContributionId: null,
                        DayIndex: entry.Day.DayIndex,
                        RecipeId: entry.Recipe.Id,
                        RecipeTitle: entry.Recipe.Title,
                        ContributionKey: key,
                        PlannedServings: entry.PlannedServings,
                        AlreadyAddedServings: 0,
                        FullQuantity: fullItem.Quantity,
                        FullQuantityMax: fullItem.QuantityMax,
                        Unit: fullItem.Unit,
                        IsPortionIncreaseDelta: false);

                    pendingDayItems.Add((
                        PendingItem: CloneShoppingItem(fullItem),
                        FullItem: CloneShoppingItem(fullItem),
                        Contrib: contribDto,
                        DayLabel: $"{dayName} ({entry.PlannedServings} port)"));
                }
                else if (alreadyAddedServings < entry.PlannedServings && fullItem.Quantity.HasValue)
                {
                    // Portions were increased after prior addition -> calculate positive delta
                    decimal prevMin;
                    decimal? prevMax;

                    if (matchedContrib!.AddedQuantity.HasValue &&
                        string.Equals(matchedContrib.Unit, fullItem.Unit, StringComparison.OrdinalIgnoreCase))
                    {
                        prevMin = matchedContrib.AddedQuantity.Value;
                        prevMax = matchedContrib.AddedQuantityMax;
                    }
                    else
                    {
                        var prevRawItems = entry.Recipe.Ingredients
                            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                            .OrderBy(i => i.OrderIndex)
                            .Select(i => _aggregator.CreateFromRecipeIngredient(i, entry.BaseServings, alreadyAddedServings, entry.Recipe.Id))
                            .ToList();
                        var prevMerged = _aggregator.MergeItems([], prevRawItems);
                        var prevMatch = prevMerged.FirstOrDefault(x =>
                            string.Equals(_aggregator.GetContributionKey(x), key, StringComparison.OrdinalIgnoreCase));
                        prevMin = prevMatch?.Quantity ?? 0m;
                        prevMax = prevMatch?.QuantityMax;
                    }

                    decimal deltaMin = Math.Max(0m, Math.Round(fullItem.Quantity.Value - prevMin, 2, MidpointRounding.AwayFromZero));
                    decimal? deltaMax = null;
                    if (fullItem.QuantityMax.HasValue)
                    {
                        var effPrevMax = prevMax ?? prevMin;
                        var candidateMax = Math.Round(fullItem.QuantityMax.Value - effPrevMax, 2, MidpointRounding.AwayFromZero);
                        if (candidateMax > deltaMin)
                        {
                            deltaMax = candidateMax;
                        }
                    }

                    var contribDto = new WeeklyMenuDayContributionDto(
                        ExistingContributionId: matchedContrib.Id,
                        DayIndex: entry.Day.DayIndex,
                        RecipeId: entry.Recipe.Id,
                        RecipeTitle: entry.Recipe.Title,
                        ContributionKey: key,
                        PlannedServings: entry.PlannedServings,
                        AlreadyAddedServings: alreadyAddedServings,
                        FullQuantity: fullItem.Quantity,
                        FullQuantityMax: fullItem.QuantityMax,
                        Unit: fullItem.Unit,
                        IsPortionIncreaseDelta: deltaMin > 0m);

                    if (deltaMin > 0m)
                    {
                        dayPartiallyAddedCount++;
                        var deltaItem = new ShoppingListItem
                        {
                            RecipeId = entry.Recipe.Id,
                            Name = fullItem.Name,
                            Quantity = deltaMin,
                            QuantityMax = deltaMax,
                            Unit = fullItem.Unit,
                            Comment = fullItem.Comment,
                            IsChecked = false,
                            RawText = _aggregator.FormatRawText(deltaMin, deltaMax, fullItem.Unit, fullItem.Name)
                        };

                        int diffServings = entry.PlannedServings - alreadyAddedServings;
                        pendingDayItems.Add((
                            PendingItem: deltaItem,
                            FullItem: CloneShoppingItem(fullItem),
                            Contrib: contribDto,
                            DayLabel: $"{dayName} (+{diffServings} port, {alreadyAddedServings} av {entry.PlannedServings} port redan tillagda)"));
                    }
                    else
                    {
                        dayFullyAddedCount++;
                        alreadyAddedDayItems.Add((
                            FullItem: CloneShoppingItem(fullItem),
                            Contrib: contribDto,
                            DayLabel: $"{dayName} ({entry.PlannedServings} port)"));
                    }
                }
                else
                {
                    dayFullyAddedCount++;
                    var contribDto = new WeeklyMenuDayContributionDto(
                        ExistingContributionId: matchedContrib?.Id,
                        DayIndex: entry.Day.DayIndex,
                        RecipeId: entry.Recipe.Id,
                        RecipeTitle: entry.Recipe.Title,
                        ContributionKey: key,
                        PlannedServings: entry.PlannedServings,
                        AlreadyAddedServings: alreadyAddedServings,
                        FullQuantity: fullItem.Quantity,
                        FullQuantityMax: fullItem.QuantityMax,
                        Unit: fullItem.Unit,
                        IsPortionIncreaseDelta: false);

                    string label = alreadyAddedServings > entry.PlannedServings
                        ? $"{dayName} ({entry.PlannedServings} port, tillagd för {alreadyAddedServings} port)"
                        : $"{dayName} ({entry.PlannedServings} port)";

                    alreadyAddedDayItems.Add((
                        FullItem: CloneShoppingItem(fullItem),
                        Contrib: contribDto,
                        DayLabel: label));
                }
            }

            bool isMealFullyAdded = dayTotalItems > 0 && dayFullyAddedCount == dayTotalItems;
            bool isMealPartiallyAdded = !isMealFullyAdded && (dayFullyAddedCount > 0 || dayPartiallyAddedCount > 0);

            plannedMeals.Add(new WeeklyMenuPlannedMealDto(
                DayIndex: entry.Day.DayIndex,
                DayName: dayName,
                Date: entry.Day.Date,
                RecipeId: entry.Recipe.Id,
                RecipeTitle: entry.Recipe.Title,
                BaseServings: entry.BaseServings,
                PlannedServings: entry.PlannedServings,
                ServingsUnit: entry.Recipe.ServingsUnit,
                IsFullyAdded: isMealFullyAdded,
                IsPartiallyAdded: isMealPartiallyAdded));
        }

        // Group pending items across the week using ShoppingListAggregator
        var pendingGroups = new List<PreviewItemAccumulator>();
        foreach (var p in pendingDayItems)
        {
            var matchGroup = pendingGroups.FirstOrDefault(g => _aggregator.CanMergeItems(g.AggregatedItem, p.PendingItem));
            if (matchGroup != null)
            {
                var mergedList = _aggregator.MergeItems([matchGroup.AggregatedItem], [p.PendingItem]);
                matchGroup.AggregatedItem = mergedList[0];
                matchGroup.Contributions.Add(p.Contrib);
                matchGroup.DayLabels.Add(p.DayLabel);
                if (p.Contrib.IsPortionIncreaseDelta)
                {
                    matchGroup.AlreadyAddedLabels.Add($"{CalendarWeek.GetSwedishDayName(p.Contrib.DayIndex)} ({p.Contrib.AlreadyAddedServings} port redan tillagda)");
                }
            }
            else
            {
                var acc = new PreviewItemAccumulator
                {
                    AggregatedItem = CloneShoppingItem(p.PendingItem),
                    IsAlreadyAdded = false
                };
                acc.Contributions.Add(p.Contrib);
                acc.DayLabels.Add(p.DayLabel);
                if (p.Contrib.IsPortionIncreaseDelta)
                {
                    acc.AlreadyAddedLabels.Add($"{CalendarWeek.GetSwedishDayName(p.Contrib.DayIndex)} ({p.Contrib.AlreadyAddedServings} port redan tillagda)");
                }
                pendingGroups.Add(acc);
            }
        }

        // Attach or group already-added items
        var alreadyAddedGroups = new List<PreviewItemAccumulator>();
        foreach (var a in alreadyAddedDayItems)
        {
            // Check if this already-added item belongs to an ingredient that also has a new pending need on another day
            var matchingPendingGroup = pendingGroups.FirstOrDefault(g => _aggregator.CanMergeItems(g.AggregatedItem, a.FullItem));
            if (matchingPendingGroup != null)
            {
                matchingPendingGroup.AlreadyAddedLabels.Add($"{a.DayLabel} ({a.FullItem.RawText})");
                continue;
            }

            var matchAddedGroup = alreadyAddedGroups.FirstOrDefault(g => _aggregator.CanMergeItems(g.AggregatedItem, a.FullItem));
            if (matchAddedGroup != null)
            {
                var mergedList = _aggregator.MergeItems([matchAddedGroup.AggregatedItem], [a.FullItem]);
                matchAddedGroup.AggregatedItem = mergedList[0];
                matchAddedGroup.Contributions.Add(a.Contrib);
                matchAddedGroup.DayLabels.Add(a.DayLabel);
            }
            else
            {
                var acc = new PreviewItemAccumulator
                {
                    AggregatedItem = CloneShoppingItem(a.FullItem),
                    IsAlreadyAdded = true
                };
                acc.Contributions.Add(a.Contrib);
                acc.DayLabels.Add(a.DayLabel);
                alreadyAddedGroups.Add(acc);
            }
        }

        // Compare against current shopping list (Att handla & Klart)
        var currentShoppingItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
        var uncheckedList = currentShoppingItems.Where(x => !x.IsChecked).ToList();
        var checkedList = currentShoppingItems.Where(x => x.IsChecked).ToList();

        var allAccumulators = pendingGroups.Concat(alreadyAddedGroups).ToList();
        var previewItems = new List<WeeklyMenuPreviewItemDto>(allAccumulators.Count);

        for (int i = 0; i < allAccumulators.Count; i++)
        {
            var acc = allAccumulators[i];
            var item = acc.AggregatedItem;
            var contribKey = _aggregator.GetContributionKey(item);
            var prefix = acc.IsAlreadyAdded ? "ADDED" : "NEW";
            var previewKey = $"{prefix}:{contribKey}:{i}";

            bool hasIntraWeekConflict = allAccumulators.Any(other =>
                !ReferenceEquals(other, acc) &&
                _aggregator.AreSameIngredient(other.AggregatedItem.Name, item.Name) &&
                !_aggregator.CanMergeItems(other.AggregatedItem, item));

            string? intraWeekWarning = hasIntraWeekConflict
                ? "Förekommer med olika enheter eller förpackningsstorlekar i veckomenyn – visas som separata poster"
                : null;

            var overlap = AnalyzeOverlap(item, uncheckedList, checkedList, intraWeekWarning);

            bool wasRemovedFromList = acc.IsAlreadyAdded && !overlap.HasAnyExistingMatch;

            string? alreadyAddedSummary = acc.AlreadyAddedLabels.Count > 0
                ? $"Redan tillagt: {string.Join(", ", acc.AlreadyAddedLabels.Distinct())}"
                : null;

            if (acc.IsAlreadyAdded && acc.Contributions.Any(c => c.AlreadyAddedServings > c.PlannedServings))
            {
                alreadyAddedSummary = "Tidigare tillagd för fler portioner – befintlig mängd i inköpslistan minskas inte automatiskt";
            }

            previewItems.Add(new WeeklyMenuPreviewItemDto
            {
                PreviewItemKey = previewKey,
                Item = item,
                IsAlreadyAdded = acc.IsAlreadyAdded,
                IsSelectedByDefault = !acc.IsAlreadyAdded,
                SourceDaysSummary = string.Join(", ", acc.DayLabels.Distinct()),
                AlreadyAddedSummary = alreadyAddedSummary,
                ExistingInUncheckedText = overlap.ExistingInUncheckedText,
                ExistingInCheckedText = overlap.ExistingInCheckedText,
                ExistingOverlapSummary = acc.IsAlreadyAdded
                    ? null
                    : overlap.ExistingOverlapSummary,
                ConflictWarning = overlap.ConflictWarning,
                WasRemovedFromShoppingList = wasRemovedFromList,
                SourceContributions = acc.Contributions
            });
        }

        string previewStamp = ComputePreviewStamp(effectiveMenu, dayEntries, savedContributions);

        return new WeeklyMenuShoppingPreview
        {
            CalendarWeek = week,
            WeeklyMenuId = dbMenu?.Id ?? (effectiveMenu.Id > 0 ? effectiveMenu.Id : null),
            HasSavedMenu = dbMenu != null || effectiveMenu.Id > 0,
            Status = effectiveMenu.Status,
            PreviewStamp = previewStamp,
            PlannedMeals = plannedMeals,
            Items = previewItems,
            HasReplacedOrClearedMealsWithPriorAdditions = hasReplacedOrClearedMealsWithPriorAdditions
        };
    }

    private static string ComputePreviewStamp(
        WeeklyMenu menu,
        List<(WeeklyMenuDay Day, Recipe Recipe, int BaseServings, int PlannedServings, List<(ShoppingListItem FullItem, string ContributionKey)> DayItems)> dayEntries,
        List<WeeklyMenuShoppingContribution> savedContributions)
    {
        var parts = new List<string>
        {
            $"M:{menu.Id}:{menu.WeekStartDate:yyyy-MM-dd}:{(int)menu.Status}:{menu.UpdatedAt.UtcTicks}"
        };

        foreach (var d in dayEntries)
        {
            parts.Add($"D:{d.Day.DayIndex}:{d.Recipe.Id}:{d.PlannedServings}:{d.Recipe.UpdatedAt.UtcTicks}");
        }

        foreach (var c in savedContributions.OrderBy(x => x.DayIndex).ThenBy(x => x.RecipeId).ThenBy(x => x.ContributionKey))
        {
            parts.Add($"C:{c.Id}:{c.DayIndex}:{c.RecipeId}:{c.ContributionKey}:{c.TargetServings}:{c.UpdatedAt.UtcTicks}");
        }

        return string.Join(";", parts);
    }

    private async Task<RecipeShoppingPreview?> BuildRecipeShoppingPreviewInternalAsync(
        AppDbContext db,
        int recipeId,
        int targetServings,
        CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes
            .AsNoTracking()
            .Include(r => r.Ingredients.OrderBy(i => i.OrderIndex))
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);

        if (recipe == null)
        {
            return null;
        }

        var servings = targetServings > 0 ? targetServings : recipe.Servings;

        var rawItems = recipe.Ingredients
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => _aggregator.CreateFromRecipeIngredient(i, recipe.Servings, servings, recipe.Id))
            .ToList();

        var groupedRecipeItems = _aggregator.MergeItems([], rawItems);

        var currentShoppingItems = await QueryOrderedItemsNoTrackingAsync(db, cancellationToken);
        var uncheckedList = currentShoppingItems.Where(x => !x.IsChecked).ToList();
        var checkedList = currentShoppingItems.Where(x => x.IsChecked).ToList();

        var previewItems = new List<RecipePreviewItemDto>(groupedRecipeItems.Count);

        for (int i = 0; i < groupedRecipeItems.Count; i++)
        {
            var item = groupedRecipeItems[i];
            var contribKey = _aggregator.GetContributionKey(item);
            var previewKey = $"RECIPE:{recipe.Id}:{contribKey}:{i}";

            var overlap = AnalyzeOverlap(item, uncheckedList, checkedList);

            previewItems.Add(new RecipePreviewItemDto
            {
                PreviewItemKey = previewKey,
                Item = item,
                IsSelectedByDefault = true,
                ExistingInUncheckedText = overlap.ExistingInUncheckedText,
                ExistingInCheckedText = overlap.ExistingInCheckedText,
                ExistingOverlapSummary = overlap.ExistingOverlapSummary,
                ConflictWarning = overlap.ConflictWarning
            });
        }

        string previewStamp = ComputeRecipePreviewStamp(recipe, servings, currentShoppingItems);

        return new RecipeShoppingPreview
        {
            RecipeId = recipe.Id,
            RecipeTitle = recipe.Title,
            BaseServings = recipe.Servings,
            TargetServings = servings,
            ServingsUnit = recipe.ServingsUnit,
            PreviewStamp = previewStamp,
            Items = previewItems
        };
    }

    private static string ComputeRecipePreviewStamp(Recipe recipe, int servings, IReadOnlyList<ShoppingListItem> currentShoppingItems)
    {
        var parts = new List<string>
        {
            $"R:{recipe.Id}:{recipe.Servings}:{servings}:{recipe.UpdatedAt.UtcTicks}",
            $"SL:{currentShoppingItems.Count}"
        };
        foreach (var x in currentShoppingItems)
        {
            parts.Add($"{x.Id}:{x.IsChecked}:{x.SortOrder}:{x.Quantity}:{x.QuantityMax}:{x.Unit}:{x.Name}");
        }
        return string.Join(";", parts);
    }

    /// <summary>
    /// Analyzes an incoming item against the current unchecked and checked shopping list items.
    /// Determines compatible matches, unit conflicts, matches in Klart, and computes the overlap summary.
    /// </summary>
    private (string? ExistingInUncheckedText, string? ExistingInCheckedText, string? ExistingOverlapSummary, string? ConflictWarning, bool HasAnyExistingMatch)
        AnalyzeOverlap(
            ShoppingListItem item,
            IReadOnlyList<ShoppingListItem> uncheckedList,
            IReadOnlyList<ShoppingListItem> checkedList,
            string? fallbackConflictWarning = null)
    {
        var compatibleUnchecked = uncheckedList
            .Where(u => _aggregator.CanMergeItems(u, item))
            .Select(u => u.RawText)
            .ToList();

        var conflictingUnchecked = uncheckedList
            .Where(u => _aggregator.AreSameIngredient(u.Name, item.Name) && !_aggregator.CanMergeItems(u, item))
            .Select(u => u.RawText)
            .ToList();

        var matchingChecked = checkedList
            .Where(c => _aggregator.AreSameIngredient(c.Name, item.Name))
            .Select(c => c.RawText)
            .ToList();

        string? conflictWarning = null;
        if (conflictingUnchecked.Count > 0)
        {
            conflictWarning = $"Finns i Att handla som {string.Join(", ", conflictingUnchecked)} (annan enhet/förpackning – läggs som separat rad)";
        }
        else if (!string.IsNullOrWhiteSpace(fallbackConflictWarning))
        {
            conflictWarning = fallbackConflictWarning;
        }

        bool hasAnyExistingMatch = compatibleUnchecked.Count > 0
            || conflictingUnchecked.Count > 0
            || matchingChecked.Count > 0;

        return (
            compatibleUnchecked.Count > 0 ? string.Join(", ", compatibleUnchecked) : null,
            matchingChecked.Count > 0 ? string.Join(", ", matchingChecked) : null,
            BuildExistingOverlapSummary(item, uncheckedList, checkedList),
            conflictWarning,
            hasAnyExistingMatch
        );
    }

    /// <summary>
    /// Builds a short, read-only hint describing matching items already on the shopping list.
    /// Totals are only shown when the real merge would update exactly one unchecked row.
    /// </summary>
    private string? BuildExistingOverlapSummary(
        ShoppingListItem incoming,
        IReadOnlyList<ShoppingListItem> uncheckedList,
        IReadOnlyList<ShoppingListItem> checkedList)
    {
        string Describe(ShoppingListItem x) => x.Quantity.HasValue
            ? _aggregator.FormatRawText(x.Quantity, x.QuantityMax, x.Unit, string.Empty)
            : string.Empty;

        var sameUnchecked = uncheckedList.Where(u => _aggregator.AreSameIngredient(u.Name, incoming.Name)).ToList();
        var sameChecked = checkedList.Where(c => _aggregator.AreSameIngredient(c.Name, incoming.Name)).ToList();

        if (sameUnchecked.Count == 0 && sameChecked.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();

        if (sameUnchecked.Count > 0)
        {
            var quantified = sameUnchecked.Where(u => u.Quantity.HasValue).Select(Describe).ToList();
            if (quantified.Count == 0)
            {
                parts.Add("Finns redan utan angiven mängd – kontrollera om det räcker");
            }
            else
            {
                var text = $"Finns redan: {string.Join(" + ", quantified)}";
                if (sameUnchecked.Count == 1 && sameUnchecked[0].Quantity.HasValue && incoming.Quantity.HasValue)
                {
                    var total = TrySimulateMergedTotal(sameUnchecked[0], incoming, uncheckedList);
                    if (total != null)
                    {
                        text += $" · Efter tillägg: {total}";
                    }
                }
                parts.Add(text);
            }
        }

        if (sameChecked.Count > 0)
        {
            var quantified = sameChecked.Where(c => c.Quantity.HasValue).Select(Describe).ToList();
            parts.Add(quantified.Count > 0
                ? $"Finns redan under Klart: {string.Join(" + ", quantified)}"
                : "Finns redan under Klart utan angiven mängd");
        }

        return string.Join(" · ", parts);
    }

    private string? TrySimulateMergedTotal(
        ShoppingListItem target,
        ShoppingListItem incoming,
        IReadOnlyList<ShoppingListItem> uncheckedList)
    {
        var clones = uncheckedList.Select(CloneShoppingItem).ToList();
        var targetIndex = uncheckedList.ToList().IndexOf(target);
        if (targetIndex < 0)
        {
            return null;
        }
        var targetClone = clones[targetIndex];
        var result = _aggregator.MergeItems(clones, [CloneShoppingItem(incoming)]);

        // Only trust the total if the real merge updates exactly this row and adds no new one
        if (result.Count != clones.Count || !result.Contains(targetClone))
        {
            return null;
        }

        var changedRows = clones
            .Select((c, i) => (c, o: uncheckedList[i]))
            .Where(x => x.c.Quantity != x.o.Quantity || x.c.QuantityMax != x.o.QuantityMax ||
                        !string.Equals(x.c.Unit, x.o.Unit, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.c)
            .ToList();

        if (changedRows.Count != 1 || !ReferenceEquals(changedRows[0], targetClone) || !targetClone.Quantity.HasValue)
        {
            return null;
        }

        return _aggregator.FormatRawText(targetClone.Quantity, targetClone.QuantityMax, targetClone.Unit, string.Empty);
    }

    private static ShoppingListItem CloneShoppingItem(ShoppingListItem src) =>
        new()
        {
            Id = src.Id,
            RecipeId = src.RecipeId,
            Name = src.Name,
            Quantity = src.Quantity,
            QuantityMax = src.QuantityMax,
            Unit = src.Unit,
            Comment = src.Comment,
            Department = src.Department,
            IsChecked = src.IsChecked,
            SortOrder = src.SortOrder,
            CreatedAt = src.CreatedAt,
            RawText = src.RawText
        };

    private sealed class PreviewItemAccumulator
    {
        public required ShoppingListItem AggregatedItem { get; set; }
        public required bool IsAlreadyAdded { get; init; }
        public List<WeeklyMenuDayContributionDto> Contributions { get; } = new();
        public List<string> DayLabels { get; } = new();
        public List<string> AlreadyAddedLabels { get; } = new();
    }
}
