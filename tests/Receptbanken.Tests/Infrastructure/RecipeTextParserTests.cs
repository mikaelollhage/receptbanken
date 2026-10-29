using FluentAssertions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Tests.Infrastructure;

public class RecipeTextParserTests
{
    private readonly RecipeTextParser _parser = new(new SwedishIngredientParser());

    [Fact]
    public void Parse_NiiinisCase_ShouldExtractTitleServings13Ingredients4StepsAndTip()
    {
        var result = _parser.Parse(NiiinisRecipeFixtures.PlainText, NiiinisRecipeFixtures.PageUrl);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        // 1. Recipe Title and Portions
        recipe.Title.Should().Be("Ninas Köttfärssås");
        recipe.Servings.Should().Be(4);
        recipe.ServingsUnit.Should().Be("portioner");

        // 2. Ingredients (13 ingredients)
        recipe.Ingredients.Should().HaveCount(13);

        var ing1 = recipe.Ingredients[0];
        ing1.Quantity.Should().Be(600);
        ing1.Unit.Should().Be("g");
        ing1.Name.Should().Be("nötfärs");

        var ing2 = recipe.Ingredients[1];
        ing2.Quantity.Should().Be(1);
        ing2.Name.Should().Be("lök");

        var ing3 = recipe.Ingredients[2];
        ing3.Quantity.Should().Be(2);
        ing3.Unit.Should().Be("klyftor");
        ing3.Name.Should().Be("vitlök");

        var ing4 = recipe.Ingredients[3];
        ing4.Quantity.Should().Be(1);
        ing4.Unit.Should().Be("msk");
        ing4.Name.Should().Be("smör till stekning");

        // Ingredient without quantity
        var ingSalt = recipe.Ingredients.First(i => i.Name.Contains("salt och svartpeppar"));
        ingSalt.Quantity.Should().BeNull();

        // 3. 4 Numbered Instructions
        recipe.Instructions.Should().HaveCount(4);
        recipe.Instructions[0].StepNumber.Should().Be(1);
        recipe.Instructions[0].InstructionText.Should().StartWith("Börja med att steka köttfärsen");
        recipe.Instructions[1].StepNumber.Should().Be(2);
        recipe.Instructions[1].InstructionText.Should().StartWith("Hacka löken");
        recipe.Instructions[2].StepNumber.Should().Be(3);
        recipe.Instructions[2].InstructionText.Should().StartWith("Häll i krossade tomater");
        recipe.Instructions[3].StepNumber.Should().Be(4);
        recipe.Instructions[3].InstructionText.Should().StartWith("Häll i grädde");

        // 4. Serving tip in notes
        recipe.Notes.Should().NotBeNull();
        recipe.Notes.Should().Contain("Tips: Riv över parmesan innan servering.");

        // 5. Source host
        recipe.SourceUrl.Should().Be(NiiinisRecipeFixtures.PageUrl);
        recipe.SourceHost.Should().Be("alltommat.expressen.se");
    }

    [Theory]
    [InlineData("Pannkakor, 4 portioner", "Pannkakor", 4, "portioner")]
    [InlineData("Kladdkaka (8 bitar)", "Kladdkaka", 8, "bitar")]
    [InlineData("Ugnsbakad lax - 4 port", "Ugnsbakad lax", 4, "portioner")]
    [InlineData("Ninas Köttfärssås, 4 p", "Ninas Köttfärssås", 4, "portioner")]
    [InlineData("Småfrallor (12 st)", "Småfrallor", 12, "st")]
    public void Parse_HeadingsWithPortions_ShouldExtractCleanTitleAndPortions(
        string rawHeading, string expectedTitle, int expectedServings, string expectedUnit)
    {
        var text = $"""
            {rawHeading}

            Ingredienser:
            1 msk smör
            2 dl mjölk

            Gör så här:
            1. Blanda allt.
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        result.DraftRecipe!.Title.Should().Be(expectedTitle);
        result.DraftRecipe.Servings.Should().Be(expectedServings);
        result.DraftRecipe.ServingsUnit.Should().Be(expectedUnit);
    }

    [Fact]
    public void Parse_SubGroupsInIngredientsAndInstructions_ShouldPreserveGroupHeaders()
    {
        const string text = """
            ## Fransk äppelpaj

            Ingredienser

            Mördeg:
            3 dl vetemjöl
            125 g smör
            2 msk vatten

            Fyllning:
            4 äpplen
            2 msk socker
            1 tsk kanel

            Gör så här:
            Deg:
            1. Nyp ihop mördegen.
            2. Låt vila i kylskåp 30 minuter.

            Montering:
            3. Skiva äpplena och lägg i pajskalet.
            4. Grädda i 200 grader i 35 minuter.
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;

        recipe.Ingredients.Should().HaveCount(6);
        recipe.Ingredients.Take(3).Should().OnlyContain(i => i.GroupHeader == "Mördeg");
        recipe.Ingredients.Skip(3).Take(3).Should().OnlyContain(i => i.GroupHeader == "Fyllning");

        recipe.Instructions.Should().HaveCount(4);
        recipe.Instructions[0].GroupHeader.Should().Be("Deg");
        recipe.Instructions[1].GroupHeader.Should().Be("Deg");
        recipe.Instructions[2].GroupHeader.Should().Be("Montering");
        recipe.Instructions[3].GroupHeader.Should().Be("Montering");
    }

