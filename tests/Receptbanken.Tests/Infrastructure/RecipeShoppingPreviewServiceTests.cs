using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class RecipeShoppingPreviewServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly ShoppingListService _shopping;
    private readonly RecipeService _recipes;
    private readonly CategoryService _categories;

    public RecipeShoppingPreviewServiceTests()
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }
        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        _shopping = new ShoppingListService(_dbFactory, aggregator, new SwedishIngredientParser(), new ShoppingListNotifier());
        _recipes = new RecipeService(_dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        _categories = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<Recipe> CreateRecipeAsync(string title, int servings, params (decimal? qty, string? unit, string name)[] ingredients)
    {
        var catId = (await _categories.GetCategoriesAsync()).First(c => c.Name == "Vardagsmat").Id;
        var ingredientList = ingredients.Select((ing, idx) => new Ingredient
        {
            OrderIndex = idx + 1,
            Quantity = ing.qty,
            Unit = ing.unit,
            Name = ing.name,
            RawText = $"{ing.qty} {ing.unit} {ing.name}".Trim()
        }).ToList();

        return await _recipes.CreateRecipeAsync(new Recipe
        {
            Title = title,
            Servings = servings,
            ServingsUnit = "portioner",
            Ingredients = ingredientList
        }, [catId]);
    }

    private async Task<ShoppingListItem> AddShoppingItemAsync(string text, bool isChecked = false)
    {
        var item = (await _shopping.AddManualItemAsync(text))!;
        if (isChecked) await _shopping.SetCheckedAsync(item.Id, true);
        return item;
    }

    [Fact]
    public async Task BuildRecipeShoppingPreview_ShouldScaleQuantitiesForTargetServings()
    {
        var recipe = await CreateRecipeAsync("Laxpasta", 2,
            (300m, "g", "laxfilé"),
            (200m, "g", "pasta"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);

        preview.Should().NotBeNull();
        preview!.RecipeId.Should().Be(recipe.Id);
        preview.RecipeTitle.Should().Be("Laxpasta");
        preview.BaseServings.Should().Be(2);
        preview.TargetServings.Should().Be(4);
        preview.Items.Should().HaveCount(2);

        var lax = preview.Items.First(i => i.Item.Name == "laxfilé");
        lax.Item.Quantity.Should().Be(600m);
        lax.Item.Unit.Should().Be("g");
        lax.IsSelectedByDefault.Should().BeTrue();

        var pasta = preview.Items.First(i => i.Item.Name == "pasta");
        pasta.Item.Quantity.Should().Be(400m);
        pasta.Item.Unit.Should().Be("g");
        pasta.IsSelectedByDefault.Should().BeTrue();
    }

    [Fact]
    public async Task BuildRecipeShoppingPreview_ShouldIdentifyExistingUncheckedItemsAndShowTotal()
    {
        await AddShoppingItemAsync("2 kg kycklingfilé");

        var recipe = await CreateRecipeAsync("Kycklinggryta", 4, (1m, "kg", "kycklingfilé"));
        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);

        preview.Should().NotBeNull();
        var item = preview!.Items.Single();
        item.ExistingInUncheckedText.Should().Be("2 kg kycklingfilé");
        item.ExistingOverlapSummary.Should().Contain("Finns redan: 2 kg");
        item.ExistingOverlapSummary.Should().Contain("Efter tillägg: 3 kg");
    }

    [Fact]
    public async Task BuildRecipeShoppingPreview_ShouldIdentifyCheckedItems()
    {
        await AddShoppingItemAsync("2 kg kycklingfilé", isChecked: true);

        var recipe = await CreateRecipeAsync("Kycklinggryta", 4, (1m, "kg", "kycklingfilé"));
        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);

        preview.Should().NotBeNull();
        var item = preview!.Items.Single();
        item.ExistingInCheckedText.Should().Be("2 kg kycklingfilé");
        item.ExistingOverlapSummary.Should().Be("Finns redan under Klart: 2 kg");
    }

    [Fact]
    public async Task BuildRecipeShoppingPreview_ShouldShowUnitConflictWarning()
    {
        await AddShoppingItemAsync("1 burk krossade tomater");

        var recipe = await CreateRecipeAsync("Tomatsås", 4, (500m, "g", "krossade tomater"));
        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);

        preview.Should().NotBeNull();
        var item = preview!.Items.Single();
        item.ConflictWarning.Should().Contain("annan enhet/förpackning – läggs som separat rad");
    }

    [Fact]
    public async Task ConfirmRecipeShoppingAdditions_WithDeselectedItem_ShouldOnlyAddSelectedItems()
    {
        var recipe = await CreateRecipeAsync("Krämig Pasta", 4,
            (400m, "g", "pasta"),
            (2m, "dl", "grädde"),
            (1m, "krm", "salt"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        preview.Should().NotBeNull();

        // Användaren avmarkerar saltet (har redan hemma)
        var selectedKeys = preview!.Items
            .Where(i => i.Item.Name != "salt")
            .Select(i => i.PreviewItemKey)
            .ToList();

        var result = await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            recipe.Id,
            4,
            preview.PreviewStamp,
            selectedKeys);

        result.Status.Should().Be(RecipeShoppingConfirmStatus.Success);
        result.AddedItemsCount.Should().Be(2);

        var dbItems = await _shopping.GetItemsAsync();
        dbItems.Should().HaveCount(2);
        dbItems.Should().Contain(i => i.Name == "pasta");
        dbItems.Should().Contain(i => i.Name == "grädde");
        dbItems.Should().NotContain(i => i.Name == "salt");
    }

    [Fact]
    public async Task ConfirmRecipeShoppingAdditions_WithNoSelection_ShouldReturnNoSelectionAndNotPersist()
    {
        var recipe = await CreateRecipeAsync("Krämig Pasta", 4, (400m, "g", "pasta"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        preview.Should().NotBeNull();

        var result = await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            recipe.Id,
            4,
            preview!.PreviewStamp,
            []);

        result.Status.Should().Be(RecipeShoppingConfirmStatus.NoSelection);
        result.AddedItemsCount.Should().Be(0);

        var dbItems = await _shopping.GetItemsAsync();
        dbItems.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmRecipeShoppingAdditions_WhenShoppingListChanges_ShouldReturnConflictAndNotWrite()
    {
        var recipe = await CreateRecipeAsync("Laxpasta", 4, (400m, "g", "laxfilé"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        preview.Should().NotBeNull();

        // Simulerar att en annan användare/enhet lägger till en vara i inköpslistan
        await AddShoppingItemAsync("1 l mjölk");

        // Försök bekräfta med den gamla preview stampen
        var result = await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            recipe.Id,
            4,
            preview!.PreviewStamp,
            [preview.Items[0].PreviewItemKey]);

        result.Status.Should().Be(RecipeShoppingConfirmStatus.Conflict);
        result.IsConflict.Should().BeTrue();
        result.AddedItemsCount.Should().Be(0);

        // Kontrollera att laxfilé inte har sparats
        var dbItems = await _shopping.GetItemsAsync();
        dbItems.Should().ContainSingle(i => i.Name == "mjölk");
        dbItems.Should().NotContain(i => i.Name == "laxfilé");

        // UpdatedPreview har den nya korrekta stämpeln
        result.UpdatedPreview.PreviewStamp.Should().NotBe(preview.PreviewStamp);
    }

    [Fact]
    public async Task ConfirmRecipeShoppingAdditions_WhenCommitHookThrows_ShouldRollbackTransaction()
    {
        var recipe = await CreateRecipeAsync("Laxpasta", 4, (400m, "g", "laxfilé"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        preview.Should().NotBeNull();

        _shopping.BeforeRecipeShoppingCommitTestHook = () => throw new InvalidOperationException("Simulerad krasch");

        var act = async () => await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            recipe.Id,
            4,
            preview!.PreviewStamp,
            [preview.Items[0].PreviewItemKey]);

        await act.Should().ThrowAsync<InvalidOperationException>();

        _shopping.BeforeRecipeShoppingCommitTestHook = null;

        var dbItems = await _shopping.GetItemsAsync();
        dbItems.Should().BeEmpty("transaktionen ska ha rullats tillbaka");
    }

    [Fact]
    public async Task ConfirmRecipeShoppingAdditions_WhenRecipeNotFound_ShouldReturnRecipeNotFound()
    {
        var result = await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            99999,
            4,
            "dummy_stamp",
            ["some_key"]);

        result.Status.Should().Be(RecipeShoppingConfirmStatus.RecipeNotFound);
        result.AddedItemsCount.Should().Be(0);
    }
}
