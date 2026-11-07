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

    public void Dispose()
    {
        _dbFactory.Dispose();
    }
}

