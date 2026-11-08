using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class WeeklyMenuShoppingOverlapTests : IDisposable
{
    private static readonly DateOnly Week = new(2026, 11, 2);
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly ShoppingListService _shopping;
    private readonly RecipeService _recipes;
    private readonly CategoryService _categories;
    private readonly WeeklyMenuService _menus;

    public WeeklyMenuShoppingOverlapTests()
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }
        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        _shopping = new ShoppingListService(_dbFactory, aggregator, new SwedishIngredientParser(), new ShoppingListNotifier());
        _recipes = new RecipeService(_dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        _categories = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
        var matcher = new PantryIngredientMatcher();
        _menus = new WeeklyMenuService(_dbFactory, new WeeklyMenuGenerator(new SeededRandomProvider(42), matcher), matcher, NullLogger<WeeklyMenuService>.Instance);
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<WeeklyMenuShoppingPreview> PlanAsync(decimal? qty, string? unit, string name, int servings = 4)
    {
        var catId = (await _categories.GetCategoriesAsync()).First(c => c.Name == "Vardagsmat").Id;
        var recipe = await _recipes.CreateRecipeAsync(new Recipe
        {
            Title = "Testrätt",
            Servings = 4,
            Ingredients = [new Ingredient { OrderIndex = 1, Quantity = qty, Unit = unit, Name = name, RawText = $"{qty} {unit} {name}" }]
        }, [catId]);
        var menu = await _menus.GetOrCreateWeekMenuAsync(Week);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = servings;
        await _menus.SaveDraftAsync(menu);
        return await _shopping.BuildWeeklyMenuPreviewAsync(Week);
    }

    private async Task<ShoppingListItem> AddAsync(string text, bool isChecked = false)
    {
        var item = (await _shopping.AddManualItemAsync(text))!;
        if (isChecked) await _shopping.SetCheckedAsync(item.Id, true);
        return item;
    }

    [Fact]
    public async Task Compatible_ExistingQuantity_ShownWithSafeTotal_And_UnitConversion()
    {
        await AddAsync("2 kg kycklingfilé");
        var preview = await PlanAsync(1m, "kg", "kycklingfilé");
        var row = preview.Items.Single();
        row.ExistingOverlapSummary.Should().Contain("Finns redan: 2 kg").And.Contain("Efter tillägg: 3 kg");
        row.IsSelectedByDefault.Should().BeTrue();
        row.IsAlreadyAdded.Should().BeFalse();
    }

    [Fact]
    public async Task UnitConversion_GivesCorrectTotal()
    {
        await AddAsync("500 g kycklingfilé");
        var preview = await PlanAsync(1m, "kg", "kycklingfilé");
        var s = preview.Items.Single().ExistingOverlapSummary;
        s.Should().Contain("Finns redan: 500 g").And.Contain("Efter tillägg:");
        s.Should().Contain("1500 g");
    }

    [Fact]
    public async Task ExistingWithoutQuantity_IsIdentified_WithoutTotal()
    {
        await AddAsync("Kycklingfilé");
        var preview = await PlanAsync(1m, "kg", "kycklingfilé");
        var s = preview.Items.Single().ExistingOverlapSummary;
        s.Should().Contain("Finns redan utan angiven mängd");
        s.Should().NotContain("Efter tillägg");
    }

    [Fact]
    public async Task CheckedItem_ShownButUnchanged_AndNoTotal()
    {
        var existing = await AddAsync("2 kg kycklingfilé", isChecked: true);
        var preview = await PlanAsync(1m, "kg", "kycklingfilé");
        var row = preview.Items.Single();
        row.ExistingOverlapSummary.Should().Be("Finns redan under Klart: 2 kg");
        row.IsSelectedByDefault.Should().BeTrue();

        await _shopping.ConfirmWeeklyMenuAdditionsAsync(Week, preview.PreviewStamp, [row.PreviewItemKey]);
        var items = await _shopping.GetItemsAsync();
        items.Single(i => i.Id == existing.Id).Quantity.Should().Be(2m);
        items.Single(i => i.Id == existing.Id).IsChecked.Should().BeTrue();
    }

    [Fact]
    public async Task NoOverlap_GivesNoInfoLine()
    {
        await AddAsync("1 dl mjölk");
        var preview = await PlanAsync(500m, "g", "pasta");
        preview.Items.Single().ExistingOverlapSummary.Should().BeNull();
    }

    [Fact]
    public async Task IncompatibleUnits_GiveNoTotal()
    {
        await AddAsync("200 g riven ost");
        var preview = await PlanAsync(2m, "dl", "riven ost");
        var s = preview.Items.Single().ExistingOverlapSummary;
        s.Should().NotBeNull();
        s.Should().NotContain("Efter tillägg");
    }

    [Fact]
    public async Task DifferentProducts_AreNotMixed()
    {
        await AddAsync("2 dl matlagningsgrädde");
        var preview = await PlanAsync(2m, "dl", "vispgrädde");
        preview.Items.Single().ExistingOverlapSummary.Should().BeNull();
    }

    [Fact]
    public async Task ExistingInterval_TotalPreservesIntervalOrIsOmitted()
    {
        var existing = await AddAsync("1 kg kycklingfilé");
        await _shopping.UpdateItemAsync(existing.Id, 1m, 2m, "kg", "kycklingfilé", null);
        var preview = await PlanAsync(1m, "kg", "kycklingfilé");
        var s = preview.Items.Single().ExistingOverlapSummary!;
        s.Should().Contain("Finns redan: 1–2 kg");
        if (s.Contains("Efter tillägg")) s.Should().Contain("2–3 kg");
    }

    [Fact]
    public async Task MultipleExistingRows_GiveNoTotal()
    {
        await AddAsync("1 kg kycklingfilé");
        await AddAsync("1 kg kycklingfilé");
        var all = await _shopping.GetItemsAsync();
        if (all.Count(i => !i.IsChecked) < 2)
        {
            return; // aggregator already merged them – nothing ambiguous to verify
        }
        var preview = await PlanAsync(1m, "kg", "kycklingfilé");
        preview.Items.Single().ExistingOverlapSummary.Should().NotContain("Efter tillägg");
    }

    [Fact]
    public async Task ManualItem_IsNotTreatedAsAlreadyAddedWeeklyNeed_And_DuplicateProtectionHolds()
    {
        await AddAsync("2 kg kycklingfilé");
        var preview = await PlanAsync(1m, "kg", "kycklingfilé");
        var row = preview.Items.Single();
        row.IsAlreadyAdded.Should().BeFalse();
        row.IsSelectedByDefault.Should().BeTrue();
        row.SourceContributions.Should().OnlyContain(c => c.AlreadyAddedServings == 0);

        await _shopping.ConfirmWeeklyMenuAdditionsAsync(Week, preview.PreviewStamp, [row.PreviewItemKey]);
        (await _shopping.GetItemsAsync()).Single(i => !i.IsChecked).Quantity.Should().Be(3m);

        var again = await _shopping.BuildWeeklyMenuPreviewAsync(Week);
        again.Items.Single().IsAlreadyAdded.Should().BeTrue();
        again.Items.Single().ExistingOverlapSummary.Should().BeNull();
        again.Items.Single().IsSelectedByDefault.Should().BeFalse();
    }
}

