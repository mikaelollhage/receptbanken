using FluentAssertions;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class SwedishIngredientParserTests
{
    private readonly SwedishIngredientParser _parser = new();

    [Fact]
    public void Parse_CommaDecimalWithUnit_ShouldExtractQuantityUnitAndName()
    {
        var result = _parser.Parse("1,5 dl mjölk", 1);

        result.OrderIndex.Should().Be(1);
        result.Quantity.Should().Be(1.5m);
        result.QuantityMax.Should().BeNull();
        result.Unit.Should().Be("dl");
        result.Name.Should().Be("mjölk");
        result.Preparation.Should().BeNull();
    }

    [Fact]
    public void Parse_WithPreparationAfterComma_ShouldSeparateNameAndPreparation()
    {
        var result = _parser.Parse("2 msk smör, smält");

        result.Quantity.Should().Be(2m);
        result.Unit.Should().Be("msk");
        result.Name.Should().Be("smör");
        result.Preparation.Should().Be("smält");
    }

    [Theory]
    [InlineData("½ tsk salt", 0.5, "tsk", "salt", null)]
    [InlineData("1 ½ dl vispgrädde", 1.5, "dl", "vispgrädde", null)]
    [InlineData("1½ dl gräddfil", 1.5, "dl", "gräddfil", null)]
    [InlineData("1/2 krm vitpeppar", 0.5, "krm", "vitpeppar", null)]
    [InlineData("1 1/2 msk olivolja", 1.5, "msk", "olivolja", null)]
    [InlineData("200 g mörk choklad", 200, "g", "mörk choklad", null)]
    [InlineData("ca 400 g nötfärs", 400, "g", "nötfärs", null)]
    [InlineData("cirka 500 g potatis", 500, "g", "potatis", null)]
    [InlineData("1 burk krossade tomater (à 400 g)", 1, "burk", "krossade tomater", "à 400 g")]
    [InlineData("2 stycken ägg", 2, "st", "ägg", null)]
    [InlineData("1 förpackning saffran", 1, "förp", "saffran", null)]
    public void Parse_VariousSwedishFormats_ShouldParseAccurately(
        string input, decimal expectedQuantity, string expectedUnit, string expectedName, string? expectedComment)
    {
        var result = _parser.Parse(input);

        result.Quantity.Should().Be(expectedQuantity);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().Be(expectedComment);
    }

    [Fact]
    public void Parse_QuantityRange_ShouldExtractMinAndMaxQuantity()
    {
        var result = _parser.Parse("1-2 klyftor vitlök, finhackade");

        result.Quantity.Should().Be(1m);
        result.QuantityMax.Should().Be(2m);
        result.Unit.Should().Be("klyftor");
        result.Name.Should().Be("vitlök");
        result.Preparation.Should().Be("finhackade");
    }

    [Theory]
    [InlineData("1-2 tsk spiskummin", 1.0, 2.0, "tsk", "spiskummin")]
    [InlineData("1–2 tsk spiskummin", 1.0, 2.0, "tsk", "spiskummin")]
    [InlineData("1—2 tsk spiskummin", 1.0, 2.0, "tsk", "spiskummin")]
    [InlineData("½–1 tsk chiliflakes", 0.5, 1.0, "tsk", "chiliflakes")]
    [InlineData("ca 1–2 msk olivolja", 1.0, 2.0, "msk", "olivolja")]
    [InlineData("1 1/2 dl grädde", 1.5, null, "dl", "grädde")]
    public void Parse_RequiredIntervalsAndFractions_ShouldExtractAccurately(
        string rawLine, double expectedMin, double? expectedMax, string expectedUnit, string expectedName)
    {
        var result = _parser.Parse(rawLine);

        result.Quantity.Should().Be((decimal)expectedMin);
        if (expectedMax.HasValue)
        {
            result.QuantityMax.Should().Be((decimal)expectedMax.Value);
            result.Comment.Should().BeNull(); // Del B5: Interval is not dumped into Comment
        }
        else
        {
            result.QuantityMax.Should().BeNull();
        }
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
    }

    [Fact]
    public void Parse_SwedishWordNumbers_EnSkvattBalsamvinager_ShouldExtractQuantityAndUnit()
    {
        var result = _parser.Parse("En skvätt balsamvinäger");

        result.Quantity.Should().Be(1m);
        result.Unit.Should().Be("skvätt");
        result.Name.Should().Be("balsamvinäger");
    }

    [Theory]
    [InlineData("en tomat", 1.0, null, "tomat")]
    [InlineData("ett äpple", 1.0, null, "äpple")]
    [InlineData("två gurkor", 2.0, null, "gurkor")]
    [InlineData("tre bananer", 3.0, null, "bananer")]
    [InlineData("fyra röda lökar", 4.0, null, "röda lökar")]
    [InlineData("fem dl mjölk", 5.0, "dl", "mjölk")]
    [InlineData("sex citroner", 6.0, null, "citroner")]
    [InlineData("sju morötter", 7.0, null, "morötter")]
    [InlineData("åtta potatisar", 8.0, null, "potatisar")]
    [InlineData("nio plommon", 9.0, null, "plommon")]
    [InlineData("tio ägg", 10.0, null, "ägg")]
    [InlineData("elva päron", 11.0, null, "päron")]
    [InlineData("tolv ägg", 12.0, null, "ägg")]
    [InlineData("tjugo servetter", 20.0, null, "servetter")]
    [InlineData("en mjölk", 1.0, null, "mjölk")]
    [InlineData("en ketchup", 1.0, null, "ketchup")]
    [InlineData("ett paket ris", 1.0, "paket", "ris")]
    [InlineData("en nypa salt", 1.0, "nypa", "salt")]
    public void Parse_SwedishWordNumbers_OneToTenAndBeyond_ShouldExtractQuantityAndItem(
        string rawLine, double expectedQuantity, string? expectedUnit, string expectedName)
    {
        var result = _parser.Parse(rawLine);

        result.Quantity.Should().Be((decimal)expectedQuantity);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("två liter mellanmjölk", 2.0, "l", "mellanmjölk")]
    [InlineData("tre paket kaffe", 3.0, "paket", "kaffe")]
    [InlineData("två burkar krossade tomater", 2.0, "burkar", "krossade tomater")]
    [InlineData("tre msk olivolja", 3.0, "msk", "olivolja")]
    [InlineData("fyra klyftor vitlök", 4.0, "klyftor", "vitlök")]
    [InlineData("två st ägg", 2.0, "st", "ägg")]
    [InlineData("fem stycken äpplen", 5.0, "st", "äpplen")]
    public void Parse_SwedishWordNumbers_WithUnits_ShouldNormalizeUnitsCorrectly(
        string rawLine, double expectedQuantity, string expectedUnit, string expectedName)
    {
        var result = _parser.Parse(rawLine);

        result.Quantity.Should().Be((decimal)expectedQuantity);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
    }

    [Fact]
    public void Parse_SwedishWordNumbers_WithPreparation_ShouldExtractPreparationCorrectly()
    {
        var result = _parser.Parse("fyra röda lökar, finhackade");

        result.Quantity.Should().Be(4m);
        result.Unit.Should().BeNull();
        result.Name.Should().Be("röda lökar");
        result.Preparation.Should().Be("finhackade");
    }

    [Fact]
    public void Parse_SwedishWordNumbers_WithComment_ShouldExtractCommentCorrectly()
    {
        var result = _parser.Parse("tre paket kaffe (à 500 g)");

        result.Quantity.Should().Be(3m);
        result.Unit.Should().Be("paket");
        result.Name.Should().Be("kaffe");
        result.Comment.Should().Be("à 500 g");
    }

    [Theory]
    [InlineData("cirka två gurkor", 2.0, null, "gurkor")]
    [InlineData("ca tre äpplen", 3.0, null, "äpplen")]
    public void Parse_SwedishWordNumbers_WithCirkaPrefix_ShouldExtractQuantity(
        string rawLine, double expectedQuantity, string? expectedUnit, string expectedName)
    {
        var result = _parser.Parse(rawLine);

        result.Quantity.Should().Be((decimal)expectedQuantity);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("enbär", null, null, "enbär")]
    [InlineData("enkel grädde", null, null, "enkel grädde")]
    [InlineData("ettårig ost", null, null, "ettårig ost")]
    [InlineData("tvättmedel", null, null, "tvättmedel")]
    [InlineData("tvåpack hushållspapper", null, null, "tvåpack hushållspapper")]
    [InlineData("en halv liter", null, null, "en halv liter")]
    [InlineData("en halv liter mjölk", null, null, "en halv liter mjölk")]
    [InlineData("ett halvt kilo", null, null, "ett halvt kilo")]
    [InlineData("ett halvt kilo potatis", null, null, "ett halvt kilo potatis")]
    [InlineData("en och en halv liter", null, null, "en och en halv liter")]
    [InlineData("en och en halv liter mjölk", null, null, "en och en halv liter mjölk")]
    [InlineData("två och en halv liter", null, null, "två och en halv liter")]
    [InlineData("två halva avokador", null, null, "två halva avokador")]
    [InlineData("Sex on the Beach", null, null, "Sex on the Beach")]
    [InlineData("två till tre bananer", null, null, "två till tre bananer")]
    [InlineData("tre eller fyra tomater", null, null, "tre eller fyra tomater")]
    [InlineData("två tre gurkor", null, null, "två tre gurkor")]
    public void Parse_SwedishWordNumbers_NonCountWordsAndFractions_ShouldNotExtractFalseCount(
        string rawLine, double? expectedQuantity, string? expectedUnit, string expectedName)
    {
        var result = _parser.Parse(rawLine);

        if (expectedQuantity.HasValue)
        {
            result.Quantity.Should().Be((decimal)expectedQuantity.Value);
        }
        else
        {
            result.Quantity.Should().BeNull();
        }
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
    }

    [Fact]
    public void Parse_CountWithoutExplicitUnit_ShouldExtractQuantityAndName()
    {
        var result = _parser.Parse("3 ägg");

        result.Quantity.Should().Be(3m);
        result.Unit.Should().BeNull();
        result.Name.Should().Be("ägg");
    }

    [Fact]
    public void Parse_FreeformTextWithoutQuantity_ShouldPreserveFullTextAsName()
    {
        var result = _parser.Parse("salt och nymalen svartpepppar efter smak", 5, "Kryddning");

        result.Quantity.Should().BeNull();
        result.Unit.Should().BeNull();
        result.Name.Should().Be("salt och nymalen svartpepppar efter smak");
        result.GroupHeader.Should().Be("Kryddning");
        result.OrderIndex.Should().Be(5);
    }

    [Theory]
    [InlineData("4 portins", 4, null, "portins")]
    [InlineData("4portins", 4, null, "portins")]
    [InlineData("4 kycklingfiléer", 4, null, "kycklingfiléer")]
    [InlineData("4kycklingfiléer", 4, null, "kycklingfiléer")]
    [InlineData("0,5 dl vetemjöl", 0.5, "dl", "vetemjöl")]
    [InlineData("0,5dl vetemjöl", 0.5, "dl", "vetemjöl")]
    [InlineData("0,5dlvetemjöl", 0.5, "dl", "vetemjöl")]
    [InlineData("2 msk olivolja", 2, "msk", "olivolja")]
    [InlineData("2mskolivolja", 2, "msk", "olivolja")]
    [InlineData("2 vitlöksklyftor", 2, null, "vitlöksklyftor")]
    [InlineData("2vitlöksklyftor", 2, null, "vitlöksklyftor")]
    [InlineData("1 förp krossade tomater (ca 390,5 g)", 1, "förp", "krossade tomater", "ca 390,5 g")]
    public void Parse_QuantitiesWithAndWithoutUnitsAndParentheses_ShouldParseCorrectly(
        string input, decimal expectedQuantity, string? expectedUnit, string expectedName, string? expectedComment = null)
    {
        var result = _parser.Parse(input);

        result.Quantity.Should().Be(expectedQuantity);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().Be(expectedComment);
    }

    [Fact]
    public void Parse_WhenCommaInsideParenthesesAndPreparationOutside_ShouldExtractCommentAndPreparation()
    {
        var result = _parser.Parse("200 g mörk choklad (70%, ekologisk), grovhackad");

        result.Quantity.Should().Be(200m);
        result.Unit.Should().Be("g");
        result.Name.Should().Be("mörk choklad");
        result.Comment.Should().Be("70%, ekologisk");
        result.Preparation.Should().Be("grovhackad");
    }

    [Theory]
    [InlineData("4 kycklingfiléer (4 filéer motsvarar ca 600 g)", 4, null, "kycklingfiléer", "4 filéer motsvarar ca 600 g", null)]
    [InlineData("1 förp strimlade soltorkade tomater (à 200 g)", 1, "förp", "strimlade soltorkade tomater", "à 200 g", null)]
    [InlineData("1 1/2 dl riven parmesan (+ ev extra till servering)", 1.5, "dl", "riven parmesan", "+ ev extra till servering", null)]
    [InlineData("2 dl crème fraiche (34 %)", 2, "dl", "crème fraiche", "34 %", null)]
    [InlineData("1 burk kokosmjölk (400 ml)", 1, "burk", "kokosmjölk", "400 ml", null)]
    [InlineData("3 ägg", 3, null, "ägg", null, null)]
    [InlineData("salt", null, null, "salt", null, null)]
    [InlineData("peppar", null, null, "peppar", null, null)]
    [InlineData("2 1/2 dl vatten", 2.5, "dl", "vatten", null, null)]
    [InlineData("1/2 tsk oregano", 0.5, "tsk", "oregano", null, null)]
    [InlineData("2 msk olivolja", 2, "msk", "olivolja", null, null)]
    public void Parse_RequiredSpecificationExamples_ShouldParseAccurately(
        string input, object? expectedQty, string? expectedUnit, string expectedName, string? expectedComment, string? expectedPrep)
    {
        var result = _parser.Parse(input);

        result.RawText.Should().Be(input);
        var expectedDecimal = expectedQty == null ? (decimal?)null : Convert.ToDecimal(expectedQty, System.Globalization.CultureInfo.InvariantCulture);
        result.Quantity.Should().Be(expectedDecimal);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().Be(expectedComment);
        result.Preparation.Should().Be(expectedPrep);
    }

    [Theory]
    [InlineData("2 dl mjölk ()", 2, "dl", "mjölk ()")]
    [InlineData("mjölk (   )", null, null, "mjölk (   )")]
    public void Parse_EmptyParentheses_ShouldRetainParenthesesInNameAndNotProduceComment(
        string input, object? expectedQty, string? expectedUnit, string expectedName)
    {
        var result = _parser.Parse(input);

        var expectedDecimal = expectedQty == null ? (decimal?)null : Convert.ToDecimal(expectedQty, System.Globalization.CultureInfo.InvariantCulture);
        result.Quantity.Should().Be(expectedDecimal);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().BeNull();
        result.RawText.Should().Be(input);
    }

    [Theory]
    [InlineData("1 burk krossade tomater (ekologiska) (à 400 g)", 1, "burk", "krossade tomater", "ekologiska, à 400 g")]
    [InlineData("pasta (glutenfri) (kokt)", null, null, "pasta", "glutenfri, kokt")]
    public void Parse_MultipleParentheses_ShouldCombineTrailingComments(
        string input, object? expectedQty, string? expectedUnit, string expectedName, string expectedComment)
    {
        var result = _parser.Parse(input);

        var expectedDecimal = expectedQty == null ? (decimal?)null : Convert.ToDecimal(expectedQty, System.Globalization.CultureInfo.InvariantCulture);
        result.Quantity.Should().Be(expectedDecimal);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().Be(expectedComment);
        result.RawText.Should().Be(input);
    }

    [Theory]
    [InlineData("2 dl mjölk (ca 200 g", 2, "dl", "mjölk (ca 200 g")]
    [InlineData("2 dl mjölk ca 200 g)", 2, "dl", "mjölk ca 200 g)")]
    public void Parse_UnbalancedParentheses_ShouldPreserveParenthesesInNameSafely(
        string input, object? expectedQty, string? expectedUnit, string expectedName)
    {
        var result = _parser.Parse(input);

        var expectedDecimal = expectedQty == null ? (decimal?)null : Convert.ToDecimal(expectedQty, System.Globalization.CultureInfo.InvariantCulture);
        result.Quantity.Should().Be(expectedDecimal);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().BeNull();
        result.RawText.Should().Be(input);
    }

    [Theory]
    [InlineData("pasta (penne) med tomatsås", null, null, "pasta (penne) med tomatsås", null)]
    [InlineData("pasta (penne) med tomatsås (à 400 g)", null, null, "pasta (penne) med tomatsås", "à 400 g")]
    [InlineData("1 paket majs (eko) tortillas", 1, "paket", "majs (eko) tortillas", null)]
    public void Parse_ParenthesesInMiddleOfIngredientName_ShouldPreserveParenthesesInName(
        string input, object? expectedQty, string? expectedUnit, string expectedName, string? expectedComment)
    {
        var result = _parser.Parse(input);

        var expectedDecimal = expectedQty == null ? (decimal?)null : Convert.ToDecimal(expectedQty, System.Globalization.CultureInfo.InvariantCulture);
        result.Quantity.Should().Be(expectedDecimal);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().Be(expectedComment);
        result.RawText.Should().Be(input);
    }

    [Theory]
    [InlineData("flingsalt (efter smak)", "flingsalt", "efter smak")]
    [InlineData("färsk timjan (till garnering)", "färsk timjan", "till garnering")]
    [InlineData("havssalt (valfritt)", "havssalt", "valfritt")]
    public void Parse_IngredientWithoutQuantity_WithComment_ShouldExtractCommentAndName(
        string input, string expectedName, string expectedComment)
    {
        var result = _parser.Parse(input);

        result.Quantity.Should().BeNull();
        result.Unit.Should().BeNull();
        result.Name.Should().Be(expectedName);
        result.Comment.Should().Be(expectedComment);
        result.RawText.Should().Be(input);
    }

    [Theory]
    [InlineData("2 nävar babyspenat", 2, null, "nävar babyspenat", null)]
    [InlineData("2 nävar babyspenat (sköljd)", 2, null, "nävar babyspenat", "sköljd")]
    [InlineData("3 skopor proteinpulver (vanilj)", 3, null, "skopor proteinpulver", "vanilj")]
    public void Parse_UnknownUnits_ShouldRetainUnitInNameAndExtractComment(
        string input, object expectedQty, string? expectedUnit, string expectedName, string? expectedComment)
    {
        var result = _parser.Parse(input);

        var expectedDecimal = Convert.ToDecimal(expectedQty, System.Globalization.CultureInfo.InvariantCulture);
        result.Quantity.Should().Be(expectedDecimal);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
        result.Comment.Should().Be(expectedComment);
        result.RawText.Should().Be(input);
    }

    [Fact]
    public void Parse_WholeStringInParentheses_ShouldNotMakeNameEmpty()
    {
        var result = _parser.Parse("(valfritt)");

        result.Name.Should().Be("(valfritt)");
        result.Comment.Should().BeNull();
        result.RawText.Should().Be("(valfritt)");
    }

    [Fact]
    public void PortionScaling_WithComment_ShouldScaleQuantityWithoutAlteringComment()
    {
        var calc = new Receptbanken.Core.Services.ServingCalculator();
        var ingredient = _parser.Parse("1 1/2 dl riven parmesan (+ ev extra till servering)");

        var scaledQty = calc.ScaleQuantity(ingredient.Quantity, 4, 8);
        var prefix = calc.FormatScaledIngredientPrefix(ingredient, 4, 8);

        scaledQty.Should().Be(3m);
        prefix.Should().Be("3 dl ");
        ingredient.Comment.Should().Be("+ ev extra till servering");
    }

    [Fact]
    public void PortionScaling_WithWeightComment_ShouldNotScaleCommentTextAutomatically()
    {
        var calc = new Receptbanken.Core.Services.ServingCalculator();
        var ingredient = _parser.Parse("4 kycklingfiléer (4 filéer motsvarar ca 600 g)");

        var scaledQty = calc.ScaleQuantity(ingredient.Quantity, 4, 8);
        var prefix = calc.FormatScaledIngredientPrefix(ingredient, 4, 8);

        scaledQty.Should().Be(8m);
        prefix.Should().Be("8 ");
        ingredient.Comment.Should().Be("4 filéer motsvarar ca 600 g");
    }

    [Theory]
    [InlineData("0,5 kilo nötfärs", 0.5, "kg", "nötfärs")]
    [InlineData("1 kilogram potatis", 1, "kg", "potatis")]
    [InlineData("200 gram smör", 200, "g", "smör")]
    [InlineData("2 hekto räkor", 2, "hg", "räkor")]
    [InlineData("3 hektogram lösgodis", 3, "hg", "lösgodis")]
    [InlineData("1,5 liter mjölk", 1.5, "l", "mjölk")]
    [InlineData("2 deciliter grädde", 2, "dl", "grädde")]
    [InlineData("5 centiliter citronsaft", 5, "cl", "citronsaft")]
    [InlineData("50 milliliter vatten", 50, "ml", "vatten")]
    [InlineData("1 matsked soja", 1, "msk", "soja")]
    [InlineData("2 matskedar olivolja", 2, "msk", "olivolja")]
    [InlineData("1 tesked salt", 1, "tsk", "salt")]
    [InlineData("2 teskedar bakpulver", 2, "tsk", "bakpulver")]
    [InlineData("1 kryddmått svartpeppar", 1, "krm", "svartpeppar")]
    public void Parse_WrittenOutSwedishUnitNames_ShouldNormalizeToStandardUnitCodes(
        string input, double expectedQty, string expectedUnit, string expectedName)
    {
        var result = _parser.Parse(input);

        result.Quantity.Should().Be((decimal)expectedQty);
        result.Unit.Should().Be(expectedUnit);
        result.Name.Should().Be(expectedName);
    }
}

