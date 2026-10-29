using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Common;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Components.Recipes;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class Phase1ComponentTests : TestContext
{
    [Fact]
    public void CategoryBadge_ShouldRenderNameAndCount()
    {
        var cut = RenderComponent<CategoryBadge>(parameters => parameters
            .Add(p => p.Name, "Vardagsmat")
            .Add(p => p.ColorHex, "#e07a5f")
            .Add(p => p.Count, 5)
            .Add(p => p.IsActive, true));

        cut.Markup.Should().Contain("Vardagsmat");
        cut.Markup.Should().Contain("(5)");
        cut.Find(".category-badge").ClassList.Should().Contain("active");
    }

    [Fact]
    public void ServingAdjuster_ShouldIncrementAndDecrementServings()
    {
        int notifiedServings = 0;
        var cut = RenderComponent<ServingAdjuster>(parameters => parameters
            .Add(p => p.CurrentServings, 4)
            .Add(p => p.Unit, "portioner")
            .Add(p => p.OnServingsChanged, EventCallback.Factory.Create<int>(this, val => notifiedServings = val)));

        cut.Find(".servings-count").TextContent.Should().Be("4 portioner");

        var buttons = cut.FindAll("button.servings-btn");
        buttons.ElementAt(1).Click(); // Increment (+)
        notifiedServings.Should().Be(5);

        buttons.ElementAt(0).Click(); // Decrement (-)
        notifiedServings.Should().Be(4);
    }

    [Fact]
    public void ConfirmDialog_WhenVisible_ShouldInvokeCallbacks()
    {
        bool confirmed = false;
        var cut = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.Title, "Radera recept")
            .Add(p => p.Message, "Är du säker?")
            .Add(p => p.ConfirmText, "Ja, radera")
            .Add(p => p.OnConfirm, EventCallback.Factory.Create(this, () => confirmed = true)));

        cut.Markup.Should().Contain("Radera recept");
        cut.Find("button.btn-danger-custom").Click();
        confirmed.Should().BeTrue();
    }

    [Fact]
    public async Task RecipeCard_ShouldRenderSemanticTitleLinkAndToggleFavorite()
    {
        using var dbFactory = new TestDbContextFactory();
        var recipeService = new RecipeService(dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        var created = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Klassiska Köttbullar",
                Description = "Serveras med potatismos och rårörda lingon.",
                PrepTimeMinutes = 20,
                CookTimeMinutes = 15,
                Servings = 4,
                ServingsUnit = "portioner"
            },
            []);

        Services.AddSingleton(recipeService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        bool favoriteToggled = false;
        var cut = RenderComponent<RecipeCard>(parameters => parameters
            .Add(p => p.Recipe, created)
            .Add(p => p.OnFavoriteToggled, EventCallback.Factory.Create<Recipe>(this, _ => favoriteToggled = true)));

        var titleLink = cut.Find("a.recipe-card-link");
        titleLink.TextContent.Should().Be("Klassiska Köttbullar");
        titleLink.GetAttribute("href").Should().Be($"/recept/{created.Id}");
        cut.Find(".recipe-card-desc").TextContent.Should().Contain("potatismos");
        cut.Find(".recipe-card-meta").TextContent.Should().Contain("35 min");

        var favBtn = cut.Find("button.recipe-card-fav-btn");
        favBtn.Click();

        cut.WaitForAssertion(() =>
        {
            favoriteToggled.Should().BeTrue();
            favBtn.ClassList.Should().Contain("is-fav");
        });
    }

    [Fact]
    public void RecipeEdit_ShouldRenderResponsiveFormLayoutClasses()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<RecipeEdit>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".form-row").Should().NotBeEmpty();
            cut.FindAll(".image-upload-grid").Should().ContainSingle();
            cut.FindAll(".ingredient-editor-row").Should().ContainSingle();
            cut.FindAll(".ingredient-remove-btn").Should().ContainSingle();
            cut.FindAll(".recipe-meta-fields").Should().ContainSingle();
            cut.FindAll(".recipe-meta-row").Should().HaveCount(4);
            cut.FindAll(".recipe-meta-suffix").Select(e => e.TextContent.Trim()).Should().Equal("min", "min");
        });
    }

    [Fact]
    public async Task ShoppingPlaceholder_ShouldRenderIngredientsWithProperSpacing()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();
        var shoppingService = new ShoppingListService(dbFactory, aggregator, parser);

        await shoppingService.AddManualItemAsync("4 portins");
        await shoppingService.AddManualItemAsync("4 kycklingfiléer");
        await shoppingService.AddManualItemAsync("0,5 dl vetemjöl");
        await shoppingService.AddManualItemAsync("2 msk olivolja");
        await shoppingService.AddManualItemAsync("2 vitlöksklyftor");
        await shoppingService.AddManualItemAsync("1 burk krossade tomater (à 400 g)");

        Services.AddFluentUIComponents();
        Services.AddSingleton(shoppingService);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(calc);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.WaitForAssertion(() =>
        {
            var itemTexts = cut.FindAll(".shopping-item-text")
                .Select(el => el.TextContent.Trim())
                .ToList();

            itemTexts.Should().Contain("4 portins");
            itemTexts.Should().Contain("4 kycklingfiléer");
            itemTexts.Should().Contain("0,5 dl vetemjöl");
            itemTexts.Should().Contain("2 msk olivolja");
            itemTexts.Should().Contain("2 vitlöksklyftor");
            itemTexts.Should().Contain("1 burk krossade tomater");
        });
    }
}

