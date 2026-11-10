using FluentAssertions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class VoiceShoppingListParserTests
{
    private readonly VoiceShoppingListParser _parser;

    public VoiceShoppingListParserTests()
    {
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var ingredientParser = new SwedishIngredientParser();
        _parser = new VoiceShoppingListParser(ingredientParser, aggregator);
    }

    [Fact]
    public void ParseItem_SingleItem_ShouldParseCorrectly()
    {
        var result = _parser.ParseItem("2 liter mjölk");

        result.Success.Should().BeTrue();
        result.RawPhrase.Should().Be("2 liter mjölk");
        result.Name.Should().Be("mjölk");
        result.Quantity.Should().Be(2m);
        result.QuantityMax.Should().BeNull();
        result.Unit.Should().Be("l");
        result.Comment.Should().BeNull();
        result.DisplayText.Should().Be("2 l mjölk");
        result.Error.Should().BeNull();
    }

    [Fact]
    public void ParseItem_ItemWithoutQuantityOrUnit_ShouldSucceedWithOnlyName()
    {
        var result = _parser.ParseItem("smör");

        result.Success.Should().BeTrue();
        result.RawPhrase.Should().Be("smör");
        result.Name.Should().Be("smör");
        result.Quantity.Should().BeNull();
        result.QuantityMax.Should().BeNull();
        result.Unit.Should().BeNull();
        result.Comment.Should().BeNull();
        result.DisplayText.Should().Be("smör");
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("1,5 dl vispgrädde", 1.5, null, "dl", "vispgrädde", null)]
    [InlineData("½ tsk salt", 0.5, null, "tsk", "salt", null)]
    [InlineData("1 1/2 msk olivolja", 1.5, null, "msk", "olivolja", null)]
    [InlineData("1-2 klyftor vitlök", 1.0, 2.0, "klyftor", "vitlök", null)]
    [InlineData("500 g nötfärs", 500, null, "g", "nötfärs", null)]
    [InlineData("1 paket jäst", 1, null, "paket", "jäst", null)]
    [InlineData("2 st ägg", 2, null, "st", "ägg", null)]
    [InlineData("1 burk krossade tomater (à 400 g)", 1, null, "burk", "krossade tomater", "à 400 g")]
    [InlineData("2 msk smör, smält", 2, null, "msk", "smör", "smält")]
    [InlineData("en tomat", 1.0, null, null, "tomat", null)]
    [InlineData("ett äpple", 1.0, null, null, "äpple", null)]
    [InlineData("två gurkor", 2.0, null, null, "gurkor", null)]
    [InlineData("tre bananer", 3.0, null, null, "bananer", null)]
    [InlineData("fyra röda lökar", 4.0, null, null, "röda lökar", null)]
    [InlineData("fem dl mjölk", 5.0, null, "dl", "mjölk", null)]
    [InlineData("två liter mellanmjölk", 2.0, null, "l", "mellanmjölk", null)]
    [InlineData("tre paket kaffe", 3.0, null, "paket", "kaffe", null)]
    [InlineData("tio ägg", 10.0, null, null, "ägg", null)]
    [InlineData("en mjölk", 1.0, null, null, "mjölk", null)]
    [InlineData("en ketchup", 1.0, null, null, "ketchup", null)]
    [InlineData("ett paket ris", 1.0, null, "paket", "ris", null)]
    [InlineData("en nypa salt", 1.0, null, "nypa", "salt", null)]
    public void ParseItem_VariousQuantitiesAndUnits_ShouldExtractExpectedFields(
        string input, double expectedQty, double? expectedMax, string? expectedUnit, string expectedName, string? expectedComment)
    {
        var result = _parser.ParseItem(input);

        result.Success.Should().BeTrue();
        result.RawPhrase.Should().Be(input);
        result.Name.Should().Be(expectedName);
        result.Quantity.Should().Be((decimal)expectedQty);
        if (expectedMax.HasValue)
        {
            result.QuantityMax.Should().Be((decimal)expectedMax.Value);
        }
        else
        {
            result.QuantityMax.Should().BeNull();
        }
        result.Unit.Should().Be(expectedUnit);
        result.Comment.Should().Be(expectedComment);
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("enbär", "enbär")]
    [InlineData("enkel grädde", "enkel grädde")]
    [InlineData("ettårig ost", "ettårig ost")]
    [InlineData("ett halvt kilo potatis", "ett halvt kilo potatis")]
    [InlineData("en och en halv liter mjölk", "en och en halv liter mjölk")]
    [InlineData("en halv liter", "en halv liter")]
    [InlineData("en halv liter mjölk", "en halv liter mjölk")]
    [InlineData("två och en halv liter mjölk", "två och en halv liter mjölk")]
    [InlineData("två halva avokador", "två halva avokador")]
    [InlineData("Sex on the Beach", "Sex on the Beach")]
    public void ParseItem_RegressionPhrases_ShouldNotExtractFalseCount(string input, string expectedName)
    {
        var result = _parser.ParseItem(input);

        result.Success.Should().BeTrue();
        result.Quantity.Should().BeNull();
        result.Unit.Should().BeNull();
        result.Name.Should().Be(expectedName);
    }

    [Fact]
    public void ParsePreview_JarvisCommandItems_ShouldParseAllQuantitiesCorrectly()
    {
        // Motiverande exempel från verkligt Jarvis-kommando:
        // "Skriv upp en tomat, två gurkor och sallad på inköpslistan."
        var request = new VoiceShoppingListRequest
        {
            Items = ["en tomat", "två gurkor", "sallad"]
        };

        var preview = _parser.ParsePreview(request);

        preview.ValidCount.Should().Be(3);
        preview.ErrorCount.Should().Be(0);

        preview.Items[0].Success.Should().BeTrue();
        preview.Items[0].Name.Should().Be("tomat");
        preview.Items[0].Quantity.Should().Be(1m);
        preview.Items[0].DisplayText.Should().Be("1 tomat");

        preview.Items[1].Success.Should().BeTrue();
        preview.Items[1].Name.Should().Be("gurkor");
        preview.Items[1].Quantity.Should().Be(2m);
        preview.Items[1].DisplayText.Should().Be("2 gurkor");

        preview.Items[2].Success.Should().BeTrue();
        preview.Items[2].Name.Should().Be("sallad");
        preview.Items[2].Quantity.Should().BeNull();
        preview.Items[2].DisplayText.Should().Be("sallad");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseItem_EmptyOrWhitespace_ShouldReturnError(string? input)
    {
        var result = _parser.ParseItem(input);

        result.Success.Should().BeFalse();
        result.RawPhrase.Should().Be(input ?? string.Empty);
        result.Error.Should().Be("Varufrasen kan inte vara tom.");
        result.Name.Should().BeNull();
    }

    [Fact]
    public void ParseItem_TooLongPhrase_ShouldReturnLengthError()
    {
        var tooLong = new string('a', 201);
        var result = _parser.ParseItem(tooLong);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("överskrider tillåten längd");
    }

    [Theory]
    [InlineData("2 dl")]
    [InlineData("500 g")]
    [InlineData("1-2 st")]
    public void ParseItem_QuantityAndUnitWithoutItemName_ShouldReportMissingNameError(string input)
    {
        var result = _parser.ParseItem(input);

        result.Success.Should().BeFalse();
        result.Quantity.Should().NotBeNull();
        result.Unit.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().Contain("Inget varunamn angavs");
    }

    [Theory]
    [InlineData("???")]
    [InlineData("...")]
    [InlineData("123")]
    public void ParseItem_InvalidPunctuationOrDigits_ShouldReportInvalidNameError(string input)
    {
        var result = _parser.ParseItem(input);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ParsePreview_MultipleItems_ShouldReturnStructuredResultsAndCorrectCounts()
    {
        var request = new VoiceShoppingListRequest
        {
            Items =
            [
                "2 liter mjölk",
                "500 g köttfärs",
                "   ",
                "2 dl",
                "smör"
            ]
        };

        var response = _parser.ParsePreview(request);

        response.TotalCount.Should().Be(5);
        response.ValidCount.Should().Be(3);
        response.ErrorCount.Should().Be(2);
        response.Items.Should().HaveCount(5);

        response.Items[0].Success.Should().BeTrue();
        response.Items[0].Name.Should().Be("mjölk");

        response.Items[1].Success.Should().BeTrue();
        response.Items[1].Name.Should().Be("köttfärs");

        response.Items[2].Success.Should().BeFalse();
        response.Items[2].Error.Should().Be("Varufrasen kan inte vara tom.");

        response.Items[3].Success.Should().BeFalse();
        response.Items[3].Error.Should().Contain("Inget varunamn angavs");

        response.Items[4].Success.Should().BeTrue();
        response.Items[4].Name.Should().Be("smör");
    }

    [Fact]
    public void ParsePreview_EmptyItems_ShouldThrowArgumentException()
    {
        var request = new VoiceShoppingListRequest { Items = [] };
        var act = () => _parser.ParsePreview(request);
        act.Should().Throw<ArgumentException>().WithMessage("*items*");
    }

    [Fact]
    public void ParsePreview_TooManyItems_ShouldThrowArgumentException()
    {
        var request = new VoiceShoppingListRequest
        {
            Items = Enumerable.Range(1, 101).Select(i => $"Vara {i}").ToList()
        };

        var act = () => _parser.ParsePreview(request);
        act.Should().Throw<ArgumentException>().WithMessage("*Maximalt 100*");
    }
}
