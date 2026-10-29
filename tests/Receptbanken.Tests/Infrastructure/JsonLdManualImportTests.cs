using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Tests.Infrastructure;

public class JsonLdManualImportTests
{
    private static WebRecipeImporter CreateImporter(TestHttpMessageHandler? handler = null)
    {
        var parser = new SwedishIngredientParser();
        return new WebRecipeImporter(
            new HttpClient(handler ?? new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new JsonLdRecipeExtractor(parser),
            new HtmlFallbackExtractor(parser),
            new MockImageStorageService(),
            NullLogger<WebRecipeImporter>.Instance);
    }

    // ---------- Successful imports ----------

    [Fact]
    public void ParseFromJsonLd_SingleRecipeObject_ShouldExtractRecipe()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Pannkakor",
              "recipeYield": "4 portioner",
              "prepTime": "PT10M",
              "cookTime": "PT20M",
              "recipeIngredient": ["2,5 dl vetemjöl", "6 dl mjölk", "3 ägg"],
              "recipeInstructions": ["Blanda mjöl och mjölk.", "Vispa i äggen och grädda."]
            }
            """;

        var result = CreateImporter().ParseFromJsonLd(json);

        result.Success.Should().BeTrue();
        result.FailureReason.Should().Be(RecipeImportFailureReason.None);
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Pannkakor");
        recipe.PrepTimeMinutes.Should().Be(10);
        recipe.CookTimeMinutes.Should().Be(20);
        recipe.Ingredients.Should().HaveCount(3);
        recipe.Ingredients[0].Quantity.Should().Be(2.5m);
        recipe.Ingredients[0].Unit.Should().Be("dl");
        recipe.Ingredients[0].Name.Should().Be("vetemjöl");
        recipe.Instructions.Select(s => s.StepNumber).Should().Equal(1, 2);
        recipe.SourceUrl.Should().BeNull();
        recipe.SourceHost.Should().BeNull();
    }

    [Fact]
    public void ParseFromJsonLd_IcaMarryMeChickenConsoleArray_ShouldExtractAllIngredientsAndSteps_WithoutHttpRequests()
    {
        var handler = new TestHttpMessageHandler(_ => throw new InvalidOperationException("No HTTP requests allowed"));
        var importer = CreateImporter(handler);

        var result = importer.ParseFromJsonLd(IcaRecipeFixtures.ConsoleArrayJson, fallbackSourceUrl: null);

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Marry me chicken");
        recipe.Description.Should().StartWith("Krämig kyckling");
        recipe.Servings.Should().Be(4);
        recipe.ServingsUnit.Should().Be("portioner");
        recipe.CookTimeMinutes.Should().Be(45);
        recipe.Ingredients.Should().HaveCount(IcaRecipeFixtures.IngredientCount);
        recipe.Instructions.Should().HaveCount(IcaRecipeFixtures.StepCount);

        recipe.Ingredients[0].Quantity.Should().Be(600m);
        recipe.Ingredients[0].Unit.Should().Be("g");
        recipe.Ingredients[0].Name.Should().Be("kycklinglårfilé");
        recipe.Ingredients[9].Quantity.Should().Be(0.5m);
        recipe.Ingredients[9].Unit.Should().Be("tsk");
        recipe.Ingredients[9].Name.Should().Be("chiliflakes");

        recipe.SourceUrl.Should().Be(IcaRecipeFixtures.PageUrl);
        recipe.SourceHost.Should().Be("ica.se");
        result.RemoteImageUrl.Should().Be(IcaRecipeFixtures.ImageUrl);

        // Pasted JSON-LD must never trigger HTTP requests (not even for the image)
        recipe.ImagePath.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public void ParseFromJsonLd_RecipeInsideGraph_ShouldExtractRecipe()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@graph": [
                { "@type": "WebPage", "name": "Kladdkaka" },
                { "@type": "Organization", "name": "Exempel AB" },
                { "@type": "Recipe", "name": "Kladdkaka", "recipeIngredient": ["100 g smör"], "recipeInstructions": "Smält smöret." }
              ]
            }
            """;

        var result = CreateImporter().ParseFromJsonLd(json);

