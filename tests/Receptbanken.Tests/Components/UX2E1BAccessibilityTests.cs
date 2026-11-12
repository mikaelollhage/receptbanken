using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Common;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Components.Recipes;
using Receptbanken.Web.Components.WeeklyMenu;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class UX2E1BAccessibilityTests : TestContext
{
    public UX2E1BAccessibilityTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void CategoryBadge_WhenInteractive_RendersAsButtonWithAriaPressed()
    {
        bool clicked = false;
        var cut = RenderComponent<CategoryBadge>(parameters => parameters
            .Add(p => p.Name, "Vegetariskt")
            .Add(p => p.ColorHex, "#2a9d8f")
            .Add(p => p.IsActive, true)
            .Add(p => p.OnClick, EventCallback.Factory.Create(this, () => clicked = true)));

        var button = cut.Find("button.category-badge");
        button.Should().NotBeNull();
        button.GetAttribute("type").Should().Be("button");
        button.GetAttribute("aria-pressed").Should().Be("true");
        button.TextContent.Should().Contain("Vegetariskt");

        button.Click();
        clicked.Should().BeTrue();
    }

    [Fact]
    public void CategoryBadge_WhenInformative_RendersAsSpanWithoutButtonOrTabstop()
    {
        var cut = RenderComponent<CategoryBadge>(parameters => parameters
            .Add(p => p.Name, "Middag")
            .Add(p => p.ColorHex, "#e76f51")
            .Add(p => p.IsActive, false));

        cut.FindAll("button").Should().BeEmpty();
        var span = cut.Find("span.category-badge");
        span.Should().NotBeNull();
        span.GetAttribute("tabindex").Should().BeNull();
    }

    [Fact]
    public void RecipeCategorySelector_RendersSemanticButtons_WithRoleGroupAndAriaPressed()
    {
        var categories = new List<CategoryWithCountDto>
        {
            new() { Id = 1, Name = "Vardag", ColorHex = "#e07a5f" },
            new() { Id = 2, Name = "Helg", ColorHex = "#10b981" }
        };
        var selected = new HashSet<int> { 1 };
        ISet<int>? updated = null;

        var cut = RenderComponent<RecipeCategorySelector>(parameters => parameters
            .Add(p => p.Categories, categories)
            .Add(p => p.SelectedCategoryIds, selected)
            .Add(p => p.SelectedCategoryIdsChanged, EventCallback.Factory.Create<ISet<int>>(this, s => updated = s)));

        var group = cut.Find("[role='group']");
        group.Should().NotBeNull();

        var buttons = cut.FindAll("button.category-badge");
        buttons.Should().HaveCount(2);

        // Cat 1 selected, Cat 2 not selected
        buttons.ElementAt(0).GetAttribute("aria-pressed").Should().Be("true");
        buttons.ElementAt(1).GetAttribute("aria-pressed").Should().Be("false");

        // Toggle Cat 2
        buttons.ElementAt(1).Click();
        updated.Should().Contain(2);
    }

    [Fact]
    public void CategoriesPage_PresetColorSwatches_RenderAsButtonsWithAccessibleNames()
    {
        using var dbFactory = new TestDbContextFactory();
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        Services.AddSingleton(categoryService);

        var cut = RenderComponent<Categories>();

        var swatchButtons = cut.FindAll("button.category-color-swatch");
        swatchButtons.Should().NotBeEmpty();

        foreach (var btn in swatchButtons)
        {
            btn.GetAttribute("type").Should().Be("button");
            btn.GetAttribute("aria-label").Should().StartWith("Välj färg #");
            btn.GetAttribute("aria-pressed").Should().NotBeNull();
        }

        var colorInput = cut.Find("input[type='color']");
        colorInput.GetAttribute("aria-label").Should().Be("Välj anpassad färgkod");
    }

    [Fact]
    public void RecipeEdit_ImageUpload_HasAssociatedLabelAndFocusableInputFile()
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

        var label = cut.Find("label.image-upload-dropzone");
        label.GetAttribute("for").Should().Be("recipe-image-upload");

        var fileInput = cut.Find("input[type='file']#recipe-image-upload");
        fileInput.Should().NotBeNull();
        fileInput.ClassList.Should().Contain("image-upload-input");
        fileInput.GetAttribute("aria-label").Should().Be("Välj receptbild att ladda upp");
        fileInput.GetAttribute("style")?.Should().NotContain("display: none");
    }

    [Fact]
    public void ImportPlaceholder_ImageUpload_HasAssociatedLabelAndFocusableInputFile()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var parser = new SwedishIngredientParser();
        var importer = new WebRecipeImporter(
            new HttpClient(new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new JsonLdRecipeExtractor(parser),
            new HtmlFallbackExtractor(parser),
            imageStorage,
            NullLogger<WebRecipeImporter>.Instance);

        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<IRecipeImporter>(importer);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);

        var cut = RenderComponent<ImportPlaceholder>();

        cut.Find("#jsonld-toggle").Click();
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        cut.Find("#parse-jsonld-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var label = cut.Find("label.image-upload-dropzone");
            label.GetAttribute("for").Should().Be("import-image-upload");

            var fileInput = cut.Find("input[type='file']#import-image-upload");
            fileInput.Should().NotBeNull();
            fileInput.ClassList.Should().Contain("image-upload-input");
            fileInput.GetAttribute("aria-label").Should().Be("Välj receptbild att ladda upp");
            fileInput.GetAttribute("style")?.Should().NotContain("display: none");
        });
    }

    [Fact]
    public void WeeklyMenuDayCard_RemoveCategoryButton_HasAccessibleNameAndTargetClass()
    {
        var day = new WeeklyMenuDay
        {
            DayIndex = 0,
            SelectedCategories = new List<WeeklyMenuDayCategory>
            {
                new() { CategoryId = 5, Category = new Category { Id = 5, Name = "Snabbmat", ColorHex = "#e76f51" } }
            }
        };

        var cut = RenderComponent<WeeklyMenuDayCard>(parameters => parameters
            .Add(p => p.Day, day)
            .Add(p => p.AllCategories, new List<Category> { day.SelectedCategories.First().Category! }));

        var removeBtn = cut.Find("button.day-category-remove-btn");
        removeBtn.Should().NotBeNull();
        removeBtn.GetAttribute("aria-label").Should().Be("Ta bort Snabbmat");
        removeBtn.GetAttribute("title").Should().Be("Ta bort Snabbmat");
        removeBtn.GetAttribute("type").Should().Be("button");
    }

    [Fact]
    public async Task CookingMode_IngredientCheckboxes_HaveAssociatedLabelsWithIngredientNames()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var created = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Köttfärssås",
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients = new List<Ingredient>
            {
                new() { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "nötfärs", RawText = "500 g nötfärs" },
                new() { OrderIndex = 2, Quantity = 1m, Unit = "st", Name = "gul lök", RawText = "1 st gul lök", Preparation = "hackad" }
            },
            Instructions = new List<InstructionStep>
            {
                new() { StepNumber = 1, InstructionText = "Fräs löken." }
            }
        });

        Services.AddSingleton(recipeService);
        Services.AddSingleton(new ServingCalculator());

        var cut = RenderComponent<CookingMode>(parameters => parameters
            .Add(p => p.Id, created.Id));

        cut.WaitForAssertion(() =>
        {
            var drawerBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Visa ingredienser"));
            drawerBtn?.Click();
            cut.FindAll("input[type='checkbox'].ingredient-checkbox").Should().HaveCount(2);
        });

        var checkboxes = cut.FindAll("input[type='checkbox'].ingredient-checkbox");
        var firstId = checkboxes.ElementAt(0).GetAttribute("id");
        firstId.Should().Be("cooking-ing-0");

        var firstLabel = cut.Find($"label[for='{firstId}']");
        firstLabel.Should().NotBeNull();
        firstLabel.TextContent.Should().Contain("nötfärs");
        firstLabel.TextContent.Should().Contain("500 g");

        // Toggle via change
        checkboxes.ElementAt(0).Change(true);
        cut.Find("li.ingredient-item").ClassList.Should().Contain("checked");
    }

    [Fact]
    public async Task RecipeDetail_IngredientCheckboxes_HaveAssociatedLabelsWithIngredientNames()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var servingCalc = new ServingCalculator();
        var shoppingListService = new ShoppingListService(dbFactory, new ShoppingListAggregator(servingCalc), new SwedishIngredientParser());

        var created = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Pannkakor",
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients = new List<Ingredient>
            {
                new() { OrderIndex = 1, Quantity = 3m, Unit = "dl", Name = "vetemjöl", RawText = "3 dl vetemjöl" },
                new() { OrderIndex = 2, Quantity = 6m, Unit = "dl", Name = "mjölk", RawText = "6 dl mjölk" }
            },
            Instructions = new List<InstructionStep>
            {
                new() { StepNumber = 1, InstructionText = "Vispa smeten." }
            }
        });

        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(shoppingListService);
        Services.AddSingleton(servingCalc);

        var cut = RenderComponent<RecipeDetail>(parameters => parameters
            .Add(p => p.Id, created.Id));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("input[type='checkbox'].ingredient-checkbox").Should().HaveCount(2);
        });

        var checkboxes = cut.FindAll("input[type='checkbox'].ingredient-checkbox");
        var firstId = checkboxes.ElementAt(0).GetAttribute("id");
        firstId.Should().Be("recipe-ing-0");

        var firstLabel = cut.Find($"label[for='{firstId}']");
        firstLabel.Should().NotBeNull();
        firstLabel.TextContent.Should().Contain("vetemjöl");
        firstLabel.TextContent.Should().Contain("3 dl");

        // Toggle via change
        checkboxes.ElementAt(0).Change(true);
        cut.Find("li.ingredient-item").ClassList.Should().Contain("checked");
    }
}
