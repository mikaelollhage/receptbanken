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

    public void Dispose()
    {
        _dbFactory.Dispose();
    }
}

