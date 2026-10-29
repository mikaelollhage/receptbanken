using FluentAssertions;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Tests.Infrastructure;

public class HtmlRecipeTextExtractorTests
{
    private readonly HtmlRecipeTextExtractor _extractor = new();

    [Fact]
    public void IsHtml_ShouldIdentifyHtmlTags_AndRejectPlainText()
    {
        HtmlRecipeTextExtractor.IsHtml("<h2>Recept</h2><p>text</p>").Should().BeTrue();
        HtmlRecipeTextExtractor.IsHtml("<div><span>Någon text</span></div>").Should().BeTrue();
        HtmlRecipeTextExtractor.IsHtml("<!DOCTYPE html><html><body></body></html>").Should().BeTrue();
        HtmlRecipeTextExtractor.IsHtml("<ul><li>1 ägg</li></ul>").Should().BeTrue();
        HtmlRecipeTextExtractor.IsHtml("Enkel text utan taggar").Should().BeFalse();
        HtmlRecipeTextExtractor.IsHtml("600 g nötfärs\n1 lök\n2 klyftor vitlök").Should().BeFalse();
        HtmlRecipeTextExtractor.IsHtml(null).Should().BeFalse();
        HtmlRecipeTextExtractor.IsHtml("   ").Should().BeFalse();
    }

    [Fact]
    public void Extract_WhenGivenNiiinisHtml_ShouldPreserveHeadingsParagraphsAndTip_AndRemoveAdsAndNav()
    {
        var result = _extractor.Extract(NiiinisRecipeFixtures.Html);

        result.Success.Should().BeTrue();
        var text = result.ExtractedText;

        // Headings preserved
        text.Should().Contain("## Ninas Köttfärssås, 4 p");
        text.Should().Contain("## Gör såhär:");

        // Ingredients preserved
        text.Should().Contain("600 g nötfärs");
        text.Should().Contain("1 lök");
        text.Should().Contain("2 klyftor vitlök");
        text.Should().Contain("1 msk smör till stekning");
        text.Should().Contain("1 burk krossade tomater");
        text.Should().Contain("salt och svartpeppar");
        text.Should().Contain("1 nypa socker");

        // Steps preserved
        text.Should().Contain("1) Börja med att steka köttfärsen");
        text.Should().Contain("2) Hacka löken");
        text.Should().Contain("3) Häll i krossade tomater");
        text.Should().Contain("4) Häll i grädde");

        // Serving tip preserved
        text.Should().Contain("Tips: Riv över parmesan innan servering.");

        // Unwanted elements removed
        text.Should().NotContain("Annons: Köp köksredskap");
        text.Should().NotContain("Sponsrat inlägg: Glöm inte att använda rabattkoden");
        text.Should().NotContain("All rights reserved");
        text.Should().NotContain("main-nav");

        // Metadata image and canonical source extracted safely
        result.RemoteImageUrl.Should().Be("https://images.expressen.se/images/niiinis-kottfarssas.jpg");
        result.SourceUrl.Should().Be(NiiinisRecipeFixtures.PageUrl);
    }

    [Fact]
    public void Extract_WithUlOlLists_ShouldFormatWithBulletsAndNumbers()
    {
        const string html = """
            <div class="recipe">
                <h1>Pannkakor</h1>
                <h3>Ingredienser</h3>
                <ul>
                    <li>2 1/2 dl vetemjöl</li>
                    <li>1/2 tsk salt</li>
                    <li>6 dl mjölk</li>
                </ul>
                <h3>Tillagning</h3>
                <ol>
                    <li>Blanda mjöl och salt.</li>
                    <li>Vispa i mjölk.</li>
                    <li>Grädda i stekpanna.</li>
                </ol>
            </div>
            """;

        var result = _extractor.Extract(html);

        result.Success.Should().BeTrue();
        var text = result.ExtractedText;

        text.Should().Contain("## Pannkakor");
        text.Should().Contain("## Ingredienser");
        text.Should().Contain("- 2 1/2 dl vetemjöl");
        text.Should().Contain("- 1/2 tsk salt");
        text.Should().Contain("- 6 dl mjölk");
        text.Should().Contain("## Tillagning");
        text.Should().Contain("1. Blanda mjöl och salt.");
        text.Should().Contain("2. Vispa i mjölk.");
        text.Should().Contain("3. Grädda i stekpanna.");
    }

    [Fact]
    public void Extract_WithScriptsStylesAndForms_ShouldStripThemCompletely()
    {
        const string html = """
            <div>
                <script>alert('malicious-script');</script>
                <style>.secret { display: none; }</style>
                <form action="/login"><input type="text" value="secret" /></form>
                <h2>Klassisk soppa</h2>
                <p>1 liter vatten</p>
                <p>2 tärningar buljong</p>
            </div>
            """;

        var result = _extractor.Extract(html);

        result.Success.Should().BeTrue();
        result.ExtractedText.Should().NotContain("alert");
        result.ExtractedText.Should().NotContain("malicious-script");
        result.ExtractedText.Should().NotContain(".secret");
        result.ExtractedText.Should().NotContain("/login");
        result.ExtractedText.Should().Contain("## Klassisk soppa");
        result.ExtractedText.Should().Contain("1 liter vatten");
        result.ExtractedText.Should().Contain("2 tärningar buljong");
    }

    [Fact]
    public void Extract_WithHiddenElements_ShouldRemoveHiddenContent()
    {
        const string html = """
            <div>
                <h2>Paj</h2>
                <p>3 dl mjöl</p>
                <p hidden>Detta är dolt innehåll</p>
                <div style="display:none">Dold rabattkod</div>
                <div style="visibility: hidden">Dold text</div>
                <span aria-hidden="true">Ikontext</span>
                <p>125 g smör</p>
            </div>
            """;

        var result = _extractor.Extract(html);

        result.Success.Should().BeTrue();
        result.ExtractedText.Should().Contain("3 dl mjöl");
        result.ExtractedText.Should().Contain("125 g smör");
        result.ExtractedText.Should().NotContain("Detta är dolt innehåll");
        result.ExtractedText.Should().NotContain("Dold rabattkod");
        result.ExtractedText.Should().NotContain("Dold text");
        result.ExtractedText.Should().NotContain("Ikontext");
    }

    [Fact]
    public void Extract_WithMalformedHtml_ShouldParseSafelyWithoutCrashing()
    {
        const string malformed = "<div class='content'><h2>Pannkaka<p>1 ägg<div>2 dl mjöl<br>vispa väl";

        var result = _extractor.Extract(malformed);

        result.Success.Should().BeTrue();
        result.ExtractedText.Should().Contain("Pannkaka");
        result.ExtractedText.Should().Contain("1 ägg");
        result.ExtractedText.Should().Contain("2 dl mjöl");
        result.ExtractedText.Should().Contain("vispa väl");
    }

    [Fact]
    public void Extract_WithEmptyOrWhitespaceInput_ShouldReturnUnsuccessful()
    {
        _extractor.Extract("").Success.Should().BeFalse();
        _extractor.Extract("   ").Success.Should().BeFalse();
        _extractor.Extract(null!).Success.Should().BeFalse();
    }
}

