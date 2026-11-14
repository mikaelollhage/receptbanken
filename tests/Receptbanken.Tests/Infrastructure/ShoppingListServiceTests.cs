using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class ShoppingListServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly ShoppingListService _shoppingService;
    private readonly RecipeService _recipeService;

    public ShoppingListServiceTests()
    {
        _dbFactory = new TestDbContextFactory();
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();
        _shoppingService = new ShoppingListService(_dbFactory, aggregator, parser);
        _recipeService = new RecipeService(_dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
    }

    [Fact]
    public async Task AddManualItemAsync_ShouldParseAndMergeCompatibleItems()
    {
        await _shoppingService.AddManualItemAsync("1,5 dl mjölk");
        await _shoppingService.AddManualItemAsync("2,5 dl mjölk");
        await _shoppingService.AddManualItemAsync("500 g smör");

        var items = await _shoppingService.GetItemsAsync();

        items.Should().HaveCount(2);
        var milk = items.Single(x => x.Name == "mjölk");
        milk.Quantity.Should().Be(4m);
        milk.Unit.Should().Be("dl");
    }

    [Fact]
    public async Task AddRecipeIngredientsAsync_WithScaledServings_ShouldScaleAndAggregate()
    {
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Pannkakor",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 3m, Unit = "dl", Name = "mjölk", RawText = "3 dl mjölk" },
                    new Ingredient { OrderIndex = 2, Quantity = 2m, Unit = "st", Name = "ägg", RawText = "2 st ägg" }
                ]
            },
            []);

        // Scale 4 servings -> 8 servings (2x)
        var added = await _shoppingService.AddRecipeIngredientsAsync(recipe.Id, 8);
        added.Should().Be(2);

        var items = await _shoppingService.GetItemsAsync();
        items.Should().HaveCount(2);
        items.Single(x => x.Name == "mjölk").Quantity.Should().Be(6m);
        items.Single(x => x.Name == "ägg").Quantity.Should().Be(4m);
    }

    [Fact]
    public async Task ToggleAndClearCheckedItemsAsync_ShouldRemoveOnlyCheckedItems()
    {
        var item1 = await _shoppingService.AddManualItemAsync("1 l fil");
        var item2 = await _shoppingService.AddManualItemAsync("2 st citroner");

        var isChecked = await _shoppingService.ToggleCheckedAsync(item1.Id);
        isChecked.Should().BeTrue();

        var cleared = await _shoppingService.ClearCheckedItemsAsync();
        cleared.Should().Be(1);

        var remaining = await _shoppingService.GetItemsAsync();
        remaining.Should().ContainSingle(x => x.Id == item2.Id);
    }

    [Fact]
    public async Task SetCheckedAsync_ShouldBeIdempotent_WhenCalledRepeatedlyOrConcurrently()
    {
        var item = await _shoppingService.AddManualItemAsync("1,5 l mjölk");

        // Both phones check off the same item at the same time
        var results = await Task.WhenAll(
            _shoppingService.SetCheckedAsync(item.Id, true),
            _shoppingService.SetCheckedAsync(item.Id, true));

        results.Should().AllBeEquivalentTo(true);

        var loaded = (await _shoppingService.GetItemsAsync()).Single();
        loaded.IsChecked.Should().BeTrue("dubbla SetCheckedAsync(true) får aldrig avmarkera varan av misstag");

        // Both phones restore the item
        var restoreResults = await Task.WhenAll(
            _shoppingService.SetCheckedAsync(item.Id, false),
            _shoppingService.SetCheckedAsync(item.Id, false));

        restoreResults.Should().AllBeEquivalentTo(true);
        (await _shoppingService.GetItemsAsync()).Single().IsChecked.Should().BeFalse();
    }

    [Fact]
    public async Task SetCheckedAsync_And_UpdateItemAsync_WhenItemAlreadyDeleted_ShouldReturnFalseAndNotRecreate()
    {
        var item = await _shoppingService.AddManualItemAsync("500 g smör");

        var deleted = await _shoppingService.DeleteItemAsync(item.Id);
        deleted.Should().BeTrue();

        // Deleting again is idempotent
        (await _shoppingService.DeleteItemAsync(item.Id)).Should().BeFalse();

        // Checking or updating a deleted item returns false without throwing or recreating
        var checkResult = await _shoppingService.SetCheckedAsync(item.Id, true);
        checkResult.Should().BeFalse();

        var updateResult = await _shoppingService.UpdateItemAsync(item.Id, 2m, "förp", "smör");
        updateResult.Should().BeFalse();

        (await _shoppingService.GetItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateItemAsync_And_SetCheckedAsync_ShouldPreserveBothChanges()
    {
        var item = await _shoppingService.AddManualItemAsync("1 st bröd");

        await Task.WhenAll(
            _shoppingService.UpdateItemAsync(item.Id, 2m, "förp", "surdegsbröd"),
            _shoppingService.SetCheckedAsync(item.Id, true));

        var updated = (await _shoppingService.GetItemsAsync()).Single();
        updated.Name.Should().Be("surdegsbröd");
        updated.Quantity.Should().Be(2m);
        updated.Unit.Should().Be("förp");
        updated.RawText.Should().Be("2 förp surdegsbröd");
        updated.IsChecked.Should().BeTrue();
    }

    [Fact]
    public async Task AddManualItemAsync_WhenCalledConcurrently_ShouldSerializeAndMergeSafely()
    {
        var notifier = new ShoppingListNotifier();
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();

        var serviceA = new ShoppingListService(_dbFactory, aggregator, parser, notifier);
        var serviceB = new ShoppingListService(_dbFactory, aggregator, parser, notifier);

        await Task.WhenAll(
            serviceA.AddManualItemAsync("2 dl mjölk"),
            serviceB.AddManualItemAsync("3 dl mjölk"),
            serviceA.AddManualItemAsync("1 st gurka"),
            serviceB.AddManualItemAsync("500 g tomater"));

        var items = await _shoppingService.GetItemsAsync();
        items.Should().HaveCount(3);
        items.Single(x => x.Name == "mjölk").Quantity.Should().Be(5m);
        items.Should().ContainSingle(x => x.Name == "gurka");
        items.Should().ContainSingle(x => x.Name == "tomater");
    }

    [Fact]
    public async Task GetItemsAsync_WhenItemCheckedAndRestored_ShouldPreserveDeterministicSortOrder()
    {
        var first = await _shoppingService.AddManualItemAsync("Mjölk");
        var second = await _shoppingService.AddManualItemAsync("Bröd");
        var third = await _shoppingService.AddManualItemAsync("Ägg");

        // Check off "Bröd" -> moves to the end (checked section)
        await _shoppingService.SetCheckedAsync(second.Id, true);
        var afterCheck = await _shoppingService.GetItemsAsync();
        afterCheck.Select(x => x.Name).Should().Equal("Mjölk", "Ägg", "Bröd");

        // Restore "Bröd" -> returns to its original position between Mjölk and Ägg
        await _shoppingService.SetCheckedAsync(second.Id, false);
        var afterRestore = await _shoppingService.GetItemsAsync();
        afterRestore.Select(x => x.Name).Should().Equal("Mjölk", "Bröd", "Ägg");
    }

    [Fact]
    public async Task ShoppingListNotifier_ShouldIncrementVersionAndNotifyOnEveryPersistedMutation()
    {
        var notifier = new ShoppingListNotifier();
        var service = new ShoppingListService(_dbFactory, new ShoppingListAggregator(new ServingCalculator()), new SwedishIngredientParser(), notifier);

        var receivedVersions = new List<long>();
        service.OnChanged += v => receivedVersions.Add(v);

        var item = await service.AddManualItemAsync("Kaffe");
        await service.SetCheckedAsync(item.Id, true);
        // Redundant SetCheckedAsync with same value does not trigger an unnecessary DB write or event
        await service.SetCheckedAsync(item.Id, true);
        await service.UpdateItemAsync(item.Id, 2m, "paket", "Bryggkaffe");
        await service.ClearCheckedItemsAsync();

        receivedVersions.Should().Equal(1L, 2L, 3L, 4L);
        service.CurrentVersion.Should().Be(4L);
    }

    [Fact]
    public async Task MoveItemAsync_MoveFromPosition4ToPosition2_ShouldInsertAndShiftIntermediateItems()
    {
        var milk = await _shoppingService.AddManualItemAsync("Mjölk");
        var bread = await _shoppingService.AddManualItemAsync("Bröd");
        var eggs = await _shoppingService.AddManualItemAsync("Ägg");
        var butter = await _shoppingService.AddManualItemAsync("Smör");

        // Move "Smör" from index 3 (position 4) to index 1 (position 2)
        var result = await _shoppingService.MoveItemAsync(butter.Id, 1, _shoppingService.CurrentOrderingVersion);

        result.Status.Should().Be(ShoppingListReorderStatus.Success);
        result.Items.Select(x => x.Name).Should().Equal("Mjölk", "Smör", "Bröd", "Ägg");
        result.Items.Select(x => x.SortOrder).Should().Equal(1, 2, 3, 4);

        // Move "Mjölk" from index 0 (position 1) down to index 2 (position 3)
        var moveDown = await _shoppingService.MoveItemAsync(milk.Id, 2, result.CurrentOrderingVersion);
        moveDown.Status.Should().Be(ShoppingListReorderStatus.Success);
        moveDown.Items.Select(x => x.Name).Should().Equal("Smör", "Bröd", "Mjölk", "Ägg");

        // Move "Ägg" to first position (index 0) and then to last position (index 3)
        var moveFirst = await _shoppingService.MoveItemAsync(eggs.Id, 0, moveDown.CurrentOrderingVersion);
        moveFirst.Status.Should().Be(ShoppingListReorderStatus.Success);
        moveFirst.Items.Select(x => x.Name).Should().Equal("Ägg", "Smör", "Bröd", "Mjölk");

        var moveLast = await _shoppingService.MoveItemAsync(eggs.Id, 3, moveFirst.CurrentOrderingVersion);
        moveLast.Status.Should().Be(ShoppingListReorderStatus.Success);
        moveLast.Items.Select(x => x.Name).Should().Equal("Smör", "Bröd", "Mjölk", "Ägg");

        // Moving to the same position returns NoChange and does not increment OrderingVersion
        var versionBeforeNoOp = _shoppingService.CurrentOrderingVersion;
        var noOp = await _shoppingService.MoveItemAsync(butter.Id, 0, versionBeforeNoOp);
        noOp.Status.Should().Be(ShoppingListReorderStatus.NoChange);
        _shoppingService.CurrentOrderingVersion.Should().Be(versionBeforeNoOp);
    }

    [Fact]
    public async Task ReorderAndMove_Validation_ShouldRejectDuplicateInvalidDeletedOrCheckedItems()
    {
        var item1 = await _shoppingService.AddManualItemAsync("Mjölk");
        var item2 = await _shoppingService.AddManualItemAsync("Bröd");
        var item3 = await _shoppingService.AddManualItemAsync("Ägg");

        var initialOrdVersion = _shoppingService.CurrentOrderingVersion;

        // 1. Duplicate IDs rejected
        var dupResult = await _shoppingService.ReorderItemsAsync([item1.Id, item1.Id, item2.Id], initialOrdVersion);
        dupResult.Status.Should().Be(ShoppingListReorderStatus.InvalidOperation);
        _shoppingService.CurrentOrderingVersion.Should().Be(initialOrdVersion);

        // 2. Non-existent bogus ID rejected
        var bogusResult = await _shoppingService.ReorderItemsAsync([item1.Id, 99999, item2.Id], initialOrdVersion);
        bogusResult.Status.Should().Be(ShoppingListReorderStatus.InvalidOperation);

        // 3. Moving a checked item directly via MoveItemAsync is rejected
        await _shoppingService.SetCheckedAsync(item3.Id, true);
        var moveChecked = await _shoppingService.MoveItemAsync(item3.Id, 0);
        moveChecked.Status.Should().Be(ShoppingListReorderStatus.InvalidOperation);

        // 4. Moving a deleted item directly via MoveItemAsync is rejected
        await _shoppingService.DeleteItemAsync(item2.Id);
        var moveDeleted = await _shoppingService.MoveItemAsync(item2.Id, 0);
        moveDeleted.Status.Should().Be(ShoppingListReorderStatus.InvalidOperation);
    }

    [Fact]
    public async Task ReorderItemsAsync_VersionControl_ShouldRejectStaleVersionAndAllowRetryAfterReload()
    {
        var notifier = new ShoppingListNotifier();
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();

        var phoneAService = new ShoppingListService(_dbFactory, aggregator, parser, notifier);
        var phoneBService = new ShoppingListService(_dbFactory, aggregator, parser, notifier);

        var a = await phoneAService.AddManualItemAsync("Mjölk");
        var b = await phoneAService.AddManualItemAsync("Bröd");
        var c = await phoneAService.AddManualItemAsync("Ägg");

        // Both phones read the same initial ordering version
        var sharedInitialVersion = phoneAService.CurrentOrderingVersion;
        phoneBService.CurrentOrderingVersion.Should().Be(sharedInitialVersion);

        var notifications = 0;
        notifier.OnChanged += _ => notifications++;

        // Phone A reorders: [Ägg, Mjölk, Bröd] -> succeeds and increments OrderingVersion
        var resA = await phoneAService.ReorderItemsAsync([c.Id, a.Id, b.Id], sharedInitialVersion);
        resA.Status.Should().Be(ShoppingListReorderStatus.Success);
        resA.CurrentOrderingVersion.Should().Be(sharedInitialVersion + 1);
        notifications.Should().Be(1);

        // Phone B attempts to reorder using the stale version -> rejected with VersionConflict, no extra notification
        var resBStale = await phoneBService.ReorderItemsAsync([b.Id, a.Id, c.Id], sharedInitialVersion);
        resBStale.Status.Should().Be(ShoppingListReorderStatus.VersionConflict);
        resBStale.IsConflict.Should().BeTrue();
        resBStale.Items.Select(x => x.Name).Should().Equal("Ägg", "Mjölk", "Bröd");
        notifications.Should().Be(1, "avvisad sortering får inte skicka falsk ändringsnotifiering");

        // Phone B reloads the latest ordering version and retries -> succeeds
        var resBRetry = await phoneBService.ReorderItemsAsync([b.Id, c.Id, a.Id], resBStale.CurrentOrderingVersion);
        resBRetry.Status.Should().Be(ShoppingListReorderStatus.Success);
        resBRetry.Items.Select(x => x.Name).Should().Equal("Bröd", "Ägg", "Mjölk");
        notifications.Should().Be(2);
    }

    [Fact]
    public async Task ReorderItemsAsync_WithConcurrentCheckOffDeletionAndAddition_ShouldPreserveAllChangesAndNewItemsGoLast()
    {
        var milk = await _shoppingService.AddManualItemAsync("2 dl mjölk");
        var bread = await _shoppingService.AddManualItemAsync("1 st bröd");
        var eggs = await _shoppingService.AddManualItemAsync("6 st ägg");
        var butter = await _shoppingService.AddManualItemAsync("500 g smör");

        var startOrderVersion = _shoppingService.CurrentOrderingVersion;

        // While Phone A is preparing to reorder [butter, milk, bread, eggs]:
        // - Phone B checks off "bröd"
        // - Phone B deletes "ägg"
        // - Phone B adds a new item "1 st gurka"
        // - Phone B adds more "3 dl mjölk" (which merges into existing "mjölk")
        await _shoppingService.SetCheckedAsync(bread.Id, true);
        await _shoppingService.DeleteItemAsync(eggs.Id);
        var cucumber = await _shoppingService.AddManualItemAsync("1 st gurka");
        await _shoppingService.AddManualItemAsync("3 dl mjölk");

        // Phone A submits its reorder snapshot [butter, milk, bread, eggs] with startOrderVersion
        var reorderResult = await _shoppingService.ReorderItemsAsync(
            [butter.Id, milk.Id, bread.Id, eggs.Id],
            startOrderVersion);

        reorderResult.Status.Should().Be(ShoppingListReorderStatus.Success);

        var finalItems = await _shoppingService.GetItemsAsync();
        var uncheckedItems = finalItems.Where(x => !x.IsChecked).ToList();
        var checkedItems = finalItems.Where(x => x.IsChecked).ToList();

        // 1. Deleted "ägg" did NOT resurrect
        finalItems.Should().NotContain(x => x.Id == eggs.Id);

        // 2. Checked-off "bröd" stayed checked in Klart
        checkedItems.Select(x => x.Id).Should().Equal(bread.Id);

        // 3. Unchecked items are ordered: Smör first, merged Mjölk (5 dl) second, and concurrently added Gurka last!
        uncheckedItems.Select(x => x.Id).Should().Equal(butter.Id, milk.Id, cucumber.Id);
        uncheckedItems.Single(x => x.Id == milk.Id).Quantity.Should().Be(5m);

        // 4. Restoring "bröd" from Klart places it back according to its preserved SortOrder without resetting the manual order
        await _shoppingService.SetCheckedAsync(bread.Id, false);
        var afterRestore = (await _shoppingService.GetItemsAsync()).Where(x => !x.IsChecked).ToList();
        afterRestore.First().Id.Should().Be(butter.Id);
        afterRestore.Last().Id.Should().Be(cucumber.Id);
    }

    [Fact]
    public async Task LegacyItemsWithZeroSortOrder_ShouldBeNormalizedDeterministicallyWithoutDataLoss()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            var baseTime = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
            db.ShoppingListItems.AddRange(
                new ShoppingListItem { Name = "Kaffe", RawText = "1 paket Kaffe", Quantity = 1, Unit = "paket", SortOrder = 0, IsChecked = false, CreatedAt = baseTime },
                new ShoppingListItem { Name = "Te", RawText = "1 paket Te", Quantity = 1, Unit = "paket", SortOrder = 0, IsChecked = false, CreatedAt = baseTime.AddMinutes(1) },
                new ShoppingListItem { Name = "Socker", RawText = "1 kg Socker", Quantity = 1, Unit = "kg", SortOrder = 0, IsChecked = true, CreatedAt = baseTime.AddMinutes(2) });
            await db.SaveChangesAsync();
        }

        // Adding a new item normalizes legacy SortOrder = 0 items deterministically and places the new item last in Att handla
        var honey = await _shoppingService.AddManualItemAsync("1 burk Honung");

        var items = await _shoppingService.GetItemsAsync();
        var uncheckedItems = items.Where(x => !x.IsChecked).ToList();
        var checkedItems = items.Where(x => x.IsChecked).ToList();

        uncheckedItems.Select(x => x.Name).Should().Equal("Kaffe", "Te", "Honung");
        uncheckedItems.Should().OnlyContain(x => x.SortOrder > 0);
        uncheckedItems.Select(x => x.SortOrder).Should().BeInAscendingOrder();
        checkedItems.Single().Name.Should().Be("Socker");
    }

    [Fact]
    public async Task RestoreDeletedItemAsync_ShouldPreserveAllItemFieldsCheckedStateAndSortOrder()
    {
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Våfflor",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 3m, Unit = "dl", Name = "vetemjöl", RawText = "3 dl vetemjöl" },
                    new Ingredient { OrderIndex = 2, Quantity = 4m, Unit = "dl", Name = "mjölk", RawText = "4 dl mjölk" }
                ]
            },
            []);
        await _shoppingService.AddRecipeIngredientsAsync(recipe.Id, targetServings: 4);

        var initialItems = await _shoppingService.GetItemsAsync();
        var target = initialItems.Single(x => x.Name == "vetemjöl");

        var createdTime = new DateTimeOffset(2026, 11, 5, 14, 30, 0, TimeSpan.Zero);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var tracked = db.ShoppingListItems.Single(x => x.Id == target.Id);
            tracked.Department = "Skafferi";
            tracked.CreatedAt = createdTime;
            tracked.IsChecked = true;
            await db.SaveChangesAsync();
        }

        var beforeDelete = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == target.Id);
        var snapshot = new ShoppingListItem
        {
            Id = beforeDelete.Id,
            RecipeId = beforeDelete.RecipeId,
            RawText = beforeDelete.RawText,
            Quantity = beforeDelete.Quantity,
            Unit = beforeDelete.Unit,
            Name = beforeDelete.Name,
            IsChecked = beforeDelete.IsChecked,
            SortOrder = beforeDelete.SortOrder,
            Department = beforeDelete.Department,
            CreatedAt = beforeDelete.CreatedAt
        };

        var deleted = await _shoppingService.DeleteItemAsync(beforeDelete.Id);
        deleted.Should().BeTrue();
        (await _shoppingService.GetItemsAsync()).Should().NotContain(x => x.Id == beforeDelete.Id);

        int notifications = 0;
        _shoppingService.OnChanged += _ => notifications++;

        var restored = await _shoppingService.RestoreDeletedItemAsync(snapshot);
        restored.Should().BeTrue();
        notifications.Should().Be(1);

        var afterRestore = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == snapshot.Id);
        afterRestore.Id.Should().Be(snapshot.Id);
        afterRestore.Name.Should().Be("vetemjöl");
        afterRestore.RawText.Should().Be("3 dl vetemjöl");
        afterRestore.Quantity.Should().Be(3m);
        afterRestore.Unit.Should().Be("dl");
        afterRestore.RecipeId.Should().Be(recipe.Id);
        afterRestore.Department.Should().Be("Skafferi");
        afterRestore.IsChecked.Should().BeTrue();
        afterRestore.SortOrder.Should().Be(snapshot.SortOrder);
        afterRestore.CreatedAt.Should().Be(createdTime);
    }

    [Fact]
    public async Task RestoreDeletedItemAsync_RepeatedCallsOrExistingIdConflict_ShouldNotDuplicateOrOverwriteOtherItem()
    {
        var item = await _shoppingService.AddManualItemAsync("100 g färskost");
        await _shoppingService.SetCheckedAsync(item.Id, true);
        var checkedSnapshot = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == item.Id);

        await _shoppingService.DeleteItemAsync(item.Id);

        int notifications = 0;
        _shoppingService.OnChanged += _ => notifications++;

        // 1. First restore succeeds
        var firstRestore = await _shoppingService.RestoreDeletedItemAsync(checkedSnapshot);
        firstRestore.Should().BeTrue();
        notifications.Should().Be(1);

        // 2. Second restore with same snapshot is safely rejected without duplicating or notifying
        var secondRestore = await _shoppingService.RestoreDeletedItemAsync(checkedSnapshot);
        secondRestore.Should().BeFalse();
        notifications.Should().Be(1);

        var itemsAfterDoubleRestore = await _shoppingService.GetItemsAsync();
        itemsAfterDoubleRestore.Should().ContainSingle(x => x.Id == item.Id);

        // 3. If another item already occupies the ID, its data must never be overwritten
        await _shoppingService.DeleteItemAsync(item.Id);
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.ShoppingListItems.Add(new ShoppingListItem
            {
                Id = item.Id,
                Name = "helt annan vara",
                RawText = "5 st helt annan vara",
                Quantity = 5m,
                Unit = "st",
                IsChecked = false,
                SortOrder = 99
            });
            await db.SaveChangesAsync();
        }

        var conflictRestore = await _shoppingService.RestoreDeletedItemAsync(checkedSnapshot);
        conflictRestore.Should().BeFalse();

        var preserved = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == item.Id);
        preserved.Name.Should().Be("helt annan vara");
        preserved.RawText.Should().Be("5 st helt annan vara");
        preserved.Quantity.Should().Be(5m);
        preserved.IsChecked.Should().BeFalse();
    }

    [Fact]
    public async Task RestoreDeletedItemAsync_WhenSameNameAddedConcurrentlyBeforeUndo_ShouldKeepBothItemsAndPreserveOrder()
    {
        var first = await _shoppingService.AddManualItemAsync("2 dl gräddfil");
        var creamCheese = await _shoppingService.AddManualItemAsync("100 g färskost");
        var dill = await _shoppingService.AddManualItemAsync("1 knippe dill");

        await _shoppingService.SetCheckedAsync(first.Id, true);
        await _shoppingService.SetCheckedAsync(creamCheese.Id, true);
        await _shoppingService.SetCheckedAsync(dill.Id, true);

        var snapshot = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == creamCheese.Id);
        await _shoppingService.DeleteItemAsync(creamCheese.Id);

        // Before Undo is clicked on Phone A, Phone B adds a new item with the same name ("250 g färskost")
        // and another item ("1 paket bacon") and reorders Att handla.
        var newCreamCheese = await _shoppingService.AddManualItemAsync("250 g färskost");
        var bacon = await _shoppingService.AddManualItemAsync("1 paket bacon");
        await _shoppingService.MoveItemAsync(bacon.Id, 0);
        var orderVersionAfterPhoneB = _shoppingService.CurrentOrderingVersion;

        // Phone A now restores the deleted "100 g färskost"
        var restored = await _shoppingService.RestoreDeletedItemAsync(snapshot);
        restored.Should().BeTrue();

        // OrderingVersion for Att handla is untouched
        _shoppingService.CurrentOrderingVersion.Should().Be(orderVersionAfterPhoneB);

        var allItems = await _shoppingService.GetItemsAsync();
        var uncheckedItems = allItems.Where(x => !x.IsChecked).ToList();
        var checkedItems = allItems.Where(x => x.IsChecked).ToList();

        // Both "färskost" items exist separately without overwriting or merging
        uncheckedItems.Select(x => x.Id).Should().Equal(bacon.Id, newCreamCheese.Id);
        uncheckedItems.Single(x => x.Id == newCreamCheese.Id).Quantity.Should().Be(250m);

        // Restored item is back in its original middle position in Klart between gräddfil and dill
        checkedItems.Select(x => x.Id).Should().Equal(first.Id, creamCheese.Id, dill.Id);
        checkedItems.Single(x => x.Id == creamCheese.Id).Quantity.Should().Be(100m);
        checkedItems.Single(x => x.Id == creamCheese.Id).IsChecked.Should().BeTrue();
    }

    [Fact]
    public async Task AddRecipeIngredients_AddManual_Update_And_Restore_I3_20_To_23_ShouldPreserveCommentAndQuantityMax()
    {
        // I3 #20: AddRecipeIngredientsAsync saves Comment and QuantityMax and performs conservative merging
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Blåbärspannacotta",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "st", Name = "gelatinblad", RawText = "2 st gelatinblad" },
                    new Ingredient { OrderIndex = 2, Quantity = 2m, Unit = "dl", Name = "blåbär", RawText = "2 dl blåbär" },
                    new Ingredient { OrderIndex = 3, Quantity = 1m, Unit = "msk", Name = "pressad citron", RawText = "1 msk pressad citron" },
                    new Ingredient { OrderIndex = 4, Quantity = 3m, Unit = "dl", Name = "grädde", RawText = "3 dl grädde" },
                    new Ingredient { OrderIndex = 5, Name = "blåbär till garnering", RawText = "blåbär till garnering" },
                    new Ingredient { OrderIndex = 6, Name = "vispad grädde till garnering", RawText = "vispad grädde till garnering" },
                    new Ingredient { OrderIndex = 7, Quantity = 1m, QuantityMax = 2m, Unit = "tsk", Name = "vaniljsocker", RawText = "1–2 tsk vaniljsocker" }
                ]
            },
            []);

        await _shoppingService.AddRecipeIngredientsAsync(recipe.Id, targetServings: 4);

        var items = await _shoppingService.GetItemsAsync();
        items.Should().HaveCount(5);

        var blueberries = items.Single(x => x.Name == "blåbär");
        blueberries.Quantity.Should().Be(2m);
        blueberries.Unit.Should().Be("dl");
        blueberries.Comment.Should().Be("Extra blåbär till garnering, mängd ej angiven");

        var cream = items.Single(x => x.Name == "grädde");
        cream.Quantity.Should().Be(3m);
        cream.Unit.Should().Be("dl");
        cream.Comment.Should().Be("Extra vispad grädde till garnering, mängd ej angiven");

        var vanilla = items.Single(x => x.Name == "vaniljsocker");
        vanilla.Quantity.Should().Be(1m);
        vanilla.QuantityMax.Should().Be(2m);
        vanilla.RawText.Should().Be("1–2 tsk vaniljsocker");

        // Verify original recipe ingredients in DB were NOT mutated
        var persistedRecipe = await _recipeService.GetRecipeByIdAsync(recipe.Id);
        persistedRecipe!.Ingredients.Should().HaveCount(7);
        persistedRecipe.Ingredients.Should().Contain(i => i.Name == "blåbär till garnering");
        persistedRecipe.Ingredients.Should().Contain(i => i.Name == "vispad grädde till garnering");

        // I3 #21: AddManualItemAsync preserves comment and interval from parser
        var manualTomatoes = await _shoppingService.AddManualItemAsync("1 burk krossade tomater (à 400 g)");
        manualTomatoes.Name.Should().Be("krossade tomater");
        manualTomatoes.Quantity.Should().Be(1m);
        manualTomatoes.Unit.Should().Be("burk");
        manualTomatoes.Comment.Should().Be("à 400 g");

        // I3 #22: UpdateItemAsync preserves or updates Comment and QuantityMax
        await _shoppingService.UpdateItemAsync(manualTomatoes.Id, 2m, "burk", "krossade tomater");
        var afterLegacyUpdate = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == manualTomatoes.Id);
        afterLegacyUpdate.Quantity.Should().Be(2m);
        afterLegacyUpdate.Comment.Should().Be("à 400 g", "4-parametig UpdateItemAsync får inte tyst radera befintlig kommentar");

        await _shoppingService.UpdateItemAsync(manualTomatoes.Id, 2m, 3m, "burk", "krossade tomater", "à 400 g, ekologiska");
        var afterFullUpdate = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == manualTomatoes.Id);
        afterFullUpdate.Quantity.Should().Be(2m);
        afterFullUpdate.QuantityMax.Should().Be(3m);
        afterFullUpdate.Comment.Should().Be("à 400 g, ekologiska");
        afterFullUpdate.RawText.Should().Be("2–3 burk krossade tomater");

        // I3 #23: DeleteItemAsync + RestoreDeletedItemAsync restores Comment and QuantityMax
        await _shoppingService.DeleteItemAsync(afterFullUpdate.Id);
        (await _shoppingService.GetItemsAsync()).Should().NotContain(x => x.Id == afterFullUpdate.Id);

        var restored = await _shoppingService.RestoreDeletedItemAsync(afterFullUpdate);
        restored.Should().BeTrue();

        var afterRestore = (await _shoppingService.GetItemsAsync()).Single(x => x.Id == afterFullUpdate.Id);
        afterRestore.Comment.Should().Be("à 400 g, ekologiska");
        afterRestore.Quantity.Should().Be(2m);
        afterRestore.QuantityMax.Should().Be(3m);
        afterRestore.RawText.Should().Be("2–3 burk krossade tomater");
    }

    [Fact]
    public async Task AddManualAndRecipeIngredients_3B1A_WrittenOutKiloAndGram_ShouldMergeTo700g()
    {
        await _shoppingService.AddManualItemAsync("200 g nötfärs");
        await _shoppingService.AddManualItemAsync("0,5 kilo nötfärs");

        var items = await _shoppingService.GetItemsAsync();
        items.Should().ContainSingle();
        items[0].Name.Should().Be("nötfärs");
        items[0].Quantity.Should().Be(700m);
        items[0].Unit.Should().Be("g");
        items[0].RawText.Should().Be("700 g nötfärs");
    }

    [Fact]
    public async Task ReorderItemsAsync_AcrossTwoServiceInstances_WhenOtherInstanceDeletesChecksAndAdds_ShouldSucceedAndPreserveAllChanges()
    {
        var notifier = new ShoppingListNotifier();
        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        var parser = new SwedishIngredientParser();

        var serviceA = new ShoppingListService(_dbFactory, aggregator, parser, notifier);
        var serviceB = new ShoppingListService(_dbFactory, aggregator, parser, notifier);

        var milk = await serviceA.AddManualItemAsync("2 dl mjölk");
        var bread = await serviceA.AddManualItemAsync("1 st bröd");
        var eggs = await serviceA.AddManualItemAsync("6 st ägg");
        var butter = await serviceA.AddManualItemAsync("500 g smör");

        var startOrderVersion = serviceA.CurrentOrderingVersion;

        // Concurrent mutations on a separate scoped ShoppingListService sharing the same ShoppingListNotifier
        await serviceB.SetCheckedAsync(bread.Id, true);
        (await serviceB.DeleteItemAsync(eggs.Id)).Should().BeTrue();
        notifier.WasItemDeleted(eggs.Id).Should().BeTrue();

        var cucumber = await serviceB.AddManualItemAsync("1 st gurka");
        await serviceB.AddManualItemAsync("3 dl mjölk");

        var reorderResult = await serviceA.ReorderItemsAsync(
            [butter.Id, milk.Id, bread.Id, eggs.Id],
            startOrderVersion);

        reorderResult.Status.Should().Be(ShoppingListReorderStatus.Success);

        var finalItems = await serviceA.GetItemsAsync();
        finalItems.Should().NotContain(x => x.Id == eggs.Id);
        finalItems.Where(x => x.IsChecked).Select(x => x.Id).Should().Equal(bread.Id);

        var uncheckedItems = finalItems.Where(x => !x.IsChecked).ToList();
        uncheckedItems.Select(x => x.Id).Should().Equal(butter.Id, milk.Id, cucumber.Id);
        uncheckedItems.Single(x => x.Id == milk.Id).Quantity.Should().Be(5m);
    }

    [Fact]
    public async Task ReorderItemsAsync_AcrossTwoServiceInstances_WhenOtherInstanceClearsCheckedItems_ShouldSucceedForRemainingUncheckedItems()
    {
        var notifier = new ShoppingListNotifier();
        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        var parser = new SwedishIngredientParser();

        var serviceA = new ShoppingListService(_dbFactory, aggregator, parser, notifier);
        var serviceB = new ShoppingListService(_dbFactory, aggregator, parser, notifier);

        var milk = await serviceA.AddManualItemAsync("Mjölk");
        var bread = await serviceA.AddManualItemAsync("Bröd");
        var butter = await serviceA.AddManualItemAsync("Smör");

        var startOrderVersion = serviceA.CurrentOrderingVersion;

        await serviceB.SetCheckedAsync(bread.Id, true);
        var clearedCount = await serviceB.ClearCheckedItemsAsync();
        clearedCount.Should().Be(1);
        notifier.WasItemDeleted(bread.Id).Should().BeTrue();

        var reorderResult = await serviceA.ReorderItemsAsync(
            [butter.Id, bread.Id, milk.Id],
            startOrderVersion);

        reorderResult.Status.Should().Be(ShoppingListReorderStatus.Success);
        reorderResult.Items.Where(x => !x.IsChecked).Select(x => x.Id).Should().Equal(butter.Id, milk.Id);
        reorderResult.Items.Should().NotContain(x => x.Id == bread.Id);
    }

    [Fact]
    public async Task ReorderItemsAsync_AcrossTwoServiceInstances_WhenBogusIdIncludedOrAllCleared_ShouldRejectInvalidIds()
    {
        var notifier = new ShoppingListNotifier();
        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        var parser = new SwedishIngredientParser();

        var serviceA = new ShoppingListService(_dbFactory, aggregator, parser, notifier);
        var serviceB = new ShoppingListService(_dbFactory, aggregator, parser, notifier);

        var milk = await serviceA.AddManualItemAsync("Mjölk");
        var bread = await serviceA.AddManualItemAsync("Bröd");
        var startOrderVersion = serviceA.CurrentOrderingVersion;

        await serviceB.DeleteItemAsync(bread.Id);

        var notifications = 0;
        notifier.OnChanged += _ => notifications++;

        var bogusResult = await serviceA.ReorderItemsAsync([milk.Id, 99999, bread.Id], startOrderVersion);
        bogusResult.Status.Should().Be(ShoppingListReorderStatus.InvalidOperation);
        serviceA.CurrentOrderingVersion.Should().Be(startOrderVersion);
        notifications.Should().Be(0);

        // Clearing all remaining items leaves zero unchecked items -> InvalidOperation, no resurrect
        await serviceB.ClearAllItemsAsync();
        notifier.WasItemDeleted(milk.Id).Should().BeTrue();

        var allClearedResult = await serviceA.ReorderItemsAsync([milk.Id, bread.Id], startOrderVersion);
        allClearedResult.Status.Should().Be(ShoppingListReorderStatus.InvalidOperation);
        allClearedResult.Items.Should().BeEmpty();
        serviceA.CurrentOrderingVersion.Should().Be(startOrderVersion);
    }

    [Fact]
    public async Task RestoreDeletedItemAsync_ShouldRemoveIdFromSharedNotifierDeletedHistory()
    {
        var notifier = new ShoppingListNotifier();
        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        var parser = new SwedishIngredientParser();

        var serviceA = new ShoppingListService(_dbFactory, aggregator, parser, notifier);
        var serviceB = new ShoppingListService(_dbFactory, aggregator, parser, notifier);

        var milk = await serviceA.AddManualItemAsync("Mjölk");
        var bread = await serviceA.AddManualItemAsync("Bröd");

        await serviceB.DeleteItemAsync(bread.Id);
        notifier.WasItemDeleted(bread.Id).Should().BeTrue();

        var restored = await serviceB.RestoreDeletedItemAsync(bread);
        restored.Should().BeTrue();
        notifier.WasItemDeleted(bread.Id).Should().BeFalse();

        // Both items are back in DB and can be reordered normally
        var reorderResult = await serviceA.ReorderItemsAsync([bread.Id, milk.Id], serviceA.CurrentOrderingVersion);
        reorderResult.Status.Should().Be(ShoppingListReorderStatus.Success);
        reorderResult.Items.Select(x => x.Id).Should().Equal(bread.Id, milk.Id);
    }

    [Fact]
    public void ShoppingListNotifier_DeletedItemTracking_ShouldEnforceBoundedCapacityAndSupportRestoreRemoval()
    {
        var notifier = new ShoppingListNotifier();
        var capacity = ShoppingListNotifier.MaxTrackedDeletedIds;

        // Fill to capacity: 1..capacity
        for (var id = 1; id <= capacity; id++)
        {
            notifier.RecordDeletedItem(id);
        }

        // Remove ID 1 (simulating Undo) and re-delete it; it should move to the tail without leaving a stale FIFO entry
        notifier.RemoveDeletedItem(1).Should().BeTrue();
        notifier.WasItemDeleted(1).Should().BeFalse();
        notifier.RecordDeletedItem(1);
        notifier.WasItemDeleted(1).Should().BeTrue();

        // Repeatedly removing and re-recording the same ID must not grow the queue or cause premature eviction
        for (var i = 0; i < 20; i++)
        {
            notifier.RemoveDeletedItem(2).Should().BeTrue();
            notifier.RecordDeletedItem(2);
        }

        // Now oldest remaining ID is 3; adding one more ID should evict 3, while 1, 2, and capacity + 1 remain tracked
        notifier.RecordDeletedItem(capacity + 1);

        notifier.WasItemDeleted(3).Should().BeFalse();
        notifier.WasItemDeleted(1).Should().BeTrue();
        notifier.WasItemDeleted(2).Should().BeTrue();
        notifier.WasItemDeleted(4).Should().BeTrue();
        notifier.WasItemDeleted(capacity + 1).Should().BeTrue();

        // ClearDeletedItems empties all tracked IDs
        notifier.ClearDeletedItems();
        notifier.WasItemDeleted(1).Should().BeFalse();
        notifier.WasItemDeleted(capacity + 1).Should().BeFalse();
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
    }
}

