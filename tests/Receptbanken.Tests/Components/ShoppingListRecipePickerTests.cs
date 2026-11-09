using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;

namespace Receptbanken.Tests.Components;

public class ShoppingListRecipePickerTests : TestContext
{
    private const string PickerModulePath = "./js/recipe-picker.js";
    private readonly TestDbContextFactory _dbFactory;
    private readonly ShoppingListNotifier _sharedNotifier;
    private readonly RecipeService _recipeService;
    private readonly CategoryService _categoryService;
    private readonly ShoppingListService _shoppingService;

    public ShoppingListRecipePickerTests()
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
        var pickerModule = JSInterop.SetupModule(PickerModulePath);
        pickerModule.Mode = JSRuntimeMode.Loose;
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
        int idx = 0;
        foreach (var (name, rawText) in ingredients)
        {
            var parsed = parser.Parse(rawText);
            parsed.OrderIndex = idx++;
            recipe.Ingredients.Add(parsed);
        }

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe;
    }

    [Fact]
    public async Task OpenPicker_ClickToggle_ShouldDisplayRecipePickerModal()
    {
        await CreateRecipeAsync("Pasta Bolognese", 4, ("nötfärs", "500 g nötfärs"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.FindAll(".recipe-picker").Should().BeEmpty("modalen ska inte synas från start");

        // Klicka på "Lägg till från recept"
        cut.Find("#shopping-recipe-toggle-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".recipe-picker").Should().ContainSingle();
            cut.Find(".recipe-picker-title").TextContent.Should().Contain("Välj recept att handla för");
            cut.FindAll(".recipe-picker-card").Should().ContainSingle();
            cut.Find(".recipe-picker-card-title").TextContent.Should().Contain("Pasta Bolognese");
        });
    }

    [Fact]
    public async Task CancelInModal_ShouldCloseModalWithoutSelectingOrAddingIngredients()
    {
        await CreateRecipeAsync("Pasta Bolognese", 4, ("nötfärs", "500 g nötfärs"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.Find("#shopping-recipe-toggle-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-picker").Should().ContainSingle());

        // Klicka på Avbryt inuti modalen
        cut.Find(".recipe-picker-cancel").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".recipe-picker").Should().BeEmpty();
            cut.FindAll(".shopping-recipe-selected-panel").Should().BeEmpty("inget recept ska vara valt");
            cut.FindAll(".shopping-item-row").Should().BeEmpty("inga ingredienser ska ha lagts till");
        });
    }

    [Fact]
    public async Task SelectRecipeInModal_ShouldCloseModalAndShowCompactConfirmationSection()
    {
        var recipe = await CreateRecipeAsync("Krämig Laxpasta", 2, ("laxfilé", "300 g laxfilé"), ("pasta", "250 g pasta"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.Find("#shopping-recipe-toggle-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-picker-card").Should().ContainSingle());

        // Välj receptkortet
        cut.Find(".recipe-picker-card").Click();

        cut.WaitForAssertion(() =>
        {
            // Modalen ska stängas
            cut.FindAll(".recipe-picker").Should().BeEmpty();

            // Kompakt bekräftelsesektion ska visas
            cut.FindAll(".shopping-recipe-selected-panel").Should().ContainSingle();
            cut.Find(".shopping-selected-recipe-title").TextContent.Should().Contain("Krämig Laxpasta");

            // Portionsantal ska vara förifyllt med receptets Servings (2)
            var servingsInput = cut.Find("#shopping-recipe-servings");
            servingsInput.GetAttribute("value").Should().Be("2");

            // Inga ingredienser ska ha lagts till i inköpslistan än
            cut.FindAll(".shopping-item-row").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ConfirmSelectedRecipe_ShouldAddScaledIngredientsToShoppingList()
    {
        await CreateRecipeAsync("Krämig Laxpasta", 2, ("laxfilé", "300 g laxfilé"), ("pasta", "200 g pasta"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.Find("#shopping-recipe-toggle-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-picker-card").Should().ContainSingle());
        cut.Find(".recipe-picker-card").Click();

        cut.WaitForAssertion(() => cut.FindAll(".shopping-recipe-selected-panel").Should().ContainSingle());

        // Ändra portioner från 2 till 4
        cut.Find("#shopping-recipe-servings").Change("4");

        // Klicka på "Lägg till ingredienser" -> öppnar förhandsgranskning
        cut.Find("#shopping-add-recipe-btn").Click();

        // Förhandsgranskningen ska visas med skalade ingredienser (600 g laxfilé, 400 g pasta)
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-shopping-preview-dialog").Should().ContainSingle();
            var previewText = cut.Find(".weekly-shopping-preview-dialog").TextContent;
            previewText.Should().Contain("600 g laxfilé");
            previewText.Should().Contain("400 g pasta");
        });

        // Bekräfta tillägg från förhandsgranskningen
        cut.Find(".weekly-preview-confirm-btn").Click();

        cut.WaitForAssertion(() =>
        {
            // Panel och preview ska stängas vid lyckat tillägg
            cut.FindAll(".shopping-recipe-selected-panel").Should().BeEmpty();
            cut.FindAll(".weekly-shopping-preview-dialog").Should().BeEmpty();

            // Ingredienser ska finnas i listan, skalade till 4 portioner (dubbelt upp mot 2 portioner)
            var items = cut.FindAll(".shopping-unchecked-list .shopping-item-row");
            items.Should().HaveCount(2);

            var textContent = cut.Find(".shopping-unchecked-list").TextContent;
            textContent.Should().Contain("600 g laxfilé");
            textContent.Should().Contain("400 g pasta");

            // Tydlig feedback visas
            cut.Find(".shopping-recipe-feedback").TextContent.Should().Contain("2 varor lades till i inköpslistan från Krämig Laxpasta");
        });
    }

    [Fact]
    public async Task CancelInCompactSection_ShouldClearSelectionWithoutAddingIngredients()
    {
        await CreateRecipeAsync("Pannkakor", 4, ("ägg", "3 st ägg"), ("mjölk", "6 dl mjölk"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.Find("#shopping-recipe-toggle-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-picker-card").Should().ContainSingle());
        cut.Find(".recipe-picker-card").Click();

        cut.WaitForAssertion(() => cut.FindAll(".shopping-recipe-selected-panel").Should().ContainSingle());

        // Klicka på Avbryt i den kompakta sektionen
        cut.Find("#shopping-cancel-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".shopping-recipe-selected-panel").Should().BeEmpty();
            cut.FindAll(".shopping-item-row").Should().BeEmpty("ingenting ska ha sparats i inköpslistan");
        });
    }

    [Fact]
    public async Task ChangeRecipeButton_ShouldReopenModalAndAllowSelectingDifferentRecipe()
    {
        await CreateRecipeAsync("Recept Ett", 2, ("salt", "1 tsk salt"));
        await CreateRecipeAsync("Recept Två", 4, ("peppar", "1 krm peppar"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        // Välj recept ett
        cut.Find("#shopping-recipe-toggle-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-picker-card").Should().HaveCount(2));
        cut.FindAll(".recipe-picker-card").First().Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".shopping-selected-recipe-title").TextContent.Should().Contain("Recept Ett");
            cut.Find("#shopping-recipe-servings").GetAttribute("value").Should().Be("2");
        });

        // Klicka på "Byt recept…"
        cut.Find("#shopping-change-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".recipe-picker").Should().ContainSingle();
        });

        // Välj det andra receptet
        cut.FindAll(".recipe-picker-card").Last().Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".recipe-picker").Should().BeEmpty();
            cut.Find(".shopping-selected-recipe-title").TextContent.Should().Contain("Recept Två");
            cut.Find("#shopping-recipe-servings").GetAttribute("value").Should().Be("4");
        });
    }

    [Fact]
    public async Task InvalidServings_ShouldShowValidationErrorAndNotAddIngredients()
    {
        await CreateRecipeAsync("Pasta Pesto", 4, ("pesto", "1 burk pesto"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.Find("#shopping-recipe-toggle-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-picker-card").Should().ContainSingle());
        cut.Find(".recipe-picker-card").Click();

        cut.WaitForAssertion(() => cut.FindAll(".shopping-recipe-selected-panel").Should().ContainSingle());

        // Ange 0 portioner
        cut.Find("#shopping-recipe-servings").Change("0");
        cut.Find("#shopping-add-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".shopping-recipe-feedback").TextContent.Should().Contain("Ange ett giltigt antal portioner (1–100)");
            cut.FindAll(".shopping-item-row").Should().BeEmpty("inga varor ska ha lagts till med ogiltiga portioner");
            cut.FindAll(".shopping-recipe-selected-panel").Should().ContainSingle("sektionen ska förbli öppen");
        });
    }

    [Fact]
    public async Task RapidRepeatedClicks_ShouldNotRegisterDuplicates()
    {
        await CreateRecipeAsync("Pannkakor", 4, ("ägg", "3 st ägg"));

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.Find("#shopping-recipe-toggle-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-picker-card").Should().ContainSingle());
        cut.Find(".recipe-picker-card").Click();

        cut.WaitForAssertion(() => cut.FindAll(".shopping-recipe-selected-panel").Should().ContainSingle());

        // Klicka på "Lägg till ingredienser" för att öppna förhandsgranskningen
        cut.Find("#shopping-add-recipe-btn").Click();

        cut.WaitForAssertion(() => cut.FindAll(".weekly-shopping-preview-dialog").Should().ContainSingle());

        // Klicka två gånger i snabb följd på bekräfta
        var confirmBtn = cut.Find(".weekly-preview-confirm-btn");
        confirmBtn.Click();
        if (cut.FindAll(".weekly-preview-confirm-btn").Count > 0)
        {
            try { cut.Find(".weekly-preview-confirm-btn").Click(); } catch { /* Ignore if already disabled/unmounted */ }
        }

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".shopping-unchecked-list .shopping-item-row");
            rows.Should().ContainSingle("endast ett tillägg ska registreras trots upprepade klick");
        });
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