    [Fact]
    public void Parse_CookingTimesAndTips_ShouldExtractMinutesAndNotes()
    {
        const string text = """
            ## Snabb vardagsgryta

            Tid: 25 minuter
            Förberedelsetid: 10 min

            Ingredienser:
            500 g grytbitar
            1 msk olja

            Gör så här:
            1. Stek köttet i olja.

            Tips: Servera grytan med ris och en krispig sallad.
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;

        recipe.CookTimeMinutes.Should().Be(25);
        recipe.PrepTimeMinutes.Should().Be(10);
        recipe.Notes.Should().Contain("Tips: Servera grytan med ris och en krispig sallad.");
    }

    [Fact]
    public void Parse_UnnumberedInstructions_ShouldBeAssignedSequentialStepNumbers()
    {
        const string text = """
            ## Enkel pastasås

            Ingredienser:
            1 burk krossade tomater
            1 msk oregano

            Gör så här:
            Häll tomaterna i en kastrull.
            Tillsätt oregano och låt koka upp.
            Låt puttra i 10 minuter.
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;

        recipe.Instructions.Should().HaveCount(3);
        recipe.Instructions[0].StepNumber.Should().Be(1);
        recipe.Instructions[0].InstructionText.Should().Be("Häll tomaterna i en kastrull.");
        recipe.Instructions[1].StepNumber.Should().Be(2);
        recipe.Instructions[1].InstructionText.Should().Be("Tillsätt oregano och låt koka upp.");
        recipe.Instructions[2].StepNumber.Should().Be(3);
        recipe.Instructions[2].InstructionText.Should().Be("Låt puttra i 10 minuter.");
    }

    [Fact]
    public void Parse_NonRecipeText_ShouldReturnNoRecipeData()
    {
        const string text = """
            Idag var en fantastisk dag!
            Vi gick på promenad i skogen och drack varm choklad på ett mysigt fik.
            Imorgon ska vi åka och hälsa på mormor och morfar.
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
        result.ErrorMessage.Should().Contain("Inget recept kunde identifieras");
    }

    [Fact]
    public void Parse_EmptyOrWhitespaceText_ShouldReturnEmptyInput()
    {
        var result1 = _parser.Parse("");
        result1.Success.Should().BeFalse();
        result1.FailureReason.Should().Be(RecipeImportFailureReason.EmptyInput);

        var result2 = _parser.Parse("    \n\t   ");
        result2.Success.Should().BeFalse();
        result2.FailureReason.Should().Be(RecipeImportFailureReason.EmptyInput);

        var result3 = _parser.Parse(null);
        result3.Success.Should().BeFalse();
        result3.FailureReason.Should().Be(RecipeImportFailureReason.EmptyInput);
    }

    [Fact]
    public void Parse_TextExceedingMaxLimit_ShouldReturnInputTooLarge()
    {
        var hugeText = new string('A', RecipeImportLimits.MaxRecipeTextLength + 10);

        var result = _parser.Parse(hugeText);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(RecipeImportFailureReason.InputTooLarge);
        result.ErrorMessage.Should().Contain("för stor");
    }
}

