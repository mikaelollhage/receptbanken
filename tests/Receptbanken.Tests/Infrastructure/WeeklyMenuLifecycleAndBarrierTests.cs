using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Xunit;

namespace Receptbanken.Tests.Infrastructure;

public sealed class WeeklyMenuLifecycleAndBarrierTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly RecipeService _recipeService;
    private readonly CategoryService _categoryService;
    private readonly WeeklyMenuService _weeklyMenuService;
    private readonly ServingCalculator _calculator;
    private readonly ShoppingListAggregator _aggregator;
    private readonly SwedishIngredientParser _ingredientParser;
    private readonly VoiceShoppingListParser _voiceParser;
    private readonly ShoppingListNotifier _notifier;
    private readonly ShoppingListService _shoppingService;

    public WeeklyMenuLifecycleAndBarrierTests()
    {
        _dbFactory = new TestDbContextFactory();
        using (var db = _dbFactory.CreateDbContext())
        {
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        var imageStorage = new MockImageStorageService();
        _recipeService = new RecipeService(_dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        _categoryService = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        _weeklyMenuService = new WeeklyMenuService(_dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        _calculator = new ServingCalculator();
        _aggregator = new ShoppingListAggregator(_calculator);
        _ingredientParser = new SwedishIngredientParser();
        _voiceParser = new VoiceShoppingListParser(_ingredientParser, _aggregator);
        _notifier = new ShoppingListNotifier();
        _shoppingService = new ShoppingListService(_dbFactory, _aggregator, _ingredientParser, _notifier);
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<int> GetVardagCategoryIdAsync()
    {
        var cats = await _categoryService.GetCategoriesAsync();
        return cats.First(c => c.Name == "Vardagsmat").Id;
    }

    [Fact]
    public async Task ConfirmWeeklyMenuAdditions_WhenMenuIsDraft_ReturnsMenuNotConfirmed_AndAddsNothing()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Linsgryta",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 300m, Unit = "g", Name = "linser", RawText = "300 g röda linser" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.SaveDraftAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview.Status.Should().Be(WeeklyMenuStatus.Draft);

        var confirmResult = await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            preview.PreviewStamp,
            [preview.Items[0].PreviewItemKey]);

        confirmResult.Status.Should().Be(WeeklyMenuConfirmStatus.MenuNotConfirmed);
        confirmResult.AddedItemsCount.Should().Be(0);
        confirmResult.Message.Should().Contain("Endast spikade veckomenyer kan användas som underlag");

        // Verifiera att inga varor lades till i inköpslistan
        var listItems = await _shoppingService.GetItemsAsync();
        listItems.Should().BeEmpty();

        // Verifiera att inga WeeklyMenuShoppingContributions skapades
        using var db = _dbFactory.CreateDbContext();
        var contributions = db.WeeklyMenuShoppingContributions.ToList();
        contributions.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmWeeklyMenuAdditions_WhenMenuIsConfirmed_Succeeds_AndCreatesItemsAndContributions()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Linsgryta",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 300m, Unit = "g", Name = "linser", RawText = "300 g röda linser" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview.Status.Should().Be(WeeklyMenuStatus.Confirmed);

        var confirmResult = await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            preview.PreviewStamp,
            [preview.Items[0].PreviewItemKey]);

        confirmResult.Status.Should().Be(WeeklyMenuConfirmStatus.Success);
        confirmResult.AddedItemsCount.Should().Be(1);

        var listItems = await _shoppingService.GetItemsAsync();
        listItems.Should().ContainSingle(i => i.Name == "linser" && i.Quantity == 300m);

        using var db = _dbFactory.CreateDbContext();
        var contributions = db.WeeklyMenuShoppingContributions.ToList();
        contributions.Should().ContainSingle(c => c.RecipeId == recipe.Id && c.TargetServings == 4);
    }

    [Fact]
    public async Task ConfirmWeeklyMenuAdditions_WhenMenuDoesNotExist_ReturnsMenuNotFound()
    {
        var weekDate = new DateOnly(2026, 12, 1);
        var confirmResult = await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            "EMPTY:2026-11-30",
            ["dummy-key"]);

        confirmResult.Status.Should().Be(WeeklyMenuConfirmStatus.MenuNotFound);
        confirmResult.AddedItemsCount.Should().Be(0);
        confirmResult.Message.Should().Contain("Ingen sparad veckomeny hittades");
    }

    [Fact]
    public async Task ConfirmWeeklyMenuAdditions_WhenPreviewWasConfirmed_ButMenuRevertedToDraftBeforeConfirm_ReturnsMenuNotConfirmedOrConflict()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Ugnsfalukorv",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "falukorv", RawText = "500 g falukorv" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        // Skapa preview medan menyn var Confirmed
        var preview = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        preview.Status.Should().Be(WeeklyMenuStatus.Confirmed);

        // Ändra menyn till Draft i databasen precis före bekräftelse (t.ex. på en annan enhet)
        await _weeklyMenuService.SaveDraftAsync(menu);

        var confirmResult = await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            preview.PreviewStamp,
            [preview.Items[0].PreviewItemKey]);

        // Eftersom PreviewStamp skiljer sig eller statusen i DB nu är Draft, nekas tillägget
        confirmResult.Status.Should().Match(s => s == WeeklyMenuConfirmStatus.MenuNotConfirmed || s == WeeklyMenuConfirmStatus.Conflict);
        confirmResult.AddedItemsCount.Should().Be(0);

        var items = await _shoppingService.GetItemsAsync();
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task IndependentShoppingFlows_Manual_Voice_AndRecipe_AreNotBlockedByWeeklyMenuStatus()
    {
        var weekDate = new DateOnly(2026, 11, 2);

        // 1. Manuell inmatning fungerar oavsett veckomenyns status
        var manualItem = await _shoppingService.AddManualItemAsync("1 liter mjölk");
        manualItem.Should().NotBeNull();
        manualItem!.Name.Should().Be("mjölk");

        // 2. Röstinmatning fungerar oavsett veckomenyns status
        var voiceRequest = new VoiceShoppingListRequest { Items = ["2 st gurka", "500 g smör"] };
        var voiceResponse = await _shoppingService.AddVoiceItemsAsync(voiceRequest, _voiceParser);
        voiceResponse.Items.Should().HaveCount(2);
        voiceResponse.Items.Should().OnlyContain(r => r.Status == VoiceShoppingListItemStatus.Added);

        // 3. Enskilt recept kan läggas till oavsett veckomenyns status
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Pannkakor",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 3m, Unit = "st", Name = "ägg", RawText = "3 st ägg" }]
            },
            [catId]);

        var countAdded = await _shoppingService.AddRecipeIngredientsAsync(recipe.Id, 4);
        countAdded.Should().Be(1);

        var allItems = await _shoppingService.GetItemsAsync();
        allItems.Should().HaveCount(4); // mjölk, gurka, smör, ägg
    }

    [Fact]
    public async Task ConfirmMenuAsync_WhenMenuHasNoRecipes_ThrowsInvalidOperationException()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);

        // Alla sju dagar är tomma
        menu.Days.Should().OnlyContain(d => !d.RecipeId.HasValue);

        var act = async () => await _weeklyMenuService.ConfirmMenuAsync(menu);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*helt tom veckomeny kan inte spikas*");
    }

    [Fact]
    public async Task ConfirmMenuAsync_WhenMenuHasAtLeastOneRecipe_Succeeds()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe { Title = "Tisdagsfisk", Servings = 4 },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[1].RecipeId = recipe.Id; // Bara tisdag är planerad

        var confirmed = await _weeklyMenuService.ConfirmMenuAsync(menu);
        confirmed.Status.Should().Be(WeeklyMenuStatus.Confirmed);
        confirmed.ConfirmedAt.Should().NotBeNull();
        confirmed.Days[1].RecipeId.Should().Be(recipe.Id);
    }

    [Fact]
    public async Task SaveDraftAsync_WhenMenuHasNoRecipes_Succeeds()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);

        var draft = await _weeklyMenuService.SaveDraftAsync(menu);
        draft.Status.Should().Be(WeeklyMenuStatus.Draft);
        draft.ConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task ClearMenuAsync_OnConfirmedMenu_EmptiesAllDays_ResetsStatusToDraft_AndKeepsCategories()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(
            new Recipe { Title = "Lasagne", Servings = 4 },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 6;
        menu.Days[4].RecipeId = recipe.Id;
        menu.Days[4].Servings = 8;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        // Rensa menyn
        var cleared = await _weeklyMenuService.ClearMenuAsync(weekDate);

        cleared.Status.Should().Be(WeeklyMenuStatus.Draft);
        cleared.ConfirmedAt.Should().BeNull();
        cleared.Days.Should().HaveCount(7);
        cleared.Days.Should().OnlyContain(d => d.RecipeId == null && d.Recipe == null && d.Servings == null);

        // Kategoriinställningarna ska finnas kvar
        cleared.Days.Should().OnlyContain(d => d.SelectedCategories.Count > 0);

        // Läs in igen från databasen för att verifiera persistence
        var reloaded = await _weeklyMenuService.GetSavedMenuForWeekAsync(weekDate);
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(WeeklyMenuStatus.Draft);
        reloaded.ConfirmedAt.Should().BeNull();
        reloaded.Days.Should().OnlyContain(d => d.RecipeId == null && d.Servings == null);
    }

    [Fact]
    public async Task ClearMenuAsync_DoesNotAffectOtherWeeks()
    {
        var week1 = new DateOnly(2026, 11, 2);
        var week2 = new DateOnly(2026, 11, 9);
        var catId = await GetVardagCategoryIdAsync();
        var recipe = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Pasta", Servings = 4 }, [catId]);

        var menu1 = await _weeklyMenuService.GetOrCreateWeekMenuAsync(week1);
        menu1.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.ConfirmMenuAsync(menu1);

        var menu2 = await _weeklyMenuService.GetOrCreateWeekMenuAsync(week2);
        menu2.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.ConfirmMenuAsync(menu2);

        // Rensa endast vecka 1
        await _weeklyMenuService.ClearMenuAsync(week1);

        var reloaded1 = await _weeklyMenuService.GetSavedMenuForWeekAsync(week1);
        reloaded1!.Status.Should().Be(WeeklyMenuStatus.Draft);
        reloaded1.Days[0].RecipeId.Should().BeNull();

        var reloaded2 = await _weeklyMenuService.GetSavedMenuForWeekAsync(week2);
        reloaded2!.Status.Should().Be(WeeklyMenuStatus.Confirmed);
        reloaded2.Days[0].RecipeId.Should().Be(recipe.Id);
    }

    [Fact]
    public async Task ClearMenuAsync_OnNonExistentWeek_ReturnsDefaultDraft()
    {
        var weekDate = new DateOnly(2027, 5, 3);
        var cleared = await _weeklyMenuService.ClearMenuAsync(weekDate);

        cleared.Should().NotBeNull();
        cleared.Status.Should().Be(WeeklyMenuStatus.Draft);
        cleared.Days.Should().HaveCount(7);
        cleared.Days.Should().OnlyContain(d => d.RecipeId == null);
    }

    [Fact]
    public async Task ClearMenu_PreservesContributions_AndAvoidsDuplicatesWhenReplanned()
    {
        // 1. En spikad vecka innehåller Kycklingpasta (4 portioner på måndag)
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var chickenPasta = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Kycklingpasta",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "kycklingfilé", RawText = "500 g kycklingfilé" },
                    new Ingredient { OrderIndex = 2, Quantity = 400m, Unit = "g", Name = "pasta", RawText = "400 g pasta" }
                ]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = chickenPasta.Id;
        menu.Days[0].Servings = 4;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        // 2. Användaren lägger till ingredienserna i inköpslistan
        var p1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            p1.PreviewStamp,
            p1.Items.Select(i => i.PreviewItemKey).ToList());

        var initialList = await _shoppingService.GetItemsAsync();
        initialList.Should().HaveCount(2);
        initialList.Single(i => i.Name == "kycklingfilé").Quantity.Should().Be(500m);
        initialList.Single(i => i.Name == "pasta").Quantity.Should().Be(400m);

        // 3. Användaren rensar veckomenyn
        var cleared = await _weeklyMenuService.ClearMenuAsync(weekDate);
        cleared.Status.Should().Be(WeeklyMenuStatus.Draft);

        // 4. Inköpsvarorna ligger kvar oförändrade!
        var listAfterClear = await _shoppingService.GetItemsAsync();
        listAfterClear.Should().HaveCount(2);

        // 5 & 6. Användaren planerar samma vecka på nytt och väljer Kycklingpasta igen
        var replannedMenu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        replannedMenu.Days[0].RecipeId = chickenPasta.Id;
        replannedMenu.Days[0].Servings = 4;

        // 7. Veckan spikas
        await _weeklyMenuService.ConfirmMenuAsync(replannedMenu);

        // 8. Användaren öppnar inköpsförhandsgranskningen igen
        var previewAfterReplan = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        // VERIFIERING: Eftersom bidragshistoriken bevarades, känner systemet igen att kycklingfilé och pasta redan är tillagda!
        previewAfterReplan.NewItemsCount.Should().Be(0);
        previewAfterReplan.AlreadyAddedCount.Should().Be(2);
        previewAfterReplan.Items.Should().OnlyContain(i => i.IsAlreadyAdded && !i.IsSelectedByDefault);

        // Bekräftelse lägger inte till några dubbletter i inköpslistan
        var reconfirm = await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            weekDate,
            previewAfterReplan.PreviewStamp,
            previewAfterReplan.Items.Where(i => i.IsSelectedByDefault).Select(i => i.PreviewItemKey).ToList());

        reconfirm.Status.Should().Be(WeeklyMenuConfirmStatus.NoSelection);
        (await _shoppingService.GetItemsAsync()).Single(i => i.Name == "kycklingfilé").Quantity.Should().Be(500m);
    }

    [Fact]
    public async Task PortionIncreaseAfterReplan_AddsOnlyDelta()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var soup = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Grönsakssoppa",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 400m, Unit = "g", Name = "morötter", RawText = "400 g morötter" }]
            },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = soup.Id;
        menu.Days[0].Servings = 4;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        var p1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, p1.PreviewStamp, [p1.Items[0].PreviewItemKey]);

        // Rensa och planera om med 6 portioner (+50% morötter = 600 g totalt)
        await _weeklyMenuService.ClearMenuAsync(weekDate);

        var replanned = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        replanned.Days[0].RecipeId = soup.Id;
        replanned.Days[0].Servings = 6;
        await _weeklyMenuService.ConfirmMenuAsync(replanned);

        var p2 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        p2.Items.Should().ContainSingle();
        var carrotItem = p2.Items[0];

        // Endast mellanskillnaden på 200 g föreslås!
        carrotItem.Item.Quantity.Should().Be(200m);
        carrotItem.IsSelectedByDefault.Should().BeTrue();

        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, p2.PreviewStamp, [carrotItem.PreviewItemKey]);
        var list = await _shoppingService.GetItemsAsync();
        list.Single(i => i.Name == "morötter").Quantity.Should().Be(600m);
    }

    [Fact]
    public async Task DifferentRecipeAfterReplan_FlagsPriorAdditions()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var catId = await GetVardagCategoryIdAsync();
        var r1 = await _recipeService.CreateRecipeAsync(
            new Recipe { Title = "Köttfärssås", Servings = 4, Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "köttfärs", RawText = "500 g köttfärs" }] },
            [catId]);
        var r2 = await _recipeService.CreateRecipeAsync(
            new Recipe { Title = "Fisksoppa", Servings = 4, Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 400m, Unit = "g", Name = "torskfilé", RawText = "400 g torskfilé" }] },
            [catId]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = r1.Id;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        var p1 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);
        await _shoppingService.ConfirmWeeklyMenuAdditionsAsync(weekDate, p1.PreviewStamp, [p1.Items[0].PreviewItemKey]);

        // Rensa menyn
        await _weeklyMenuService.ClearMenuAsync(weekDate);

        // Planera om med Fisksoppa
        var replanned = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        replanned.Days[0].RecipeId = r2.Id;
        await _weeklyMenuService.ConfirmMenuAsync(replanned);

        var p2 = await _shoppingService.BuildWeeklyMenuPreviewAsync(weekDate);

        // Systemet varnar för att tidigare inlagda rätter har tagits bort/ersatts
        p2.HasReplacedOrClearedMealsWithPriorAdditions.Should().BeTrue();
        p2.Items.Should().ContainSingle(i => i.Item.Name == "torskfilé" && i.IsSelectedByDefault);

        // Köttfärsen ligger kvar i inköpslistan
        var list = await _shoppingService.GetItemsAsync();
        list.Should().ContainSingle(i => i.Name == "köttfärs");
    }
}
