using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Tests.Infrastructure;

public class WebRecipeImporterTests
{
    private WebRecipeImporter CreateImporter(HttpMessageHandler? handler = null)
    {
        var parser = new SwedishIngredientParser();
        var jsonLd = new JsonLdRecipeExtractor(parser);
        var fallback = new HtmlFallbackExtractor(parser);
        var httpClient = new HttpClient(handler ?? new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        return new WebRecipeImporter(
            httpClient,
            jsonLd,
            fallback,
            new MockImageStorageService(),
            NullLogger<WebRecipeImporter>.Instance);
    }

    [Fact]
    public void ParseFromHtml_IcaStyleJsonLdGraph_ShouldExtractCompleteSwedishRecipe()
    {
        const string html = """
            <!DOCTYPE html>
            <html lang="sv">
            <head>
                <title>Klassiska pannkakor | ICA</title>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@graph": [
                    {
                      "@type": "WebPage",
                      "name": "Klassiska pannkakor"
                    },
                    {
                      "@type": "Recipe",
                      "name": "Klassiska pannkakor",
                      "description": "Tunna och frasiga pannkakor för hela familjen.",
                      "prepTime": "PT10M",
                      "cookTime": "PT20M",
                      "recipeYield": "4 portioner",
                      "image": {
                        "@type": "ImageObject",
                        "url": "https://assets.icanet.se/pannkakor.jpg"
                      },
                      "recipeIngredient": [
                        "2 ½ dl vetemjöl",
                        "½ tsk salt",
                        "6 dl mjölk",
                        "3 ägg",
                        "3 msk smör, smält"
                      ],
                      "recipeInstructions": [
                        {
                          "@type": "HowToStep",
                          "text": "Blanda mjöl och salt i en bunke."
                        },
                        {
                          "@type": "HowToStep",
                          "text": "Vispa i hälften av mjölken och vispa till en slät smet."
                        },
                        {
                          "@type": "HowToStep",
                          "text": "Vispa i resten av mjölken och äggen. Grädda tunna pannkakor i smör."
                        }
                      ]
                    }
                  ]
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        var importer = CreateImporter();
        var result = importer.ParseFromHtml(html, "https://www.ica.se/recept/pannkakor-713191/");

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Klassiska pannkakor");
        recipe.Description.Should().Be("Tunna och frasiga pannkakor för hela familjen.");
        recipe.PrepTimeMinutes.Should().Be(10);
        recipe.CookTimeMinutes.Should().Be(20);
        recipe.Servings.Should().Be(4);
        recipe.SourceHost.Should().Be("ica.se");
        result.RemoteImageUrl.Should().Be("https://assets.icanet.se/pannkakor.jpg");

        recipe.Ingredients.Should().HaveCount(5);
        recipe.Ingredients[0].Quantity.Should().Be(2.5m);
        recipe.Ingredients[0].Unit.Should().Be("dl");
        recipe.Ingredients[0].Name.Should().Be("vetemjöl");

        recipe.Ingredients[4].Quantity.Should().Be(3m);
        recipe.Ingredients[4].Unit.Should().Be("msk");
        recipe.Ingredients[4].Name.Should().Be("smör");
        recipe.Ingredients[4].Preparation.Should().Be("smält");

        recipe.Instructions.Should().HaveCount(3);
        recipe.Instructions[0].StepNumber.Should().Be(1);
        recipe.Instructions[0].InstructionText.Should().Contain("Blanda mjöl och salt");
    }

    [Fact]
    public void ParseFromHtml_ArlaStyleWithHowToSection_ShouldExtractStepsAndTotalTime()
    {
        const string html = """
            <!DOCTYPE html>
            <html>
            <head>
                <script type="application/ld+json">
                [
                  {
                    "@context": "http://schema.org",
                    "@type": ["Recipe", "NewsArticle"],
                    "name": "Kladdkaka med grädde",
                    "description": "Seg och chokladig kladdkaka.",
                    "totalTime": "PT35M",
                    "recipeYield": ["8 bitar"],
                    "image": ["https://images.arla.com/kladdkaka.webp"],
                    "recipeIngredient": [
                      "100 g smör",
                      "2,5 dl strösocker",
                      "2 ägg"
                    ],
                    "recipeInstructions": [
                      {
                        "@type": "HowToSection",
                        "name": "Smet",
                        "itemListElement": [
                          { "@type": "HowToStep", "text": "Sätt ugnen på 175 grader." },
                          { "@type": "HowToStep", "text": "Smält smöret i en kastrull." }
                        ]
                      }
                    ]
                  }
                ]
                </script>
            </head>
            <body></body>
            </html>
            """;

        var importer = CreateImporter();
        var result = importer.ParseFromHtml(html, "https://www.arla.se/recept/kladdkaka/");

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Kladdkaka med grädde");
        recipe.CookTimeMinutes.Should().Be(35);
        recipe.Servings.Should().Be(8);
        recipe.Ingredients.Should().HaveCount(3);
        recipe.Instructions.Should().HaveCount(2);
    }

    [Fact]
    public void ParseFromHtml_WithoutJsonLd_ShouldUseHtmlFallbackStrategy()
    {
        const string html = """
            <!DOCTYPE html>
            <html lang="sv">
            <head>
                <meta property="og:title" content="Mormors köttbullar" />
                <meta property="og:description" content="Klassiska svenska köttbullar med gräddsås." />
                <meta property="og:image" content="https://koket.se/kottbullar.jpg" />
            </head>
            <body>
                <article class="recipe">
                    <h1>Mormors köttbullar</h1>
                    <p>4 portioner</p>
                    <h2>Ingredienser</h2>
                    <ul>
                        <li>500 g blandfärs</li>
                        <li>1 dl ströbröd</li>
                        <li>1,5 dl mjölk</li>
                    </ul>
                    <h2>Gör så här</h2>
                    <ol>
                        <li>Blanda ströbröd och mjölk och låt svälla i 10 minuter.</li>
                        <li>Arbeta in färsen och rulla jämna köttbullar.</li>
                        <li>Stek i rikligt med smör tills de är gyllenbruna.</li>
                    </ol>
                </article>
            </body>
            </html>
            """;

        var importer = CreateImporter();
        var result = importer.ParseFromHtml(html, "https://koket.se/mormors-kottbullar");

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Mormors köttbullar");
        recipe.Servings.Should().Be(4);
        recipe.Ingredients.Should().HaveCount(3);
        recipe.Ingredients[0].Quantity.Should().Be(500m);
        recipe.Ingredients[0].Unit.Should().Be("g");
        recipe.Instructions.Should().HaveCount(3);
    }

    [Fact]
    public void ParseFromHtml_HtmlFallbackWithNestedSpans_ShouldPreserveSpacesBetweenQuantityUnitAndName()
    {
        const string html = """
            <!DOCTYPE html>
            <html lang="sv">
            <head><title>Kycklinggryta</title></head>
            <body>
                <h1>Kycklinggryta</h1>
                <ul class="recipe-ingredients">
                    <li><span>4</span><span>kycklingfiléer</span></li>
                    <li><span>0,5</span><span>dl</span><span>vetemjöl</span></li>
                    <li><span>2</span><span>msk</span><span>olivolja</span></li>
                    <li><span>2</span><span>vitlöksklyftor</span></li>
                    <li><span>1</span><span>burk</span><span>krossade tomater (à 400 g)</span></li>
                </ul>
                <ol class="recipe-instructions">
                    <li>Bryn kycklingen och koka ihop.</li>
                </ol>
            </body>
            </html>
            """;

        var importer = CreateImporter();
        var result = importer.ParseFromHtml(html, "https://example.se/kycklinggryta");

        result.Success.Should().BeTrue();
        var ingredients = result.DraftRecipe!.Ingredients;
        ingredients.Should().HaveCount(5);

        ingredients[0].Quantity.Should().Be(4m);
        ingredients[0].Unit.Should().BeNull();
        ingredients[0].Name.Should().Be("kycklingfiléer");

        ingredients[1].Quantity.Should().Be(0.5m);
        ingredients[1].Unit.Should().Be("dl");
        ingredients[1].Name.Should().Be("vetemjöl");

        ingredients[2].Quantity.Should().Be(2m);
        ingredients[2].Unit.Should().Be("msk");
        ingredients[2].Name.Should().Be("olivolja");

        ingredients[3].Quantity.Should().Be(2m);
        ingredients[3].Unit.Should().BeNull();
        ingredients[3].Name.Should().Be("vitlöksklyftor");

        ingredients[4].Quantity.Should().Be(1m);
        ingredients[4].Unit.Should().Be("burk");
        ingredients[4].Name.Should().Be("krossade tomater");
        ingredients[4].Comment.Should().Be("à 400 g");
    }

    [Fact]
    public void ParseFromHtml_WhenPageHasNoRecipeData_ShouldReturnGracefulSwedishError()
    {
        const string html = "<html><head></head><body><div>Ingen mat här</div></body></html>";

        var importer = CreateImporter();
        var result = importer.ParseFromHtml(html, "https://example.com/empty");

        result.Success.Should().BeFalse();
        result.DraftRecipe.Should().BeNull();
        result.ErrorMessage.Should().Contain("Inget strukturerat recept kunde hittas");
    }

    [Fact]
    public async Task ImportFromUrlAsync_WhenRedirectedToPrivateIp_ShouldBlockAndReturnError()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = new Uri("http://192.168.1.1/internal-admin");
            return resp;
        });

        var importer = CreateImporter(handler);
        var result = await importer.ImportFromUrlAsync("https://example.com/redirect-me");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("blocker");
    }

    [Fact]
    public void ParseFromHtml_IcaMarryMeChicken730140Fixture_ShouldExtractAllIngredientsAndSteps()
    {
        const string html = """
            <!DOCTYPE html>
            <html lang="sv">
            <head>
                <title>Marry me chicken | Recept ICA.se</title>
                <script type="application/ld+json">
                {
                  "@context": "http://schema.org",
                  "@type": "Recipe",
                  "name": "Marry me chicken",
                  "description": "En underbart krämig kycklinggryta med soltorkade tomater, vitlök och parmesan.",
                  "totalTime": "PT30M",
                  "recipeYield": "4 portioner",
                  "image": "https://assets.icanet.se/e_sharpen:80,f_auto,q_auto,t_1200x1200/imagevaultfiles/id_254100/cf_259/marry_me_chicken.jpg",
                  "recipeIngredient": [
                    "600 g kycklingfilé",
                    "1 tsk salt",
                    "2 krm svartpeppar",
                    "2 msk olivolja",
                    "3 vitlöksklyftor",
                    "2,5 dl vispgrädde",
                    "1,5 dl kycklingbuljong",
                    "1 dl riven parmesanost",
                    "1 dl strimlade soltorkade tomater",
                    "1 tsk torkad oregano",
                    "0,5 tsk chiliflakes"
                  ],
                  "recipeInstructions": [
                    { "@type": "HowToStep", "text": "Dela kycklingfiléerna på längden och krydda med salt och peppar." },
                    { "@type": "HowToStep", "text": "Stek kycklingen gyllene i olivolja i en rymlig stekpanna och lägg åt sidan." },
                    { "@type": "HowToStep", "text": "Fräs finhackad vitlök, tillsätt grädde, buljong, parmesan, soltorkade tomater och kryddor. Låt sjuda med kycklingen i 10 minuter." }
                  ]
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        var importer = CreateImporter();
        var result = importer.ParseFromHtml(html, "https://www.ica.se/recept/marry-me-chicken-730140/");

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Marry me chicken");
        recipe.Servings.Should().Be(4);
        recipe.CookTimeMinutes.Should().Be(30);
        recipe.SourceHost.Should().Be("ica.se");
        recipe.Ingredients.Should().HaveCount(11);
        recipe.Instructions.Should().HaveCount(3);
    }

    [Fact]
    public async Task ImportFromUrlAsync_WhenIcaReturns403Forbidden_ShouldReturnClearAccessBlockedMessage()
    {
        const string akamai403Body = """
            <HTML><HEAD>
            <TITLE>Access Denied</TITLE>
            </HEAD><BODY>
            <H1>Access Denied</H1>
            You don't have permission to access "http&#58;&#47;&#47;www&#46;ica&#46;se&#47;recept&#47;marry&#45;me&#45;chicken&#45;730140&#47;" on this server.<P>
            Reference&#32;&#35;18&#46;85d4f748&#46;1776715743&#46;39647bc6
            <P>https&#58;&#47;&#47;errors&#46;edgesuite&#46;net&#47;18&#46;85d4f748&#46;1776715743&#46;39647bc6</P>
            </BODY>
            </HTML>
            """;

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "Forbidden",
            Content = new StringContent(akamai403Body, Encoding.UTF8, "text/html")
        });

        var importer = CreateImporter(handler);
        var result = await importer.ImportFromUrlAsync("https://www.ica.se/recept/marry-me-chicken-730140/");

        result.Success.Should().BeFalse();
        result.DraftRecipe.Should().BeNull();
        result.ErrorMessage.Should().Contain("ica.se");
        result.ErrorMessage.Should().Contain("403");
        result.ErrorMessage.Should().Contain("blockerade automatisk hämtning");
    }

    [Fact]
    public void ParseFromHtml_WhenHtmlIsAkamaiAccessDeniedPage_ShouldReturnClearAccessBlockedMessage()
    {
        const string akamaiBlockHtml = """
            <HTML><HEAD>
            <TITLE>Access Denied</TITLE>
            </HEAD><BODY>
            <H1>Access Denied</H1>
            You don't have permission to access "http://www.ica.se/recept/marry-me-chicken-730140/" on this server.
            <P>https://errors.edgesuite.net/18.85d4f748</P>
            </BODY></HTML>
            """;

        var importer = CreateImporter();
        var result = importer.ParseFromHtml(akamaiBlockHtml, "https://www.ica.se/recept/marry-me-chicken-730140/");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("blockerade automatisk hämtning");
    }

    [Fact]
    public async Task ImportFromUrlAsync_WhenRemoteReturns404NotFound_ShouldReturnClearNotFoundMessage()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var importer = CreateImporter(handler);
        var result = await importer.ImportFromUrlAsync("https://www.koket.se/recept/finns-inte");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("404");
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
