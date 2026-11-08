using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Backup;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Infrastructure.Storage;

namespace Receptbanken.Tests.Infrastructure;

public class WeeklyMenuShoppingServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly ServingCalculator _calculator;
    private readonly ShoppingListAggregator _aggregator;
    private readonly SwedishIngredientParser _parser;
    private readonly ShoppingListNotifier _notifier;
    private readonly ShoppingListService _shoppingService;
    private readonly RecipeService _recipeService;
    private readonly CategoryService _categoryService;
    private readonly WeeklyMenuGenerator _generator;
    private readonly WeeklyMenuService _weeklyMenuService;
    private readonly MockImageStorageService _imageStorage;

    public WeeklyMenuShoppingServiceTests()
    {
        _dbFactory = new TestDbContextFactory();
        using (var db = _dbFactory.CreateDbContext())
        {
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        _calculator = new ServingCalculator();
        _aggregator = new ShoppingListAggregator(_calculator);
        _parser = new SwedishIngredientParser();
        _notifier = new ShoppingListNotifier();
        _shoppingService = new ShoppingListService(_dbFactory, _aggregator, _parser, _notifier);
        _imageStorage = new MockImageStorageService();
        _recipeService = new RecipeService(_dbFactory, _imageStorage, NullLogger<RecipeService>.Instance);
        _categoryService = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        _generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        _weeklyMenuService = new WeeklyMenuService(_dbFactory, _generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
    }

    private async Task<int> GetVardagCategoryIdAsync()
    {
        var cats = await _categoryService.GetCategoriesAsync();
        return cats.First(c => c.Name == "Vardagsmat").Id;
    }

    // =========================================================================
    // K1. Veckomenyns sammanställning och portioner (Scenarion 1–7)
    // =========================================================================

    [Fact]
    public async Task K1_01_EmptyDaysAreIgnored_And_K1_02_SingleRecipeProducesCorrectIngredients()
    {
        var weekDate = new DateOnly(2026, 11, 2); // Vecka 45
        var catId = await GetVardagCategoryIdAsync();

        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Blåbärspannkaka",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 3m, Unit = "dl", Name = "mjölk", RawText = "3 dl mjölk" },
                    new Ingredient { OrderIndex = 2, Quantity = 2m, Unit = "dl", Name = "blåbär", RawText = "2 dl blåbär" }
                ]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        // Endast måndag har recept; tisdag–söndag är tomma
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 4;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        preview.HasSavedMenu.Should().BeTrue();
        preview.PlannedMeals.Should().ContainSingle();
        preview.PlannedMeals[0].DayName.Should().Be("Måndag");
        preview.PlannedMeals[0].RecipeTitle.Should().Be("Blåbärspannkaka");
        preview.PlannedMeals[0].PlannedServings.Should().Be(4);
        preview.Items.Should().HaveCount(2);
        preview.Items.Should().Contain(i => i.Item.Name == "mjölk" && i.Item.Quantity == 3m && i.Item.Unit == "dl");
        preview.Items.Should().Contain(i => i.Item.Name == "blåbär" && i.Item.Quantity == 2m && i.Item.Unit == "dl");
    }

    [Fact]
    public async Task K1_03_SameRecipeOnTwoDays_CountedTwiceWithRespectiveServings_And_K3_16_TreatedAsTwoSeparateNeeds()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();

        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Chili con carne",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 400m, Unit = "g", Name = "nötfärs", RawText = "400 g nötfärs" }
                ]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        // Måndag: 4 portioner (400 g), Torsdag: 6 portioner (600 g) -> Totalt 1000 g
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 4;
        menu.Days[3].RecipeId = recipe.Id;
        menu.Days[3].Servings = 6;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        preview.PlannedMeals.Should().HaveCount(2);
        preview.Items.Should().ContainSingle();
        var item = preview.Items[0];
        item.Item.Name.Should().Be("nötfärs");
        item.Item.Quantity.Should().Be(1000m);
        item.Item.Unit.Should().Be("g");
        item.SourceDaysSummary.Should().Contain("Måndag").And.Contain("Torsdag");
        item.SourceContributions.Should().HaveCount(2);
    }

    [Fact]
    public async Task K1_04_DifferentRecipesWithSameIngredient_AreSummedIncludingUnitConversion_And_K1_05_PortionScalingFrom4To6()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();

        var recipe1 = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Kycklinggryta",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "grädde", RawText = "2 dl grädde" },
                    new Ingredient { OrderIndex = 2, Quantity = 200m, Unit = "g", Name = "potatis", RawText = "200 g potatis" }
                ]
            },
            [catId]);

        var recipe2 = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Fiskgratäng",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "grädde", RawText = "2 dl grädde" },
                    new Ingredient { OrderIndex = 2, Quantity = 0.5m, Unit = "kilo", Name = "potatis", RawText = "0,5 kilo potatis" }
                ]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        // Måndag: Kycklinggryta skalas från 4 -> 6 portioner (2 dl -> 3 dl grädde, 200 g -> 300 g potatis)
        menu.Days[0].RecipeId = recipe1.Id;
        menu.Days[0].Servings = 6;
        // Tisdag: Fiskgratäng 4 portioner (2 dl grädde, 0,5 kilo = 500 g potatis)
        menu.Days[1].RecipeId = recipe2.Id;
        menu.Days[1].Servings = 4;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        var cream = preview.Items.Single(i => i.Item.Name == "grädde");
        cream.Item.Quantity.Should().Be(5m); // 3 dl + 2 dl
        cream.Item.Unit.Should().Be("dl");

        var potato = preview.Items.Single(i => i.Item.Name == "potatis");
        potato.Item.Quantity.Should().Be(800m); // 300 g + 500 g
        potato.Item.Unit.Should().Be("g");
    }

    [Fact]
    public async Task K1_06_ServingsFollowsRecipeWhenMovedOrSwappedBetweenDays()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();

        var r1 = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Rätt A", Servings = 4 }, [catId]);
        var r2 = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Rätt B", Servings = 2 }, [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Recipe = r1;
        menu.Days[0].Servings = 6; // A har justerats till 6 portioner

        menu.Days[1].RecipeId = r2.Id;
        menu.Days[1].Recipe = r2;
        menu.Days[1].Servings = 2; // B har 2 portioner

        _generator.MoveOrSwapRecipe(menu, 0, 1);

        menu.Days[0].RecipeId.Should().Be(r2.Id);
        menu.Days[0].Servings.Should().Be(2);
        menu.Days[1].RecipeId.Should().Be(r1.Id);
        menu.Days[1].Servings.Should().Be(6);
    }

    [Fact]
    public async Task K1_07_BothDraftAndConfirmedWeeklyMenusWork()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Soppa",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 1m, Unit = "l", Name = "buljong", RawText = "1 l buljong" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var draftPreview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        draftPreview.Status.Should().Be(WeeklyMenuStatus.Draft);
        draftPreview.Items.Should().ContainSingle(i => i.Item.Name == "buljong");

        await _weeklyMenuService.ConfirmMenuAsync(menu);
        var confirmedPreview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        confirmedPreview.Status.Should().Be(WeeklyMenuStatus.Confirmed);
        confirmedPreview.Items.Should().ContainSingle(i => i.Item.Name == "buljong");
    }

    // =========================================================================
    // K2. Förhandsgranskning och val (Scenarion 9–14)
    // =========================================================================

    [Fact]
    public async Task K2_09_OpeningPreviewDoesNotCreateItems_And_K2_14_CancelledPreviewLeavesListUnchanged()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Pannkakor",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 6m, Unit = "dl", Name = "mjölk", RawText = "6 dl mjölk" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        // Öppna förhandsgranskning utan att bekräfta
        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview.Items.Should().ContainSingle();

        var listItems = await _shoppingService.GetItemsAsync();
        listItems.Should().BeEmpty("inga inköpsvaror får skapas bara av att förhandsgranskningen öppnas");
    }

    [Fact]
    public async Task K2_10_UserCanDeselectIndividualItems_And_K2_11_CommentsAndIntervalsArePreserved()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Marängsviss",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 3m, QuantityMax = 4m, Unit = "dl", Name = "grädde", Comment = "ekologisk", RawText = "3-4 dl grädde (ekologisk)" },
                    new Ingredient { OrderIndex = 2, Quantity = null, Unit = null, Name = "grädde", Preparation = "vispad", GroupHeader = "Till garnering", RawText = "vispad grädde" },
                    new Ingredient { OrderIndex = 3, Quantity = 2m, Unit = "st", Name = "banan", RawText = "2 st banan" }
                ]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview.Items.Should().HaveCount(2);

        var creamPreview = preview.Items.Single(i => i.Item.Name == "grädde");
        creamPreview.Item.Quantity.Should().Be(3m);
        creamPreview.Item.QuantityMax.Should().Be(4m);
        creamPreview.Item.Comment.Should().Contain("ekologisk").And.ContainEquivalentOf("extra").And.Contain("vispad").And.Contain("till garnering");

        // Användaren väljer bort banan och lägger bara till grädde
        var confirmResult = await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            preview.PreviewStamp,
            [creamPreview.PreviewItemKey]);

        confirmResult.Status.Should().Be(WeeklyMenuConfirmStatus.Success);
        confirmResult.AddedItemsCount.Should().Be(1);

        var shoppingItems = await _shoppingService.GetItemsAsync();
        shoppingItems.Should().ContainSingle();
        shoppingItems[0].Name.Should().Be("grädde");
        shoppingItems[0].Quantity.Should().Be(3m);
        shoppingItems[0].QuantityMax.Should().Be(4m);
        shoppingItems[0].Comment.Should().Contain("ekologisk").And.ContainEquivalentOf("extra");
    }

    [Fact]
    public async Task K2_12_ExistingActiveItemsShownAsOverlap_And_K2_13_ExistingCheckedItemsShownAndNotModified()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();

        // Lägg in 1 dl mjölk i Att handla och 2 st ägg i Klart
        await _shoppingService.AddManualItemAsync("1 dl mjölk");
        var eggItem = await _shoppingService.AddManualItemAsync("2 st ägg");
        await _shoppingService.SetCheckedAsync(eggItem!.Id, true);

        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Våfflor",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 3m, Unit = "dl", Name = "mjölk", RawText = "3 dl mjölk" },
                    new Ingredient { OrderIndex = 2, Quantity = 2m, Unit = "st", Name = "ägg", RawText = "2 st ägg" }
                ]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        var milkPreview = preview.Items.Single(i => i.Item.Name == "mjölk");
        milkPreview.ExistingInUncheckedText.Should().Contain("1 dl mjölk");
        milkPreview.ExistingInCheckedText.Should().BeNull();

        var eggPreview = preview.Items.Single(i => i.Item.Name == "ägg");
        eggPreview.ExistingInCheckedText.Should().Contain("2 st ägg");
        eggPreview.ExistingInUncheckedText.Should().BeNull();

        // Bekräfta båda -> mjölk slås ihop till 4 dl i Att handla, medan Klart-raden för ägg lämnas orörd och ny rad skapas i Att handla
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            preview.PreviewStamp,
            preview.Items.Select(i => i.PreviewItemKey).ToList());

        var allItems = await _shoppingService.GetItemsAsync();
        var checkedItems = allItems.Where(i => i.IsChecked).ToList();
        var uncheckedItems = allItems.Where(i => !i.IsChecked).ToList();

        checkedItems.Should().ContainSingle(i => i.Name == "ägg" && i.Quantity == 2m);
        uncheckedItems.Should().Contain(i => i.Name == "mjölk" && i.Quantity == 4m && i.Unit == "dl");
        uncheckedItems.Should().Contain(i => i.Name == "ägg" && i.Quantity == 2m && i.Unit == "st");
    }

    // =========================================================================
    // K3. Skydd mot dubbelräkning och livscykel (Scenarion 15–23)
    // =========================================================================

    [Fact]
    public async Task K3_15_SameWeekRunTwice_DoesNotDuplicateAlreadyAddedItems()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Lasagne",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "köttfärs", RawText = "500 g köttfärs" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        // Första körningen
        var preview1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview1.NewItemsCount.Should().Be(1);
        preview1.AlreadyAddedCount.Should().Be(0);

        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            preview1.PreviewStamp,
            preview1.Items.Where(i => i.IsSelectedByDefault).Select(i => i.PreviewItemKey).ToList());

        // Andra körningen direkt efteråt
        var preview2 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview2.NewItemsCount.Should().Be(0);
        preview2.AlreadyAddedCount.Should().Be(1);
        preview2.Items[0].IsAlreadyAdded.Should().BeTrue();
        preview2.Items[0].IsSelectedByDefault.Should().BeFalse("redan tillagda varor ska inte vara förvalda igen");

        var itemsAfter = await _shoppingService.GetItemsAsync();
        itemsAfter.Should().ContainSingle(i => i.Name == "köttfärs" && i.Quantity == 500m);
    }

    [Fact]
    public async Task K3_17_PartialAddition_CanBeCompletedLaterWithoutDuplicatingAlreadyAddedItems()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Tacos",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "nötfärs", RawText = "500 g nötfärs" },
                    new Ingredient { OrderIndex = 2, Quantity = 1m, Unit = "burk", Name = "salsa", RawText = "1 burk salsa" }
                ]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        // 1. Lägg bara till nötfärs (avmarkera salsa)
        var preview1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        var beefKey = preview1.Items.Single(i => i.Item.Name == "nötfärs").PreviewItemKey;
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, preview1.PreviewStamp, [beefKey]);

        // 2. Öppna förhandsgranskningen igen: nötfärs är redan tillagd (ej förvald), salsa är ny (förvald)
        var preview2 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview2.NewItemsCount.Should().Be(1);
        preview2.AlreadyAddedCount.Should().Be(1);

        var beefItem2 = preview2.Items.Single(i => i.Item.Name == "nötfärs");
        beefItem2.IsAlreadyAdded.Should().BeTrue();
        beefItem2.IsSelectedByDefault.Should().BeFalse();

        var salsaItem2 = preview2.Items.Single(i => i.Item.Name == "salsa");
        salsaItem2.IsAlreadyAdded.Should().BeFalse();
        salsaItem2.IsSelectedByDefault.Should().BeTrue();

        // 3. Bekräfta de förvalda (endast salsa)
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            preview2.PreviewStamp,
            preview2.Items.Where(i => i.IsSelectedByDefault).Select(i => i.PreviewItemKey).ToList());

        var items = await _shoppingService.GetItemsAsync();
        items.Should().HaveCount(2);
        items.Single(i => i.Name == "nötfärs").Quantity.Should().Be(500m);
        items.Single(i => i.Name == "salsa").Quantity.Should().Be(1m);
    }

    [Fact]
    public async Task K3_18_ChangedServingsAfterPriorAddition_ProposesOnlyDeltaOnIncrease_AndNeverSubtractsOnDecrease()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Carbonara",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "grädde", RawText = "2 dl grädde" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 4; // 2 dl grädde
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, preview1.PreviewStamp, [preview1.Items[0].PreviewItemKey]);

        // Öka portionerna från 4 -> 6 (behovet blir 3 dl, redan tillagt är 2 dl -> föreslå bara 1 dl mellanskillnad!)
        menu.Days[0].Servings = 6;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var previewIncrease = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        previewIncrease.Items.Should().ContainSingle();
        var deltaItem = previewIncrease.Items[0];
        deltaItem.IsSelectedByDefault.Should().BeTrue();
        deltaItem.AlreadyAddedSummary.Should().NotBeNullOrWhiteSpace();
        deltaItem.Item.Quantity.Should().Be(1m);
        deltaItem.Item.Unit.Should().Be("dl");

        // Bekräfta mellanskillnaden -> totalt 3 dl i inköpslistan
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, previewIncrease.PreviewStamp, [deltaItem.PreviewItemKey]);
        var listAfterIncrease = await _shoppingService.GetItemsAsync();
        listAfterIncrease.Single(i => i.Name == "grädde").Quantity.Should().Be(3m);

        // Minska nu portionerna från 6 -> 2 (behovet är 1 dl, redan tillagt är 3 dl -> dra aldrig ifrån i inköpslistan!)
        menu.Days[0].Servings = 2;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var previewDecrease = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        previewDecrease.Items[0].IsAlreadyAdded.Should().BeTrue();
        previewDecrease.Items[0].IsSelectedByDefault.Should().BeFalse();

        var listAfterDecrease = await _shoppingService.GetItemsAsync();
        listAfterDecrease.Single(i => i.Name == "grädde").Quantity.Should().Be(3m);
    }

    [Fact]
    public async Task K3_19_ReplacedRecipeOnDay_ProposesNewRecipeWithoutDeletingExistingShoppingItems()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var r1 = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Fiskpinnar",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 1m, Unit = "förp", Name = "fiskpinnar", RawText = "1 förp fiskpinnar" }]
            },
            [catId]);
        var r2 = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Köttbullar",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "köttbullar", RawText = "500 g köttbullar" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = r1.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var p1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, p1.PreviewStamp, [p1.Items[0].PreviewItemKey]);

        // Byt ut måndagens recept mot Köttbullar
        menu.Days[0].RecipeId = r2.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var p2 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        p2.HasReplacedOrClearedMealsWithPriorAdditions.Should().BeTrue();
        p2.Items.Should().ContainSingle(i => i.Item.Name == "köttbullar" && i.IsSelectedByDefault);

        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, p2.PreviewStamp, [p2.Items[0].PreviewItemKey]);

        var shoppingItems = await _shoppingService.GetItemsAsync();
        shoppingItems.Should().Contain(i => i.Name == "fiskpinnar", "befintliga inköpsvaror får aldrig tas bort automatiskt");
        shoppingItems.Should().Contain(i => i.Name == "köttbullar");
    }

    [Fact]
    public async Task K3_20_ManuallyAddedItem_And_K3_21_RecipeAddedItem_AreNotMistakenForAlreadyAddedWeeklyMeal()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();

        var otherRecipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Frukost",
                Servings = 2,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "mjölk", RawText = "2 dl mjölk" }]
            },
            [catId]);

        var dinnerRecipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Stuvade makaroner",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 5m, Unit = "dl", Name = "mjölk", RawText = "5 dl mjölk" },
                    new Ingredient { OrderIndex = 2, Quantity = 4m, Unit = "dl", Name = "makaroner", RawText = "4 dl makaroner" }
                ]
            },
            [catId]);

        // Lägg till manuellt och från enskilt recept
        await _shoppingService.AddManualItemAsync("2 dl makaroner");
        await _shoppingService.AddRecipeIngredientsAsync(otherRecipe.Id, 2);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = dinnerRecipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        // Varken mjölk eller makaroner får markeras som "redan tillagd från veckomenyn", men båda ska visa överlapp i Att handla!
        preview.NewItemsCount.Should().Be(2);
        preview.AlreadyAddedCount.Should().Be(0);

        var milk = preview.Items.Single(i => i.Item.Name == "mjölk");
        milk.IsAlreadyAdded.Should().BeFalse();
        milk.IsSelectedByDefault.Should().BeTrue();
        milk.ExistingInUncheckedText.Should().Contain("2 dl mjölk");

        var pasta = preview.Items.Single(i => i.Item.Name == "makaroner");
        pasta.IsAlreadyAdded.Should().BeFalse();
        pasta.IsSelectedByDefault.Should().BeTrue();
        pasta.ExistingInUncheckedText.Should().Contain("2 dl makaroner");
    }

    [Fact]
    public async Task K3_22_DeletedItemDoesNotAutoResurrect_CanBeExplicitlyReselected_And_K3_23_UndoRestoresConsistentStatus()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Omelett",
                Servings = 2,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 4m, Unit = "st", Name = "ägg", RawText = "4 st ägg" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 2;
        await _weeklyMenuService.SaveDraftAsync(menu);

        // 1. Lägg till från veckomeny
        var p1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, p1.PreviewStamp, [p1.Items[0].PreviewItemKey]);

        var list1 = await _shoppingService.GetItemsAsync();
        var eggItem = list1.Single(i => i.Name == "ägg");

        // 2. Ta bort varan med ✕ (DeleteItemAsync)
        var deletedOk = await _shoppingService.DeleteItemAsync(eggItem.Id);
        deletedOk.Should().BeTrue();

        // 3. Öppna förhandsgranskningen igen: varan får INTE återuppstå som förvald, men ska tydligt visa att den tagits bort ur listan
        var pAfterDelete = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        pAfterDelete.Items.Should().ContainSingle();
        pAfterDelete.Items[0].IsAlreadyAdded.Should().BeTrue();
        pAfterDelete.Items[0].WasRemovedFromShoppingList.Should().BeTrue();
        pAfterDelete.Items[0].IsSelectedByDefault.Should().BeFalse("borttagen vara ska inte återuppstå automatiskt");

        // 4. Användaren ångrar borttagningen ("Ångra") -> varan finns åter i Att handla och WasRemovedFromShoppingList blir false
        await _shoppingService.RestoreDeletedItemAsync(eggItem);
        var pAfterUndo = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        pAfterUndo.Items[0].IsAlreadyAdded.Should().BeTrue();
        pAfterUndo.Items[0].WasRemovedFromShoppingList.Should().BeFalse();
        pAfterUndo.Items[0].ExistingInUncheckedText.Should().Contain("4 st ägg");

        // 5. Töm hela listan och välj om varan uttryckligen i förhandsgranskningen
        await _shoppingService.ClearAllItemsAsync();
        var pAfterClear = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        pAfterClear.Items[0].WasRemovedFromShoppingList.Should().BeTrue();
        pAfterClear.Items[0].IsSelectedByDefault.Should().BeFalse();

        // Uttryckligt omval av användaren
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, pAfterClear.PreviewStamp, [pAfterClear.Items[0].PreviewItemKey]);
        var listAfterReselect = await _shoppingService.GetItemsAsync();
        listAfterReselect.Should().ContainSingle(i => i.Name == "ägg" && i.Quantity == 4m);
    }

    // =========================================================================
    // K4. Samtidighet, datakonsistens och realtidssynk (Scenarion 24–28)
    // =========================================================================

    [Fact]
    public async Task K4_24_TwoPhonesWithSamePreview_And_K4_25_DoubleClick_And_K4_26_StalePreview_DoNotDuplicateQuantities()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Köttgryta",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 600m, Unit = "g", Name = "högrev", RawText = "600 g högrev" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var phoneAService = new ShoppingListService(_dbFactory, _aggregator, _parser, _notifier);
        var phoneBService = new ShoppingListService(_dbFactory, _aggregator, _parser, _notifier);

        // Både Telefon A och Telefon B öppnar förhandsgranskningen samtidigt och får samma PreviewStamp
        var previewA = await phoneAService.BuildWeeklyMenuPreviewAsync(weekDate);
        var previewB = await phoneBService.BuildWeeklyMenuPreviewAsync(weekDate);
        previewA.PreviewStamp.Should().Be(previewB.PreviewStamp);

        // Kör bekräftelse samtidigt (t.ex. två mobiler eller dubbelklick på samma stamp)
        var taskA = phoneAService.ConfirmWeeklyMenuAdditionsAsync(weekDate, previewA.PreviewStamp, [previewA.Items[0].PreviewItemKey]);
        var taskB = phoneBService.ConfirmWeeklyMenuAdditionsAsync(weekDate, previewB.PreviewStamp, [previewB.Items[0].PreviewItemKey]);

        var results = await Task.WhenAll(taskA, taskB);

        // Exakt en lyckas och den andra stoppas med Conflict och uppdaterad förhandsgranskning
        results.Count(r => r.Status == WeeklyMenuConfirmStatus.Success).Should().Be(1);
        results.Count(r => r.Status == WeeklyMenuConfirmStatus.Conflict).Should().Be(1);

        var conflictResult = results.Single(r => r.IsConflict);
        conflictResult.UpdatedPreview.AlreadyAddedCount.Should().Be(1);
        conflictResult.UpdatedPreview.NewItemsCount.Should().Be(0);

        // Inköpslistan har exakt 600 g högrev (aldrig 1200 g)
        var items = await _shoppingService.GetItemsAsync();
        items.Should().ContainSingle(i => i.Name == "högrev" && i.Quantity == 600m);

        // Om veckomenyn ändras efter att en förhandsgranskning hämtats avvisas det inaktuella stampet (K4 #26)
        var stalePreview = await phoneAService.BuildWeeklyMenuPreviewAsync(weekDate);
        menu.Days[0].Servings = 8;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var staleResult = await phoneAService.ConfirmWeeklyMenuAdditionsAsync(weekDate, stalePreview.PreviewStamp, [stalePreview.Items[0].PreviewItemKey]);
        staleResult.Status.Should().Be(WeeklyMenuConfirmStatus.Conflict);
        staleResult.UpdatedPreview.Items[0].Item.Quantity.Should().Be(600m, "uppdaterad förhandsgranskning visar den nya mellanskillnaden på 600 g");
    }

    [Fact]
    public async Task K4_27_FailedAddition_RollsBackTransactionLeavingZeroHalfWrittenItemsOrTracking_And_K4_28_NotifiesOnSuccess()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Lax i ugn",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "laxfilé", RawText = "500 g laxfilé" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        int notificationCount = 0;
        Action<long> handler = _ => notificationCount++;
        _notifier.OnChanged += handler;

        try
        {
            var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

            // Simulera ett oväntat databasfel precis före commit
            _shoppingService.BeforeWeeklyMenuCommitTestHook = () => throw new InvalidOperationException("Simulerat databasfel före commit");

            var act = async () => await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
                weekDate,
                preview.PreviewStamp,
                [preview.Items[0].PreviewItemKey]);

            await act.Should().ThrowAsync<InvalidOperationException>();

            // Verifiera att varken ShoppingListItems eller WeeklyMenuShoppingContributions sparats (full rollback)
            notificationCount.Should().Be(0);
            (await _shoppingService.GetItemsAsync()).Should().BeEmpty();
            await using (var db = _dbFactory.CreateDbContext())
            {
                (await db.WeeklyMenuShoppingContributions.CountAsync()).Should().Be(0);
            }

            // Ta bort test-hooken och verifiera att tillägget nu lyckas och triggar realtidsnotering (K4 #28)
            _shoppingService.BeforeWeeklyMenuCommitTestHook = null;
            var okResult = await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
                weekDate,
                preview.PreviewStamp,
                [preview.Items[0].PreviewItemKey]);

            okResult.Status.Should().Be(WeeklyMenuConfirmStatus.Success);
            notificationCount.Should().Be(1);
            (await _shoppingService.GetItemsAsync()).Should().ContainSingle(i => i.Name == "laxfilé" && i.Quantity == 500m);
        }
        finally
        {
            _notifier.OnChanged -= handler;
        }
    }

    // =========================================================================
    // K5 #35. Backup och återställning bevarar spårningen konsekvent
    // =========================================================================

    [Fact]
    public async Task K5_35_BackupAndRestore_PreservesWeeklyMenuAndShoppingContributions()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Ärtsoppa",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "gula ärtor", RawText = "500 g gula ärtor" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 6;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, preview.PreviewStamp, [preview.Items[0].PreviewItemKey]);

        var storageOpts = Options.Create(new ImageStorageOptions());

        // Exportera backup
        var backupService = new ZipBackupService(_dbFactory, storageOpts, NullLogger<ZipBackupService>.Instance);
        var zipBytes = await backupService.CreateBackupArchiveAsync();

        // Återställ med replaceExisting = true i ny databas
        using var targetDbFactory = new TestDbContextFactory();
        await using (var initDb = targetDbFactory.CreateDbContext())
        {
            await DbInitializer.InitializeAsync(initDb, NullLogger.Instance);
        }

        var restoreService = new ZipBackupService(targetDbFactory, storageOpts, NullLogger<ZipBackupService>.Instance);
        await using var zipStream = new MemoryStream(zipBytes);
        var restoreResult = await restoreService.RestoreFromArchiveAsync(zipStream, replaceExisting: true);
        restoreResult.Success.Should().BeTrue();

        var restoredShoppingService = new ShoppingListService(targetDbFactory, _aggregator, _parser, _notifier);
        var restoredPreview = await restoredShoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        restoredPreview.HasSavedMenu.Should().BeTrue();
        restoredPreview.Status.Should().Be(WeeklyMenuStatus.Confirmed);
        restoredPreview.PlannedMeals.Should().ContainSingle(m => m.PlannedServings == 6);
        restoredPreview.AlreadyAddedCount.Should().Be(1);
        restoredPreview.NewItemsCount.Should().Be(0);
        restoredPreview.Items[0].IsAlreadyAdded.Should().BeTrue();
    }
}