        result.Success.Should().BeTrue();
        result.DraftRecipe!.Title.Should().Be("Kladdkaka");
        result.DraftRecipe.Instructions.Should().ContainSingle();
    }

    [Fact]
    public void ParseFromJsonLd_TypeAsArray_ShouldExtractRecipe()
    {
        const string json = """
            { "@type": ["NewsArticle", "Recipe"], "name": "Kanelbullar", "recipeIngredient": ["50 g jäst"] }
            """;

        var result = CreateImporter().ParseFromJsonLd(json);

        result.Success.Should().BeTrue();
        result.DraftRecipe!.Title.Should().Be("Kanelbullar");
    }

    [Fact]
    public void ParseFromJsonLd_MultipleRecipes_ShouldDeterministicallySelectFirstNamedRecipeInDocumentOrder()
    {
        const string json = """
            [
              { "@type": "BreadcrumbList", "itemListElement": [] },
              { "@type": "Recipe", "recipeIngredient": ["1 st okänd"] },
              { "@type": "Recipe", "name": "Första namngivna receptet" },
              { "@type": "Recipe", "name": "Andra receptet" }
            ]
            """;

        var importer = CreateImporter();
        var first = importer.ParseFromJsonLd(json);
        var second = importer.ParseFromJsonLd(json);

        first.Success.Should().BeTrue();
        first.DraftRecipe!.Title.Should().Be("Första namngivna receptet");
        second.DraftRecipe!.Title.Should().Be(first.DraftRecipe.Title);
    }

    [Theory]
    [InlineData("\"4 portioner\"", 4, "portioner")]
    [InlineData("6", 6, "portioner")]
    [InlineData("[\"8 bitar\"]", 8, "bitar")]
    [InlineData("[12, \"12 st\"]", 12, "portioner")]
    public void ParseFromJsonLd_RecipeYieldVariants_ShouldBeParsed(string yieldJson, int expectedServings, string expectedUnit)
    {
        var json = $$"""{ "@type": "Recipe", "name": "Test", "recipeYield": {{yieldJson}} }""";

        var result = CreateImporter().ParseFromJsonLd(json);

        result.Success.Should().BeTrue();
        result.DraftRecipe!.Servings.Should().Be(expectedServings);
        result.DraftRecipe.ServingsUnit.Should().Be(expectedUnit);
    }

    [Theory]
    [InlineData("\"totalTime\": \"PT45M\"", null, 45)]
    [InlineData("\"prepTime\": \"PT15M\", \"cookTime\": \"PT1H\"", 15, 60)]
    [InlineData("\"cookTime\": \"PT1H30M\"", null, 90)]
    public void ParseFromJsonLd_Iso8601Durations_ShouldBeParsed(string timeProps, int? expectedPrep, int? expectedCook)
    {
        var json = $$"""{ "@type": "Recipe", "name": "Test", {{timeProps}} }""";

        var result = CreateImporter().ParseFromJsonLd(json);

        result.Success.Should().BeTrue();
        result.DraftRecipe!.PrepTimeMinutes.Should().Be(expectedPrep);
        result.DraftRecipe.CookTimeMinutes.Should().Be(expectedCook);
    }

    [Fact]
    public void ParseFromJsonLd_ShouldProduceSameResultAsEquivalentHtmlImport()
    {
        var importer = CreateImporter();

        var fromHtml = importer.ParseFromHtml(IcaRecipeFixtures.Html, IcaRecipeFixtures.PageUrl);
        var fromJsonLd = importer.ParseFromJsonLd(IcaRecipeFixtures.RecipeJson);

        fromHtml.Success.Should().BeTrue();
        fromJsonLd.Success.Should().BeTrue();
        fromJsonLd.FailureReason.Should().Be(fromHtml.FailureReason);
        fromJsonLd.RemoteImageUrl.Should().Be(fromHtml.RemoteImageUrl);
        fromJsonLd.DraftRecipe.Should().BeEquivalentTo(
            fromHtml.DraftRecipe,
            options => options
                .Excluding(r => r!.CreatedAt)
                .Excluding(r => r!.UpdatedAt));
    }

    // ---------- Source URL rule ----------

    [Fact]
    public void ParseFromJsonLd_JsonLdUrlTakesPrecedenceOverPreviouslyEnteredUrl()
    {
        var result = CreateImporter().ParseFromJsonLd(IcaRecipeFixtures.RecipeJson, "https://www.example.se/annan-sida");

        result.DraftRecipe!.SourceUrl.Should().Be(IcaRecipeFixtures.PageUrl);
    }

    [Fact]
    public void ParseFromJsonLd_WithoutJsonLdUrl_ShouldFallBackToPreviouslyEnteredUrl()
    {
        const string json = """{ "@type": "Recipe", "name": "Utan url" }""";

        var result = CreateImporter().ParseFromJsonLd(json, "  https://www.ica.se/recept/marry-me-chicken-730140/  ");

        result.DraftRecipe!.SourceUrl.Should().Be(IcaRecipeFixtures.PageUrl);
        result.DraftRecipe.SourceHost.Should().Be("ica.se");
    }

    [Theory]
    [InlineData("javascript:alert(1)", "https://www.koket.se/recept/x", "https://www.koket.se/recept/x")]
    [InlineData("ftp://example.com/recipe", null, null)]
    [InlineData("http://localhost/recept", null, null)]
    [InlineData("inte en url", "inte heller en url", null)]
    [InlineData(null, "http://192.168.1.10/recept", null)]
    public void ParseFromJsonLd_InvalidSourceUrls_ShouldBeRejected(string? jsonLdUrl, string? fallbackUrl, string? expectedSource)
    {
        var urlProp = jsonLdUrl == null ? string.Empty : $", \"url\": \"{jsonLdUrl}\"";
        var json = $$"""{ "@type": "Recipe", "name": "Källtest"{{urlProp}} }""";

        var result = CreateImporter().ParseFromJsonLd(json, fallbackUrl);

        result.Success.Should().BeTrue();
        result.DraftRecipe!.SourceUrl.Should().Be(expectedSource);
    }

    // ---------- Error handling ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    public void ParseFromJsonLd_EmptyOrWhitespace_ShouldReturnEmptyInput(string? input)
    {
        var result = CreateImporter().ParseFromJsonLd(input);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.EmptyInput);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("{ \"@type\": \"Recipe\", \"name\": ")]
    [InlineData("<script type=\"application/ld+json\">{}</script>")]
    [InlineData("detta är inte json")]
    public void ParseFromJsonLd_InvalidJson_ShouldReturnInvalidJson(string input)
    {
        var result = CreateImporter().ParseFromJsonLd(input);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.InvalidJson);
        result.ErrorMessage.Should().Contain("giltig JSON");
    }

    [Theory]
    [InlineData("""[{ "@type": "Organization", "name": "ICA" }, { "@type": "WebSite", "name": "ICA.se" }]""")]
    [InlineData("""{ "@type": "BreadcrumbList", "itemListElement": [] }""")]
    [InlineData("""{ "@type": "Recipe", "name": 42 }""")]
    [InlineData("\"bara en sträng\"")]
    [InlineData("123")]
    [InlineData("[]")]
    public void ParseFromJsonLd_ValidJsonWithoutUsableRecipe_ShouldReturnNoRecipeData(string input)
    {
        var result = CreateImporter().ParseFromJsonLd(input);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
        result.ErrorMessage.Should().Contain("Inget recept hittades");
    }

    [Fact]
    public void ParseFromJsonLd_UnexpectedDataTypes_ShouldNotThrowAndIgnoreInvalidFields()
    {
        const string json = """
            {
              "@type": "Recipe",
              "name": "Konstiga typer",
              "description": { "text": "objekt i stället för sträng" },
              "recipeIngredient": 123,
              "recipeInstructions": { "foo": true },
              "recipeYield": true,
              "totalTime": 45,
              "image": [null, 5, { "url": 7 }],
              "url": ["https://www.ica.se/"]
            }
            """;

        var act = () => CreateImporter().ParseFromJsonLd(json);

        var result = act.Should().NotThrow().Subject;
        result.Success.Should().BeTrue();
        result.DraftRecipe!.Title.Should().Be("Konstiga typer");
        result.DraftRecipe.Description.Should().BeNull();
        result.DraftRecipe.Ingredients.Should().BeEmpty();
        result.DraftRecipe.Instructions.Should().BeEmpty();
        result.DraftRecipe.Servings.Should().Be(4);
        result.DraftRecipe.CookTimeMinutes.Should().BeNull();
        result.DraftRecipe.SourceUrl.Should().BeNull();
        result.RemoteImageUrl.Should().BeNull();
    }

    [Fact]
    public void ParseFromJsonLd_TooLargeDocument_ShouldReturnInputTooLarge()
    {
        var padding = new string(' ', RecipeImportLimits.MaxJsonLdLength);
        var json = $$"""{ "@type": "Recipe", "name": "Stor"{{padding}} }""";

        var result = CreateImporter().ParseFromJsonLd(json);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.InputTooLarge);
    }

    [Fact]
    public void ParseFromJsonLd_DeeplyNestedDocument_ShouldFailGracefully()
    {
        var json = new StringBuilder()
            .Append('[', 500)
            .Append("""{ "@type": "Recipe", "name": "Djupt" }""")
            .Append(']', 500)
            .ToString();

        var act = () => CreateImporter().ParseFromJsonLd(json);

        var result = act.Should().NotThrow().Subject;
        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.InvalidJson);
    }

    // ---------- Typed failure classification for URL import ----------

    [Theory]
    [InlineData(RecipeImportFailureReason.AccessBlocked, true)]
    [InlineData(RecipeImportFailureReason.RateLimited, true)]
    [InlineData(RecipeImportFailureReason.NoRecipeData, true)]
    [InlineData(RecipeImportFailureReason.NotFound, false)]
    [InlineData(RecipeImportFailureReason.InvalidUrl, false)]
    [InlineData(RecipeImportFailureReason.NetworkError, false)]
    [InlineData(RecipeImportFailureReason.Timeout, false)]
    [InlineData(RecipeImportFailureReason.HttpError, false)]
    [InlineData(RecipeImportFailureReason.SecurityRejected, false)]
    [InlineData(RecipeImportFailureReason.None, false)]
    public void SuggestsStructuredDataFallback_ShouldMatchSpecifiedTable(RecipeImportFailureReason reason, bool expected)
    {
        reason.SuggestsStructuredDataFallback().Should().Be(expected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, RecipeImportFailureReason.AccessBlocked)]
    [InlineData(HttpStatusCode.Unauthorized, RecipeImportFailureReason.AccessBlocked)]
    [InlineData(HttpStatusCode.TooManyRequests, RecipeImportFailureReason.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, RecipeImportFailureReason.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, RecipeImportFailureReason.HttpError)]
    public async Task ImportFromUrlAsync_HttpStatusCodes_ShouldBeClassified(HttpStatusCode statusCode, RecipeImportFailureReason expected)
    {
        var importer = CreateImporter(new TestHttpMessageHandler(_ => new HttpResponseMessage(statusCode)));

        var result = await importer.ImportFromUrlAsync(IcaRecipeFixtures.PageUrl);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(expected);
    }

    [Fact]
    public async Task ImportFromUrlAsync_BlockPageWith200_ShouldBeClassifiedAsAccessBlocked()
    {
        var importer = CreateImporter(new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(IcaRecipeFixtures.Akamai403Body, Encoding.UTF8, "text/html")
        }));

        var result = await importer.ImportFromUrlAsync(IcaRecipeFixtures.PageUrl);

        result.FailureReason.Should().Be(RecipeImportFailureReason.AccessBlocked);
    }

    [Fact]
    public async Task ImportFromUrlAsync_HtmlWithoutRecipe_ShouldBeClassifiedAsNoRecipeData()
    {
        var importer = CreateImporter(new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><head><title>Start</title></head><body><p>Ingen mat här</p></body></html>", Encoding.UTF8, "text/html")
        }));

        var result = await importer.ImportFromUrlAsync("https://www.example.se/");

        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
    }

    [Theory]
    [InlineData("inte en url")]
    [InlineData("ftp://example.com/recept")]
    [InlineData("http://localhost/recept")]
    public async Task ImportFromUrlAsync_InvalidUrl_ShouldBeClassifiedAsInvalidUrl_WithoutHttpRequests(string url)
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var importer = CreateImporter(handler);

        var result = await importer.ImportFromUrlAsync(url);

        result.FailureReason.Should().Be(RecipeImportFailureReason.InvalidUrl);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ImportFromUrlAsync_NetworkFailure_ShouldBeClassifiedAsNetworkError()
    {
        var importer = CreateImporter(new TestHttpMessageHandler(_ => throw new HttpRequestException("Connection refused")));

        var result = await importer.ImportFromUrlAsync(IcaRecipeFixtures.PageUrl);

        result.FailureReason.Should().Be(RecipeImportFailureReason.NetworkError);
        result.FailureReason.IsTransient().Should().BeTrue();
    }

    [Fact]
    public async Task ImportFromUrlAsync_Timeout_ShouldBeClassifiedAsTimeout()
    {
        var importer = CreateImporter(new TestHttpMessageHandler(_ => throw new TaskCanceledException("Timeout")));

        var result = await importer.ImportFromUrlAsync(IcaRecipeFixtures.PageUrl);

        result.FailureReason.Should().Be(RecipeImportFailureReason.Timeout);
        result.FailureReason.IsTransient().Should().BeTrue();
    }
}

