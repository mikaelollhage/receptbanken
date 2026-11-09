using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Recipes;

namespace Receptbanken.Tests.Components;

public class RecipeShoppingPreviewModalTests : TestContext
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly ShoppingListNotifier _sharedNotifier;
    private readonly RecipeService _recipeService;
    private readonly CategoryService _categoryService;
    private readonly ShoppingListService _shoppingService;

    public RecipeShoppingPreviewModalTests()
    {
        _dbFactory = new TestDbContextFactory();
        _sharedNotifier = new ShoppingListNotifier();

        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();
        var imageStorage = new MockImageStorageService();

        _recipeService = new RecipeService(_dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        _categoryService = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
        _shoppingService = new ShoppingListService(_dbFactory, aggregator, parser, _sharedNotifier);

        Services.AddFluentUIComponents();
        Services.AddSingleton(calc);
        Services.AddSingleton(_sharedNotifier);
        Services.AddSingleton(_recipeService);
        Services.AddSingleton(_categoryService);
        Services.AddSingleton(_shoppingService);

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<Recipe> CreateRecipeAsync(string title, int servings, params (string Name, string RawText)[] ingredients)
    {
        await using var db = _dbFactory.CreateDbContext();
        var recipe = new Recipe
        {
            Title = title,
            Servings = servings,
            ServingsUnit = "portioner",
            CookTimeMinutes = 20
        };

        var parser = new SwedishIngredientParser();
        int order = 1;
        foreach (var ing in ingredients)
        {
            var parsed = parser.Parse(ing.RawText);
            recipe.Ingredients.Add(new Ingredient
            {
                OrderIndex = order++,
                Name = ing.Name,
                Quantity = parsed.Quantity,
                QuantityMax = parsed.QuantityMax,
                Unit = parsed.Unit,
                Comment = parsed.Comment,
                RawText = ing.RawText
            });
        }

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe;
    }

    [Fact]
    public void WhenInvisible_ShouldNotRenderDialog()
    {
        var cut = RenderComponent<RecipeShoppingPreviewModal>(parameters => parameters
            .Add(p => p.IsVisible, false)
            .Add(p => p.RecipeId, 1)
            .Add(p => p.Servings, 4));

        cut.FindAll(".weekly-shopping-preview-dialog").Should().BeEmpty();
    }

    [Fact]
    public async Task WhenVisible_ShouldRenderScaledIngredientsAndCounts()
    {
        var recipe = await CreateRecipeAsync("Gräddig Pasta", 2,
            ("pasta", "200 g pasta"),
            ("grädde", "2 dl grädde"));

        var cut = RenderComponent<RecipeShoppingPreviewModal>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.RecipeId, recipe.Id)
            .Add(p => p.Servings, 4));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-shopping-preview-dialog").Should().ContainSingle();
            var text = cut.Find(".weekly-shopping-preview-dialog").TextContent;
            text.Should().Contain("Gräddig Pasta");
            text.Should().Contain("400 g pasta");
            text.Should().Contain("4 dl grädde");
            text.Should().Contain("2 varor att lägga till");
        });
    }

    [Fact]
    public async Task DeselectCheckbox_ShouldUpdateSelectedCount()
    {
        var recipe = await CreateRecipeAsync("Pasta Pesto", 4,
            ("pasta", "400 g pasta"),
            ("pesto", "1 burk pesto"));

        var cut = RenderComponent<RecipeShoppingPreviewModal>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.RecipeId, recipe.Id)
            .Add(p => p.Servings, 4));

        cut.WaitForAssertion(() => cut.FindAll(".weekly-preview-checkbox").Should().HaveCount(2));

        // Avmarkera första varan (pasta)
        var firstCheckbox = cut.FindAll(".weekly-preview-checkbox").First();
        firstCheckbox.Change(false);

        cut.WaitForAssertion(() =>
        {
            var summary = cut.Find(".weekly-preview-summary-counts").TextContent;
            summary.Should().Contain("1 vara att lägga till");
            cut.Find(".weekly-preview-confirm-btn").TextContent.Should().Contain("(1)");
        });
    }

    [Fact]
    public async Task DeselectAll_ShouldDisableConfirmButton_And_SelectAllShouldReenable()
    {
        var recipe = await CreateRecipeAsync("Frukost", 4,
            ("ägg", "4 st ägg"),
            ("bröd", "4 skivor bröd"));

        var cut = RenderComponent<RecipeShoppingPreviewModal>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.RecipeId, recipe.Id)
            .Add(p => p.Servings, 4));

        cut.WaitForAssertion(() => cut.FindAll(".weekly-preview-deselect-all-btn").Should().ContainSingle());

        // Klicka Avmarkera alla
        cut.Find(".weekly-preview-deselect-all-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("0 varor att lägga till");
            cut.Find(".weekly-preview-confirm-btn").HasAttribute("disabled").Should().BeTrue();
        });

        // Klicka Markera alla
        cut.Find(".weekly-preview-select-new-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("2 varor att lägga till");
            cut.Find(".weekly-preview-confirm-btn").HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task CancelButton_ShouldInvokeOnCancelCallback()
    {
        var recipe = await CreateRecipeAsync("Middag", 2, ("torsk", "300 g torsk"));
        bool cancelInvoked = false;

        var cut = RenderComponent<RecipeShoppingPreviewModal>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.RecipeId, recipe.Id)
            .Add(p => p.Servings, 2)
            .Add(p => p.OnCancel, () => cancelInvoked = true));

        cut.WaitForAssertion(() => cut.FindAll(".weekly-preview-cancel-btn").Should().ContainSingle());
        cut.Find(".weekly-preview-cancel-btn").Click();

        cancelInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmButton_ShouldPersistSelectedAndInvokeOnConfirmed()
    {
        var recipe = await CreateRecipeAsync("Middag", 2, ("torsk", "300 g torsk"));
        RecipeShoppingConfirmResult? confirmResult = null;

        var cut = RenderComponent<RecipeShoppingPreviewModal>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.RecipeId, recipe.Id)
            .Add(p => p.Servings, 2)
            .Add(p => p.OnConfirmed, (RecipeShoppingConfirmResult res) => confirmResult = res));

        cut.WaitForAssertion(() => cut.FindAll(".weekly-preview-confirm-btn").Should().ContainSingle());
        cut.Find(".weekly-preview-confirm-btn").Click();

        cut.WaitForAssertion(() =>
        {
            confirmResult.Should().NotBeNull();
            confirmResult!.Status.Should().Be(RecipeShoppingConfirmStatus.Success);
            confirmResult.AddedItemsCount.Should().Be(1);
        });

        var items = await _shoppingService.GetItemsAsync();
        items.Should().ContainSingle(i => i.Name == "torsk");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dbFactory.Dispose();
        }
        base.Dispose(disposing);
    }
}
