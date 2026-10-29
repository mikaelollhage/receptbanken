using System.Net;
using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class RecipeTextImportComponentTests : TestContext
{
    private (IRenderedComponent<ImportPlaceholder> Cut, RecipeService RecipeService) RenderImportPage(HttpMessageHandler? httpHandler = null)
    {
        var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var ingredientParser = new SwedishIngredientParser();
        var jsonLdExtractor = new JsonLdRecipeExtractor(ingredientParser);
        var fallbackExtractor = new HtmlFallbackExtractor(ingredientParser);
        var htmlTextExtractor = new HtmlRecipeTextExtractor();
        var textParser = new RecipeTextParser(ingredientParser);

        var importer = new WebRecipeImporter(
            new HttpClient(httpHandler ?? new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            jsonLdExtractor,
            fallbackExtractor,
            htmlTextExtractor,
            textParser,
            imageStorage,
            NullLogger<WebRecipeImporter>.Instance);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<IRecipeImporter>(importer);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (RenderComponent<ImportPlaceholder>(), recipeService);
    }

    [Fact]
    public void RecipeTextSection_ShouldBeCollapsedByDefault_AndAllowManualToggle()
    {
        var (cut, _) = RenderImportPage();

        // Initially collapsed
        cut.FindAll("#recipe-text-panel").Should().BeEmpty();
        cut.Find("#recipe-text-toggle").GetAttribute("aria-expanded").Should().Be("false");

        // Click to expand
        cut.Find("#recipe-text-toggle").Click();
        cut.FindAll("#recipe-text-panel").Should().ContainSingle();
        cut.Find("#recipe-text-toggle").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("#parse-recipe-text-btn").HasAttribute("disabled").Should().BeTrue();

        // Pasting text enables "Tolka recepttext" button immediately
        cut.Find("#recipe-text-input").Input(NiiinisRecipeFixtures.PlainText);
        cut.Find("#parse-recipe-text-btn").HasAttribute("disabled").Should().BeFalse();

        // Click to collapse
        cut.Find("#recipe-text-toggle").Click();
        cut.FindAll("#recipe-text-panel").Should().BeEmpty();
    }

    [Fact]
    public void TogglingRecipeTextSection_ShouldCollapseJsonLdSection_ToAvoidMultipleLargeForms()
    {
        var (cut, _) = RenderImportPage();

        // Open JSON-LD
        cut.Find("#jsonld-toggle").Click();
        cut.FindAll("#jsonld-panel").Should().ContainSingle();
        cut.FindAll("#recipe-text-panel").Should().BeEmpty();

        // Open Recipe Text -> JSON-LD automatically collapses
        cut.Find("#recipe-text-toggle").Click();
        cut.FindAll("#recipe-text-panel").Should().ContainSingle();
        cut.FindAll("#jsonld-panel").Should().BeEmpty();

        // Open JSON-LD again -> Recipe Text automatically collapses
        cut.Find("#jsonld-toggle").Click();
        cut.FindAll("#jsonld-panel").Should().ContainSingle();
        cut.FindAll("#recipe-text-panel").Should().BeEmpty();
    }

    [Fact]
    public void ParseRecipeText_WithNiiinisPlainText_ShouldRenderSharedPreview_WithAllFields()
    {
        var (cut, _) = RenderImportPage();

        // Open Recipe Text section
        cut.Find("#recipe-text-toggle").Click();
        cut.Find("#recipe-text-source-url").Input(NiiinisRecipeFixtures.PageUrl);
        cut.Find("#recipe-text-input").Input(NiiinisRecipeFixtures.PlainText);

        // Click Tolka recepttext
        cut.Find("#parse-recipe-text-btn").Click();

        // Verify shared preview is populated
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#recipe-text-panel").Should().BeEmpty();
            cut.Markup.Should().Contain("Förhandsgranskning — Väntar på ditt godkännande");
            cut.Markup.Should().Contain(NiiinisRecipeFixtures.RecipeTitle);
            cut.Markup.Should().Contain($"Tolkade ingredienser ({NiiinisRecipeFixtures.IngredientCount})");
            cut.Markup.Should().Contain($"Tolkade instruktionssteg ({NiiinisRecipeFixtures.StepCount})");
            cut.FindAll(".ingredient-editor-row").Should().HaveCount(NiiinisRecipeFixtures.IngredientCount);
            cut.FindAll(".instruction-editor-row").Should().HaveCount(NiiinisRecipeFixtures.StepCount);
        });
    }

    [Fact]
    public async Task ParseRecipeText_ThenApproveAndSave_ShouldPersistRecipeInDatabase()
    {
        var (cut, recipeService) = RenderImportPage();

        cut.Find("#recipe-text-toggle").Click();
        cut.Find("#recipe-text-source-url").Input(NiiinisRecipeFixtures.PageUrl);
        cut.Find("#recipe-text-input").Input(NiiinisRecipeFixtures.PlainText);
        cut.Find("#parse-recipe-text-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Godkänn & Spara recept"));
        });

        // Click "✓ Godkänn & Spara recept"
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Godkänn & Spara recept"));
        saveBtn.Click();

        // Verify saved recipe in DB
        var savedRecipes = await recipeService.GetRecipesAsync();
        var saved = savedRecipes.FirstOrDefault(r => r.Title == NiiinisRecipeFixtures.RecipeTitle);
        saved.Should().NotBeNull();
        saved!.Servings.Should().Be(NiiinisRecipeFixtures.Servings);
        saved.SourceUrl.Should().Be(NiiinisRecipeFixtures.PageUrl);

        var fullRecipe = await recipeService.GetRecipeByIdAsync(saved.Id);
        fullRecipe.Should().NotBeNull();
        fullRecipe!.Ingredients.Should().HaveCount(NiiinisRecipeFixtures.IngredientCount);
        fullRecipe.Instructions.Should().HaveCount(NiiinisRecipeFixtures.StepCount);
    }

    [Fact]
    public void UrlImportFailure_ShouldOfferTextImportButton_WhichExpandsAndPreservesUrl()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(IcaRecipeFixtures.Akamai403Body, Encoding.UTF8, "text/html")
        });
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input(NiiinisRecipeFixtures.PageUrl);
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".import-error-box").Should().ContainSingle();
            var textImportBtn = cut.Find("#btn-open-textimport");
            textImportBtn.Should().NotBeNull();
            textImportBtn.Click();

            cut.FindAll("#recipe-text-panel").Should().ContainSingle();
            cut.Find("#recipe-text-source-url").GetAttribute("value").Should().Be(NiiinisRecipeFixtures.PageUrl);
        });
    }

    [Fact]
    public void ParseRecipeText_WithInvalidNonRecipeText_ShouldShowSwedishErrorMessage()
    {
        var (cut, _) = RenderImportPage();

        cut.Find("#recipe-text-toggle").Click();
        cut.Find("#recipe-text-input").Input("Detta är bara en slumpmässig text utan ingredienser eller instruktioner.");
        cut.Find("#parse-recipe-text-btn").Click();

        cut.FindAll(".recipe-text-error").Should().ContainSingle();
        cut.Find(".recipe-text-error").TextContent.Should().Contain("Inget recept kunde identifieras");
    }
}
