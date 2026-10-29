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
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Components.Recipes;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class RecipeReusableComponentsTests : TestContext
{
    public RecipeReusableComponentsTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void RecipeBasicInfoFields_ShouldRenderConsistentLabelsAndPlaceholders()
    {
        var recipe = new Recipe
        {
            Title = "Klassisk Lasagne",
            Description = "En krämig och god lasagne med köttfärssås.",
            PrepTimeMinutes = 20,
            CookTimeMinutes = 45,
            Servings = 6,
            ServingsUnit = "portioner",
            SourceUrl = "https://example.com/lasagne"
        };

        var cut = RenderComponent<RecipeBasicInfoFields>(parameters => parameters
            .Add(p => p.Recipe, recipe)
            .Add(p => p.IdPrefix, "test"));

        // Labels
        cut.Markup.Should().Contain("Titel");
        cut.Markup.Should().Contain("Beskrivning");
        cut.Markup.Should().Contain("Förberedelsetid");
        cut.Markup.Should().Contain("Tillagningstid");
        cut.Markup.Should().Contain("Portioner");
        cut.Markup.Should().Contain("Portionsenhet");
        cut.Markup.Should().Contain("Ursprunglig källänk");

        // When fields have text/values, no asterisks should be rendered
        cut.FindAll(".required-asterisk").Should().BeEmpty();

        // Layout classes
        cut.FindAll(".recipe-meta-fields").Should().ContainSingle();
        cut.FindAll(".recipe-meta-row").Should().HaveCount(4);
        cut.FindAll(".recipe-meta-suffix").Select(s => s.TextContent.Trim()).Should().Equal("min", "min");
        cut.FindAll(".form-row").Should().ContainSingle();

        // Values rendered
        cut.Markup.Should().Contain("Klassisk Lasagne");
        cut.Markup.Should().Contain("https://example.com/lasagne");
    }

    [Fact]
    public void RecipeBasicInfoFields_ShouldRenderRedAsteriskOnlyWhenEmpty()
    {
        var emptyRecipe = new Recipe
        {
            Title = "",
            Servings = 0,
            ServingsUnit = "portioner"
        };

        var cut = RenderComponent<RecipeBasicInfoFields>(parameters => parameters
            .Add(p => p.Recipe, emptyRecipe)
            .Add(p => p.IdPrefix, "test"));

        // Both empty title and empty/zero servings should show the red asterisk
        var asterisks = cut.FindAll(".required-asterisk");
        asterisks.Should().HaveCount(2);
        foreach (var ast in asterisks)
        {
            ast.TextContent.Trim().Should().Be("*");
            ast.GetAttribute("style").Should().Contain("var(--rb-danger)");
        }

        // Fill in Title -> asterisk disappears for Title
        emptyRecipe.Title = "Min nya soppa";
        cut.Render();
        cut.FindAll(".required-asterisk").Should().HaveCount(1);

        // Fill in Servings -> asterisk disappears for Servings
        emptyRecipe.Servings = 4;
        cut.Render();
        cut.FindAll(".required-asterisk").Should().BeEmpty();
    }

    [Fact]
    public void RecipeBasicInfoFields_ShouldTwoWayBindValues()
    {
        var recipe = new Recipe
        {
            Title = "Starttitel",
            Servings = 2,
            ServingsUnit = "portioner"
        };

        var cut = RenderComponent<RecipeBasicInfoFields>(parameters => parameters
            .Add(p => p.Recipe, recipe)
            .Add(p => p.IdPrefix, "bind"));

        recipe.Title = "Uppdaterad titel";
        recipe.Servings = 8;
        recipe.PrepTimeMinutes = 15;
        recipe.CookTimeMinutes = 30;
        recipe.SourceUrl = "https://example.com/ny-url";

        cut.Render();

        cut.Markup.Should().Contain("Uppdaterad titel");
        cut.Markup.Should().Contain("https://example.com/ny-url");
    }

    [Fact]
    public void RecipeIngredientRow_ShouldRenderSpecifiedPlaceholdersAndLabels()
    {
        var ingredient = new Ingredient
        {
            Quantity = 2.5m,
            Unit = "dl",
            Name = "vetemjöl",
            Comment = "gärna eko",
            Preparation = "siktat",
            GroupHeader = "Sås"
        };

        var cut = RenderComponent<RecipeIngredientRow>(parameters => parameters
            .Add(p => p.Ingredient, ingredient));

        // Placeholders check
        cut.Markup.Should().Contain("placeholder=\"Mängd\"");
        cut.Markup.Should().Contain("placeholder=\"Enhet\"");
        cut.Markup.Should().Contain("placeholder=\"Råvara *\"");
        cut.Markup.Should().Contain("placeholder=\"Kommentar (t.ex. à 200 g)\"");
        cut.Markup.Should().Contain("placeholder=\"Beredning (t.ex. hackad)\"");
        cut.Markup.Should().Contain("placeholder=\"Grupp (valfritt)\"");

        // Remove button
        var removeBtn = cut.Find(".ingredient-remove-btn");
        removeBtn.TextContent.Trim().Should().Be("✕");
        removeBtn.GetAttribute("title").Should().Be("Ta bort rad");
    }

    [Fact]
    public void RecipeIngredientRow_ShouldBindIngredientCommentAndFields()
    {
        var ingredient = new Ingredient
        {
            Quantity = 1,
            Unit = "burk",
            Name = "krossade tomater",
            Comment = "à 400 g"
        };

        var cut = RenderComponent<RecipeIngredientRow>(parameters => parameters
            .Add(p => p.Ingredient, ingredient));

        cut.Markup.Should().Contain("krossade tomater");
        cut.Markup.Should().Contain("à 400 g");

        ingredient.Comment = "à 500 g finhackade";
        cut.Render();
        cut.Markup.Should().Contain("à 500 g finhackade");
    }

    [Fact]
    public void RecipeIngredientEditor_ShouldAddAndRemoveRows()
    {
        var ingredients = new List<Ingredient>
        {
            new() { Name = "Mjölk", Quantity = 5, Unit = "dl" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser")
            .Add(p => p.AddButtonText, "Lägg till ingrediens"));

        cut.FindAll(".ingredient-editor-row").Should().HaveCount(1);

        // Click Add
        var addBtn = cut.Find("button.btn-secondary-custom");
        addBtn.Click();

        ingredients.Should().HaveCount(2);
        cut.FindAll(".ingredient-editor-row").Should().HaveCount(2);

        // Click Remove on second row
        var removeBtns = cut.FindAll(".ingredient-remove-btn");
        removeBtns.ElementAt(1).Click();

        ingredients.Should().HaveCount(1);
        cut.FindAll(".ingredient-editor-row").Should().HaveCount(1);
        ingredients[0].Name.Should().Be("Mjölk");
    }

    [Fact]
    public void RecipeIngredientEditor_WithTitleFormatter_ShouldUpdateCountDynamically()
    {
        var ingredients = new List<Ingredient>
        {
            new() { Name = "Ägg", Quantity = 2, Unit = "st" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.TitleFormatter, count => $"Tolkade ingredienser ({count})")
            .Add(p => p.HeadingLevel, 3));

        cut.Find("h3.form-section-title").TextContent.Trim().Should().Be("Tolkade ingredienser (1)");

        cut.Find("button.btn-secondary-custom").Click();
        cut.Find("h3.form-section-title").TextContent.Trim().Should().Be("Tolkade ingredienser (2)");
    }

    [Fact]
    public void RecipeEdit_RendersSharedBasicInfoAndIngredientEditor()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);

        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);

        var cut = RenderComponent<RecipeEdit>();

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<RecipeBasicInfoFields>().Should().NotBeNull();
            cut.FindComponent<RecipeIngredientEditor>().Should().NotBeNull();
            cut.FindAll(".ingredient-editor-row").Should().ContainSingle();
            cut.FindAll(".ingredient-remove-btn").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task RecipeEdit_SaveRecipe_ReconstructsRawTextWithComment()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);

        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);

        var cut = RenderComponent<RecipeEdit>();

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<RecipeBasicInfoFields>().Should().NotBeNull();
        });

        // Set Title
        var basicInfo = cut.FindComponent<RecipeBasicInfoFields>();
        basicInfo.Instance.Recipe!.Title = "Test Recept";

        // Edit the single initial ingredient
        var ingredient = basicInfo.Instance.Recipe.Ingredients[0];
        ingredient.Quantity = 1;
        ingredient.Unit = "burk";
        ingredient.Name = "krossade tomater";
        ingredient.Comment = "à 400 g";
        ingredient.Preparation = "silade";
        ingredient.RawText = ""; // Empty to trigger reconstruction on save

        // Trigger save
        var saveBtn = cut.FindAll("button.btn-primary-custom").First();
        await cut.InvokeAsync(() => saveBtn.Click());

        // Verify ingredient RawText was reconstructed with comment and prep
        ingredient.RawText.Should().Be("1 burk krossade tomater (à 400 g), silade");

        // Verify recipe was saved to DB
        var savedRecipes = await recipeService.GetRecipesAsync();
        savedRecipes.Should().ContainSingle(r => r.Title == "Test Recept");
    }
}
