using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Models;
using Receptbanken.Web.Components.Recipes;

namespace Receptbanken.Tests.Components;

public class RecipePickerModalTests : TestContext
{
    private const string ModulePath = "./js/recipe-picker.js";

    private readonly BunitJSModuleInterop pickerModule;

    public RecipePickerModalTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
        pickerModule = JSInterop.SetupModule(ModulePath);
        pickerModule.Mode = JSRuntimeMode.Loose;
    }

    // ---------- Hjälpmetoder ----------

    private static Category Cat(int id, string name, string? color = null) =>
        new() { Id = id, Name = name, ColorHex = color };

    private static Recipe MakeRecipe(
        int id,
        string title,
        string? description = null,
        string? imagePath = null,
        bool favorite = false,
        int? cookMinutes = null,
        int servings = 4,
        string[]? ingredients = null,
        params Category[] categories)
    {
        var recipe = new Recipe
        {
            Id = id,
            Title = title,
            Description = description,
            ImagePath = imagePath,
            IsFavorite = favorite,
            CookTimeMinutes = cookMinutes,
            Servings = servings,
            ServingsUnit = "portioner"
        };

        foreach (var name in ingredients ?? Array.Empty<string>())
        {
            recipe.Ingredients.Add(new Ingredient { RecipeId = id, Name = name, RawText = $"1 st {name}" });
        }

        foreach (var category in categories)
        {
            recipe.RecipeCategories.Add(new RecipeCategory
            {
                RecipeId = id,
                CategoryId = category.Id,
                Category = category
            });
        }

        return recipe;
    }

    private IRenderedComponent<RecipePickerModal> RenderPicker(
        IReadOnlyList<Recipe> recipes,
        IReadOnlyList<Category>? categories = null,
        bool visible = true,
        int? selectedId = null,
        bool allowClear = false,
        Action<Recipe>? onSelected = null,
        Action? onCleared = null,
        Action? onCancel = null)
    {
        return RenderComponent<RecipePickerModal>(p => p
            .Add(c => c.IsVisible, visible)
            .Add(c => c.Recipes, recipes)
            .Add(c => c.Categories, categories ?? Array.Empty<Category>())
            .Add(c => c.SelectedRecipeId, selectedId)
            .Add(c => c.AllowClear, allowClear)
            .Add(c => c.Title, "Välj middag")
            .Add(c => c.OnSelected, EventCallback.Factory.Create<Recipe>(this, r => onSelected?.Invoke(r)))
            .Add(c => c.OnCleared, EventCallback.Factory.Create(this, () => onCleared?.Invoke()))
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => onCancel?.Invoke())));
    }

    private static string[] CardTitles(IRenderedComponent<RecipePickerModal> cut) =>
        cut.FindAll(".recipe-picker-card-title").Select(e => e.TextContent.Trim()).ToArray();

    private static List<Recipe> ManyRecipes(int count) =>
        Enumerable.Range(1, count).Select(i => MakeRecipe(i, $"Recept {i:000}")).ToList();

    // ---------- Receptkort ----------

    [Fact]
    public void Card_ShouldRenderImageTitleAndMetadata()
    {
        var cat = Cat(1, "Fisk", "#00aaff");
        var recipe = MakeRecipe(7, "Laxsoppa", imagePath: "images/recipes/lax.jpg", favorite: true,
            cookMinutes: 30, servings: 6, categories: cat);

        var cut = RenderComponent<RecipePickerCard>(p => p.Add(c => c.Recipe, recipe));

        var img = cut.Find("img.recipe-picker-card-image");
        img.GetAttribute("src").Should().Be("/images/recipes/lax.jpg");
        img.GetAttribute("loading").Should().Be("lazy");
        cut.Find(".recipe-picker-card-title").TextContent.Trim().Should().Be("Laxsoppa");
        cut.Find(".recipe-picker-card-meta").TextContent.Should().Contain("30 min").And.Contain("6 portioner");
        cut.Find(".recipe-picker-card-cats").TextContent.Should().Contain("Fisk");
        cut.FindAll(".recipe-picker-card-fav").Should().ContainSingle();
        cut.Find("button").GetAttribute("aria-label").Should().Contain("Laxsoppa").And.Contain("favorit");
    }

    [Fact]
    public void Card_WithoutImage_ShouldRenderPlaceholder()
    {
        var recipe = MakeRecipe(1, "Pannkakor");

        var cut = RenderComponent<RecipePickerCard>(p => p.Add(c => c.Recipe, recipe));

        cut.FindAll("img").Should().BeEmpty();
        cut.FindAll(".recipe-picker-card-placeholder").Should().ContainSingle();
    }

    [Fact]
    public void Card_WhenImageFails_ShouldFallBackToPlaceholder()
    {
        var recipe = MakeRecipe(1, "Pannkakor", imagePath: "images/recipes/saknas.jpg");
        var cut = RenderComponent<RecipePickerCard>(p => p.Add(c => c.Recipe, recipe));

        cut.Find("img").TriggerEvent("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());

        cut.FindAll("img").Should().BeEmpty();
        cut.FindAll(".recipe-picker-card-placeholder").Should().ContainSingle();
    }

    [Fact]
    public void Card_Click_ShouldRaiseOnSelect_AndMarkSelectedState()
    {
        var recipe = MakeRecipe(3, "Tacos");
        Recipe? selected = null;
        var cut = RenderComponent<RecipePickerCard>(p => p
            .Add(c => c.Recipe, recipe)
            .Add(c => c.OnSelect, EventCallback.Factory.Create<Recipe>(this, r => selected = r)));

        cut.Find("button").GetAttribute("aria-current").Should().BeNull();
        cut.Find("button").Click();
        selected.Should().BeSameAs(recipe);

        cut.SetParametersAndRender(p => p.Add(c => c.IsSelected, true));
        cut.Find("button").GetAttribute("aria-current").Should().Be("true");
        cut.Find("button").ClassList.Should().Contain("is-selected");
    }

    // ---------- Rendering av modalen ----------

    [Fact]
    public void Modal_WhenNotVisible_ShouldRenderNoContent()
    {
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") }, visible: false);

        cut.FindAll(".recipe-picker").Should().BeEmpty();
        cut.FindAll(".recipe-picker-card").Should().BeEmpty();
    }

    [Fact]
    public void Modal_WhenVisible_ShouldRenderCardsSortedByTitle()
    {
        var recipes = new[] { MakeRecipe(1, "Pasta"), MakeRecipe(2, "Äppelpaj"), MakeRecipe(3, "Bullar") };

        var cut = RenderPicker(recipes);

        CardTitles(cut).Should().Equal("Bullar", "Pasta", "Äppelpaj");
        cut.Find(".recipe-picker-count").TextContent.Should().Contain("3 recept");
    }

    // ---------- Val, avbryt, tömning ----------

    [Fact]
    public void Modal_SelectRecipe_ShouldRaiseOnSelectedOnly()
    {
        var pasta = MakeRecipe(1, "Pasta");
        var soppa = MakeRecipe(2, "Soppa");
        Recipe? selected = null;
        var cleared = false;
        var cancelled = false;
        var cut = RenderPicker(new[] { pasta, soppa }, onSelected: r => selected = r,
            onCleared: () => cleared = true, onCancel: () => cancelled = true);

        cut.FindAll(".recipe-picker-card").ToList()[1].Click();

        selected.Should().BeSameAs(soppa);
        cleared.Should().BeFalse();
        cancelled.Should().BeFalse();
    }

    [Fact]
    public void Modal_CancelButton_ShouldRaiseOnCancelWithoutSelecting()
    {
        Recipe? selected = null;
        var cancelled = 0;
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") }, selectedId: 1, allowClear: true,
            onSelected: r => selected = r, onCancel: () => cancelled++);

        cut.Find(".recipe-picker-cancel").Click();

        cancelled.Should().Be(1);
        selected.Should().BeNull();
    }

    [Fact]
    public void Modal_CloseButton_ShouldRaiseOnCancel()
    {
        var cancelled = 0;
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") }, onCancel: () => cancelled++);

        cut.Find(".recipe-picker-close").Click();

        cancelled.Should().Be(1);
    }

    [Fact]
    public void Modal_AllowClearWithSelection_ShouldRaiseOnCleared()
    {
        var cleared = 0;
        Recipe? selected = null;
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") }, selectedId: 1, allowClear: true,
            onCleared: () => cleared++, onSelected: r => selected = r);

        cut.Find(".recipe-picker-clear").Click();

        cleared.Should().Be(1);
        selected.Should().BeNull();
    }

    [Fact]
    public void Modal_ClearText_ShouldBeConfigurable()
    {
        var cut = RenderComponent<RecipePickerModal>(p => p
            .Add(c => c.IsVisible, true)
            .Add(c => c.Recipes, new[] { MakeRecipe(1, "Pasta") })
            .Add(c => c.SelectedRecipeId, 1)
            .Add(c => c.AllowClear, true)
            .Add(c => c.ClearText, "Ingen middag"));

        cut.Find(".recipe-picker-clear").TextContent.Trim().Should().Be("Ingen middag");
    }

    [Fact]
    public void Modal_ClearButton_ShouldBeHiddenWhenNotAllowedOrNothingSelected()
    {
        var recipes = new[] { MakeRecipe(1, "Pasta") };

        RenderPicker(recipes, selectedId: 1, allowClear: false)
            .FindAll(".recipe-picker-clear").Should().BeEmpty();
        RenderPicker(recipes, selectedId: null, allowClear: true)
            .FindAll(".recipe-picker-clear").Should().BeEmpty();
    }

    // ---------- Sökning och filter ----------

    [Theory]
    [InlineData("lasagne", new[] { "Lasagne" })]                 // titel
    [InlineData("krämig", new[] { "Pasta Carbonara" })]          // beskrivning
    [InlineData("gräddfil", new[] { "Tacopaj" })]                // ingrediensnamn
    [InlineData("1 st lök", new[] { "Löksoppa" })]               // ingrediens-RawText
    [InlineData("vegetarisk", new[] { "Grönsakssoppa" })]        // kategori
    [InlineData("SOPPA", new[] { "Grönsakssoppa", "Löksoppa" })] // skiftlägesokänsligt
    [InlineData("soppa vegetarisk", new[] { "Grönsakssoppa" })]  // flera sökord = alla måste matcha
    public void Modal_Search_ShouldMatchTitleDescriptionIngredientsAndCategories(string query, string[] expected)
    {
        var veg = Cat(1, "Vegetarisk");
        var recipes = new[]
        {
            MakeRecipe(1, "Lasagne"),
            MakeRecipe(2, "Pasta Carbonara", description: "En krämig klassiker"),
            MakeRecipe(3, "Tacopaj", ingredients: new[] { "gräddfil" }),
            MakeRecipe(4, "Löksoppa", ingredients: new[] { "lök" }),
            MakeRecipe(5, "Grönsakssoppa", categories: veg)
        };
        var cut = RenderPicker(recipes, new[] { veg });

        cut.Find("input.recipe-picker-search").Input(query);

        CardTitles(cut).OrderBy(t => t, StringComparer.Ordinal).Should()
            .Equal(expected.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Modal_CategoryFilter_ShouldFilterAndToggleOff()
    {
        var fisk = Cat(1, "Fisk");
        var kott = Cat(2, "Kött");
        var oanvand = Cat(3, "Dessert");
        var recipes = new[]
        {
            MakeRecipe(1, "Laxsoppa", categories: fisk),
            MakeRecipe(2, "Biff", categories: kott),
            MakeRecipe(3, "Torsk", categories: fisk)
        };
        var cut = RenderPicker(recipes, new[] { fisk, kott, oanvand });

        // Kategorier utan recept visas inte; "Alla kategorier" + två kategorier.
        var chips = cut.FindAll(".recipe-picker-categories button").ToList();
        chips.Select(c => c.TextContent.Trim()).Should().Equal("Alla kategorier", "Fisk", "Kött");

        chips[1].Click();
        CardTitles(cut).Should().Equal("Laxsoppa", "Torsk");
        cut.FindAll(".recipe-picker-categories button").ToList()[1].GetAttribute("aria-pressed").Should().Be("true");

        cut.FindAll(".recipe-picker-categories button").ToList()[0].Click();
        CardTitles(cut).Should().HaveCount(3);
    }

    [Fact]
    public void Modal_CategoryAndSearch_ShouldCombine()
    {
        var fisk = Cat(1, "Fisk");
        var recipes = new[]
        {
            MakeRecipe(1, "Laxsoppa", categories: fisk),
            MakeRecipe(2, "Torsk", categories: fisk),
            MakeRecipe(3, "Köttsoppa")
        };
        var cut = RenderPicker(recipes, new[] { fisk });

        cut.FindAll(".recipe-picker-categories button").ToList()[1].Click();
        cut.Find("input.recipe-picker-search").Input("soppa");

        CardTitles(cut).Should().Equal("Laxsoppa");
    }

    [Fact]
    public void Modal_FavoritesFilter_ShouldShowOnlyFavorites()
    {
        var recipes = new[]
        {
            MakeRecipe(1, "Pasta", favorite: true),
            MakeRecipe(2, "Soppa"),
            MakeRecipe(3, "Paj", favorite: true)
        };
        var cut = RenderPicker(recipes);

        cut.Find(".recipe-picker-favorites").GetAttribute("aria-pressed").Should().Be("false");
        cut.Find(".recipe-picker-favorites").Click();

        CardTitles(cut).Should().Equal("Paj", "Pasta");
        cut.Find(".recipe-picker-favorites").GetAttribute("aria-pressed").Should().Be("true");

        cut.Find(".recipe-picker-favorites").Click();
        CardTitles(cut).Should().HaveCount(3);
    }

    // ---------- Tomt resultat ----------

    [Fact]
    public void Modal_NoMatches_ShouldShowEmptyStateAndResetFilters()
    {
        var recipes = new[] { MakeRecipe(1, "Pasta"), MakeRecipe(2, "Soppa") };
        var cut = RenderPicker(recipes);

        cut.Find("input.recipe-picker-search").Input("finnsinte");

        cut.FindAll(".recipe-picker-card").Should().BeEmpty();
        cut.Find(".recipe-picker-empty").TextContent.Should().Contain("Inga recept matchar din sökning");

        cut.Find(".recipe-picker-reset").Click();

        cut.FindAll(".recipe-picker-empty").Should().BeEmpty();
        CardTitles(cut).Should().Equal("Pasta", "Soppa");
        ((string?)cut.Find("input.recipe-picker-search").GetAttribute("value")).Should().BeNullOrEmpty();
    }

    [Fact]
    public void Modal_FavoritesFilterWithoutFavorites_ShouldShowEmptyState()
    {
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") });

        cut.Find(".recipe-picker-favorites").Click();

        cut.Find(".recipe-picker-empty").TextContent.Should().Contain("Inga recept matchar");
    }

    [Fact]
    public void Modal_WithoutAnyRecipes_ShouldExplainThereAreNone()
    {
        var cut = RenderPicker(Array.Empty<Recipe>());

        cut.Find(".recipe-picker-empty").TextContent.Should().Contain("inga sparade recept");
        cut.FindAll(".recipe-picker-reset").Should().BeEmpty();
    }

    // ---------- Begränsning och utökning ----------

    [Fact]
    public void Modal_ShouldShowAtMost60Initially_AndRevealMoreStepwise()
    {
        var cut = RenderPicker(ManyRecipes(130));

        cut.FindAll(".recipe-picker-card").Should().HaveCount(60);
        cut.Find(".recipe-picker-count").TextContent.Should().Contain("Visar 60 av 130 recept");
        cut.Find(".recipe-picker-more button").TextContent.Should().Contain("70 kvar");

        cut.Find(".recipe-picker-more button").Click();
        cut.FindAll(".recipe-picker-card").Should().HaveCount(120);

        cut.Find(".recipe-picker-more button").Click();
        cut.FindAll(".recipe-picker-card").Should().HaveCount(130);
        cut.FindAll(".recipe-picker-more").Should().BeEmpty();
    }

    [Fact]
    public void Modal_Filtering_ShouldSearchWholeCollection_NotJustVisibleItems()
    {
        var recipes = ManyRecipes(130);
        recipes.Add(MakeRecipe(999, "Zebrapaj"));
        var cut = RenderPicker(recipes);

        cut.FindAll(".recipe-picker-card").Should().HaveCount(60);
        cut.Find("input.recipe-picker-search").Input("zebra");

        CardTitles(cut).Should().Equal("Zebrapaj");
    }

    [Fact]
    public void Modal_ChangingFilter_ShouldResetVisibleCount()
    {
        var cut = RenderPicker(ManyRecipes(130));
        cut.Find(".recipe-picker-more button").Click();
        cut.FindAll(".recipe-picker-card").Should().HaveCount(120);

        cut.Find("input.recipe-picker-search").Input("recept");

        cut.FindAll(".recipe-picker-card").Should().HaveCount(60);
    }

    // ---------- Tillgänglighet ----------

    [Fact]
    public void Modal_ShouldUseModalFluentDialogLabelledByTitle()
    {
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") });

        var dialog = cut.Find("fluent-dialog");
        var heading = cut.Find("h2.recipe-picker-title");
        heading.TextContent.Should().Be("Välj middag");
        dialog.GetAttribute("aria-labelledby").Should().Be(heading.Id);
        dialog.HasAttribute("modal").Should().BeTrue();
        dialog.HasAttribute("hidden").Should().BeFalse();
    }

    [Fact]
    public void Modal_WhenNotVisible_ShouldHideDialog()
    {
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") }, visible: false);

        cut.Find("fluent-dialog").HasAttribute("hidden").Should().BeTrue();
    }

    [Fact]
    public void Modal_ControlsShouldHaveAccessibleNames()
    {
        var fisk = Cat(1, "Fisk");
        var cut = RenderPicker(new[] { MakeRecipe(1, "Laxsoppa", categories: fisk) }, new[] { fisk });

        cut.Find("input.recipe-picker-search").GetAttribute("aria-label").Should().Be("Sök recept");
        cut.Find(".recipe-picker-close").GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        cut.Find(".recipe-picker-categories").GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        cut.Find(".recipe-picker-card").TagName.Should().Be("BUTTON"); // nåbar och aktiverbar med tangentbord
        cut.Find(".recipe-picker-card").GetAttribute("aria-label").Should().Contain("Laxsoppa");
    }

    [Fact]
    public void Modal_CurrentSelection_ShouldBeMarkedOnCard()
    {
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta"), MakeRecipe(2, "Soppa") }, selectedId: 2);

        var cards = cut.FindAll(".recipe-picker-card").ToList();
        cards[0].GetAttribute("aria-current").Should().BeNull();
        cards[1].GetAttribute("aria-current").Should().Be("true");
    }

    [Fact]
    public void Modal_Escape_ShouldCancelExactlyOnce()
    {
        var cancelled = 0;
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") }, onCancel: () => cancelled++);

        cut.Find(".recipe-picker").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.Find(".recipe-picker").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "a" });

        cancelled.Should().Be(1);
    }

    [Fact]
    public async Task Modal_FluentDialogDismiss_ShouldRaiseOnCancel()
    {
        var cancelled = 0;
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") }, onCancel: () => cancelled++);

        // Escape och klick på overlay hanteras av fluent-dialog, som rapporterar HiddenChanged(true).
        await cut.InvokeAsync(() => cut.FindComponent<FluentDialog>().Instance.HiddenChanged.InvokeAsync(true));

        cancelled.Should().Be(1);
    }

    [Fact]
    public void Modal_OnOpen_ShouldCaptureOpenerAndFocusSearch()
    {
        var recipes = new[] { MakeRecipe(1, "Pasta") };
        var cut = RenderPicker(recipes, visible: false);
        pickerModule.VerifyNotInvoke("captureOpener");

        cut.SetParametersAndRender(p => p.Add(c => c.IsVisible, true));

        pickerModule.VerifyInvoke("captureOpener", 1);
        JSInterop.Invocations["Blazor._internal.domWrapper.focus"].Should().NotBeEmpty();
    }

    [Fact]
    public void Modal_OnClose_ShouldRestoreFocusToOpener()
    {
        var cut = RenderPicker(new[] { MakeRecipe(1, "Pasta") });
        pickerModule.VerifyNotInvoke("restoreOpener");

        cut.SetParametersAndRender(p => p.Add(c => c.IsVisible, false));

        pickerModule.VerifyInvoke("restoreOpener", 1);
    }

    [Fact]
    public void Modal_Reopen_ShouldResetSearchAndFilters()
    {
        var recipes = new[] { MakeRecipe(1, "Pasta", favorite: true), MakeRecipe(2, "Soppa") };
        var cut = RenderPicker(recipes);
        cut.Find("input.recipe-picker-search").Input("soppa");
        cut.Find(".recipe-picker-favorites").Click();

        cut.SetParametersAndRender(p => p.Add(c => c.IsVisible, false));
        cut.SetParametersAndRender(p => p.Add(c => c.IsVisible, true));

        CardTitles(cut).Should().Equal("Pasta", "Soppa");
        cut.Find(".recipe-picker-favorites").GetAttribute("aria-pressed").Should().Be("false");
    }
}
