using FluentAssertions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;

namespace Receptbanken.Tests.Infrastructure;

/// <summary>
/// Regression tests covering the 8 manual real-world test cases specified in Etapp 1D.2 Del G.
/// </summary>
public class RecipeManualRegressionTests
{
    private readonly RecipeTextParser _parser = new(new SwedishIngredientParser());

    [Fact]
    public void Test1_BlogNoise_ShouldExtractCleanRecipeWithoutNoiseOrRelatedLinks()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test1_BlogNoise);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Ugnsbakad lax med citron");
        recipe.Servings.Should().Be(4);
        recipe.ServingsUnit.Should().Be("portioner");
        recipe.Ingredients.Should().HaveCount(6);
        recipe.Instructions.Should().HaveCount(4);

        // No ads or related recipe links leaked into ingredients or steps
        recipe.Ingredients.Should().NotContain(i => i.Name.Contains("Torskrygg") || i.Name.Contains("Laxsoppa") || i.Name.Contains("HavsGott"));
        recipe.Instructions.Should().NotContain(s => s.InstructionText.Contains("HavsGott") || s.InstructionText.Contains("Facebook"));
        recipe.Notes.Should().Contain("Tips: Servera med kokt potatis och sockerärtor.");
    }

    [Fact]
    public void Test2_MultipleGroups_ShouldExtractAll12IngredientsIncludingSalladsblad_AndPreserveGroups()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test2_MultipleGroups);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Kycklingtacos med mangosalsa");
        recipe.Servings.Should().Be(4);
        recipe.Ingredients.Should().HaveCount(12);
        recipe.Instructions.Should().HaveCount(4);

        // Salladsblad (unquantified line under Till servering) must be present
        recipe.Ingredients.Should().Contain(i => i.Name == "Salladsblad");

        // Group headers must be preserved as metadata, not separate ingredients
        recipe.Ingredients.Should().NotContain(i => i.Name == "Kyckling" || i.Name == "Mangosalsa" || i.Name == "Till servering");
        recipe.Ingredients.Where(i => i.GroupHeader == "Till servering").Should().HaveCount(4);
    }

    [Fact]
    public void Test3_IngredientsWithoutQuantity_ShouldExtractAll7IngredientsIncludingFlingsaltAndBalsamvinager()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test3_IngredientsWithoutQuantity);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Enkel tomatsallad");
        recipe.Servings.Should().Be(2);
        recipe.Ingredients.Should().HaveCount(7);
        recipe.Instructions.Should().HaveCount(5);

        // Verify unquantified and expression ingredients
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Flingsalt"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Nymalen svartpeppar"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("balsamvinäger") || i.Name.Contains("En skvätt balsamvinäger"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Färsk basilika"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Olivolja"));
    }

    [Fact]
    public void Test4_UnnumberedInstructions_ShouldExtract7IngredientsIncludingParmesan_And5Steps()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test4_UnnumberedInstructions);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Krämig svamppasta");
        recipe.Servings.Should().Be(3);
        recipe.Ingredients.Should().HaveCount(7);
        recipe.Instructions.Should().HaveCount(5);

        // Parmesan till servering must be included
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Parmesan"));

        // All 5 unnumbered steps should be sequenced 1..5
        recipe.Instructions.Select(s => s.StepNumber).Should().Equal(1, 2, 3, 4, 5);
        recipe.Instructions[0].InstructionText.Should().StartWith("Koka pastan");
        recipe.Instructions[4].InstructionText.Should().StartWith("Blanda såsen");
    }

    [Fact]
    public void Test5_MeasurementsAndRanges_ShouldPreserveIntervalsWithoutInformationLoss()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test5_MeasurementsAndRanges);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Mustig vegetarisk gryta");
        recipe.Servings.Should().Be(6);
        recipe.Ingredients.Should().HaveCount(10);
        recipe.Instructions.Should().HaveCount(5);

        // 1-2 tsk spiskummin: quantity interval preserved in model, comment is NOT polluted with interval note
        var spiskummin = recipe.Ingredients.First(i => i.Name.Contains("spiskummin"));
        spiskummin.Quantity.Should().Be(1m);
        spiskummin.QuantityMax.Should().Be(2m);
        spiskummin.Unit.Should().Be("tsk");
        spiskummin.Comment.Should().BeNull();

        // ½-1 tsk chiliflakes: fraction range preserved
        var chili = recipe.Ingredients.First(i => i.Name.Contains("chiliflakes"));
        chili.Quantity.Should().Be(0.5m);
        chili.QuantityMax.Should().Be(1m);
        chili.Unit.Should().Be("tsk");
        chili.Comment.Should().BeNull();

        // 1 förp kikärtor (à 380 g): actual comment preserved
        var kikartor = recipe.Ingredients.First(i => i.Name.Contains("kikärtor"));
        kikartor.Comment.Should().Be("à 380 g");

        // ca 1-2 msk olivolja
        var olja = recipe.Ingredients.First(i => i.Name.Contains("olivolja"));
        olja.Quantity.Should().Be(1m);
        olja.QuantityMax.Should().Be(2m);
        olja.Unit.Should().Be("msk");

        // 1 1/2 dl grädde
        var gradde = recipe.Ingredients.First(i => i.Name.Contains("grädde"));
        gradde.Quantity.Should().Be(1.5m);
        gradde.Unit.Should().Be("dl");
    }

    [Fact]
    public void Test6_TwoRecipesInOneText_ShouldImportOnlyFirstRecipe_AndWarnUser()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test6_TwoRecipesInOneText);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Amerikanska pannkakor");
        recipe.Servings.Should().Be(4);
        recipe.Ingredients.Should().HaveCount(5);
        recipe.Instructions.Should().HaveCount(3);

        // Warning about multiple recipes
        result.IsUncertain.Should().BeTrue();
        result.WarningMessage.Should().NotBeNullOrWhiteSpace();
        result.WarningMessage.Should().Contain("flera recept");

        // No leak from Bananpannkakor
        recipe.Ingredients.Should().NotContain(i => i.Name.Contains("banan") || i.Name.Contains("kanel"));
        recipe.Instructions.Should().NotContain(s => s.InstructionText.Contains("Mosa bananerna"));
    }

    [Fact]
    public void Test7_AdvertInsideInstruction_ShouldStripDiscountCode_AndPreserveBothCookingActions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test7_AdvertInsideInstruction);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Saftig kyckling i ugn");
        recipe.Servings.Should().Be(4);
        recipe.Ingredients.Should().HaveCount(6);
        recipe.Instructions.Should().HaveCount(4);

        // Step 2 has rabattkod stripped, but both browning and placing in oven dish are preserved
        var step2 = recipe.Instructions[1].InstructionText;
        step2.Should().StartWith("Bryn kycklingen i smör i stekpannan");
        step2.Should().Contain("lägg sedan över kycklingen i en ugnsfast form");
        step2.Should().NotContain("KOCK20");
        step2.Should().NotContain("rabattkod");
        step2.Should().NotContain("KitchenWare");
    }

    [Fact]
    public void Test8_NonRecipeText_ShouldBeRejectedWithoutCreatingDraftRecipe()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test8_NonRecipeText);

        result.Success.Should().BeFalse();
        result.DraftRecipe.Should().BeNull();
        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
        result.ErrorMessage.Should().Contain("Inget recept kunde identifieras");
    }

    [Fact]
    public void Test9_BlogNoiseAndUnquantifiedIngredients_ShouldParseCleanly()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test9_CitronkycklingMedOrter);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Citronkyckling med örter");
        recipe.Servings.Should().Be(4);
        recipe.Ingredients.Should().HaveCount(8);
        recipe.Instructions.Should().HaveCount(4);

        // Verify unquantified ingredients are captured
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Flingsalt"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Svartpeppar"));

        // No ad or related recipe leak
        recipe.Ingredients.Should().NotContain(i => i.Name.Contains("pasta") || i.Name.Contains("kycklinggryta"));
        recipe.Instructions.Should().NotContain(s => s.InstructionText.Contains("Kryddriket") || s.InstructionText.Contains("rabatt"));
    }

    [Fact]
    public void Test10_IngredientGroupsAndUnnumberedInstructions_ShouldParseCorrectly()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test10_TacobowlMedAvokadodressing);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Tacobowl med avokadodressing");
        recipe.Servings.Should().Be(2);
        recipe.Ingredients.Should().HaveCount(10);
        recipe.Instructions.Should().HaveCount(4);

        // Verify groups
        recipe.Ingredients.Count(i => i.GroupHeader == "Bowl").Should().Be(6);
        recipe.Ingredients.Count(i => i.GroupHeader == "Dressing").Should().Be(4);
    }

    [Fact]
    public void Test11_MeasurementIntervalsAndComments_ShouldPreserveIntervalAndComment()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test11_KryddigLinsgryta);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Kryddig linsgryta");
        recipe.Servings.Should().Be(4);
        recipe.Ingredients.Should().HaveCount(8);
        recipe.Instructions.Should().HaveCount(3);

        // ½–1 tsk chiliflakes
        var chili = recipe.Ingredients.First(i => i.Name.Contains("chiliflakes"));
        chili.Quantity.Should().Be(0.5m);
        chili.QuantityMax.Should().Be(1.0m);
        chili.Unit.Should().Be("tsk");
        chili.Comment.Should().BeNull(); // Comment is not polluted with interval note

        // 1 förp kikärtor (à 380 g, avrunna)
        var kikartor = recipe.Ingredients.First(i => i.Name.Contains("kikärtor"));
        kikartor.Comment.Should().Be("à 380 g, avrunna");
    }

    [Fact]
    public void Test12_TwoRecipesWithStycken_ShouldParseServingsAndUnit_AndAvoidSecondRecipe()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test12_TwoRecipesWithStycken);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Chokladmuffins");
        recipe.Servings.Should().Be(12);
        recipe.ServingsUnit.Should().Be("stycken");
        recipe.Ingredients.Should().HaveCount(5);
        recipe.Instructions.Should().HaveCount(3);

        // Multiple recipes warning
        result.IsUncertain.Should().BeTrue();
        result.WarningMessage.Should().Contain("flera recept");

        // Bananbröd content must not leak
        recipe.Ingredients.Should().NotContain(i => i.Name.Contains("banan"));
        recipe.Instructions.Should().NotContain(s => s.InstructionText.Contains("Mosa bananerna"));
    }

    [Fact]
    public void Test13_FemTipsForEttBattreKok_ShouldBeRejectedCleanly()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test13_FemTipsForEttBattreKok);

        result.Success.Should().BeFalse();
        result.DraftRecipe.Should().BeNull();
        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
        result.ErrorMessage.Should().Contain("Inget recept kunde identifieras");
    }

    [Fact]
    public void Test14_MinimalRecipeWithoutSectionHeaders_ShouldParseTitleIngredientsAndInstructions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test14_SnabbTomatpasta);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Snabb tomatpasta");
        recipe.Ingredients.Should().HaveCount(4);
        recipe.Instructions.Should().HaveCount(3);

        // Title must not appear among ingredients
        recipe.Ingredients.Should().NotContain(i => i.Name.Contains("Snabb tomatpasta") || i.RawText.Contains("Snabb tomatpasta"));

        // Instructions parsed correctly
        recipe.Instructions[0].InstructionText.Should().Be("Koka pastan i saltat vatten.");
        recipe.Instructions[1].InstructionText.Should().Be("Hacka tomaterna och fräs dem i oljan.");
        recipe.Instructions[2].InstructionText.Should().Be("Blanda med pastan och toppa med basilika.");

        // Clean recipe without false quality warnings
        result.WarningMessage.Should().BeNull();
    }

    [Fact]
    public void Test14_Regression_EnkelVitloksdipp_ShouldParseCorrectly()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test14_Regression_EnkelVitloksdipp);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Enkel vitlöksdipp");
        recipe.Ingredients.Should().HaveCount(4);
        recipe.Instructions.Should().HaveCount(3);

        recipe.Ingredients.Should().NotContain(i => i.Name.Contains("Enkel vitlöksdipp"));

        recipe.Instructions[0].InstructionText.Should().Be("Pressa vitlöken.");
        recipe.Instructions[1].InstructionText.Should().Be("Blanda med crème fraiche.");
        recipe.Instructions[2].InstructionText.Should().Be("Smaka av med salt och peppar.");

        result.WarningMessage.Should().BeNull();
    }

    [Fact]
    public void NegativeTest1_BlogAdviceWithFoodWords_ShouldBeRejectedCleanly()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.NegativeTest1_BlogAdviceWithFoodWords);

        result.Success.Should().BeFalse();
        result.DraftRecipe.Should().BeNull();
        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
        result.ErrorMessage.Should().Contain("Inget recept kunde identifieras");
    }

    [Fact]
    public void NegativeTest2_HouseholdTipsNumberedList_ShouldBeRejectedCleanly()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.NegativeTest2_HouseholdTipsNumberedList);

        result.Success.Should().BeFalse();
        result.DraftRecipe.Should().BeNull();
        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
        result.ErrorMessage.Should().Contain("Inget recept kunde identifieras");
    }

    [Fact]
    public void Test20_UgnsrostadeMorotter_ShouldParseAllIngredientsAndInstructions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test20_UgnsrostadeMorotter);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Ugnsrostade morötter");
        recipe.Servings.Should().Be(4);
        recipe.ServingsUnit.Should().Be("portioner");

        // Exactly 6 ingredients
        recipe.Ingredients.Should().HaveCount(6);
        recipe.Ingredients[0].Name.Should().Be("morötter");
        recipe.Ingredients[0].Quantity.Should().Be(500m);
        recipe.Ingredients[0].Unit.Should().Be("g");

        var oil = recipe.Ingredients.First(i => i.Name.Contains("olivolja"));
        oil.Quantity.Should().Be(1m);
        oil.QuantityMax.Should().Be(2m);
        oil.Unit.Should().Be("msk");

        var salt = recipe.Ingredients.First(i => i.Name.Contains("flingsalt"));
        salt.Quantity.Should().Be(0.5m);
        salt.Unit.Should().Be("tsk");

        recipe.Ingredients.Should().Contain(i => i.Name.Contains("honung"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("timjan"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Svartpeppar") || i.Name.Contains("svartpeppar"));

        // Exactly 4 instructions in correct order
        recipe.Instructions.Should().HaveCount(4);
        recipe.Instructions[0].InstructionText.Should().Be("Sätt ugnen på 225 grader.");
        recipe.Instructions[1].InstructionText.Should().Be("Skala morötterna och dela dem på längden.");
        recipe.Instructions[2].InstructionText.Should().Be("Blanda morötterna med olja, honung och kryddor.");
        recipe.Instructions[3].InstructionText.Should().Be("Rosta i ugnen i 20 minuter.");
    }

    [Fact]
    public void Test20B_UgnsrostadeMorotter_Reordered_ShouldParseAllIngredientsAndInstructions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test20B_UgnsrostadeMorotter_Reordered);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Ugnsrostade morötter");
        recipe.Servings.Should().Be(4);
        recipe.ServingsUnit.Should().Be("portioner");

        // Exactly 6 ingredients
        recipe.Ingredients.Should().HaveCount(6);
        recipe.Ingredients[0].Name.Should().Be("morötter");
        recipe.Ingredients[0].Quantity.Should().Be(500m);
        recipe.Ingredients[0].Unit.Should().Be("g");

        var oil = recipe.Ingredients.First(i => i.Name.Contains("olivolja"));
        oil.Quantity.Should().Be(1m);
        oil.QuantityMax.Should().Be(2m);
        oil.Unit.Should().Be("msk");

        var salt = recipe.Ingredients.First(i => i.Name.Contains("flingsalt"));
        salt.Quantity.Should().Be(0.5m);
        salt.Unit.Should().Be("tsk");

        recipe.Ingredients.Should().Contain(i => i.Name.Contains("honung"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("timjan"));
        recipe.Ingredients.Should().Contain(i => i.Name.Contains("Svartpeppar") || i.Name.Contains("svartpeppar"));

        // Exactly 4 instructions in correct order
        recipe.Instructions.Should().HaveCount(4);
        recipe.Instructions[0].InstructionText.Should().Be("Skala morötterna och dela dem på längden.");
        recipe.Instructions[1].InstructionText.Should().Be("Sätt ugnen på 225 grader.");
        recipe.Instructions[2].InstructionText.Should().Be("Blanda morötterna med olja, honung och kryddor.");
        recipe.Instructions[3].InstructionText.Should().Be("Rosta i ugnen i 20 minuter.");
    }

    [Fact]
    public void MetamorphicTest_InstructionPermutation_ShouldPreserveAllMetadataAndIngredients_AndFollowNewOrder()
    {
        // Metamorphic test: changing only the order of two valid instruction steps
        // must preserve title, servings, ingredients, and instruction count, while updating order.
        const string baseRecipe = """
            Stekta äpplen med kanel, 2 portioner

            4 äpplen
            2 msk smör
            1 msk socker
            1 tsk kanel

            Skär äpplena i klyftor.
            Smält smöret i en stekpanna.
            Stek äpplena mjuka med socker och kanel.
            """;

        const string permutedRecipe = """
            Stekta äpplen med kanel, 2 portioner

            4 äpplen
            2 msk smör
            1 msk socker
            1 tsk kanel

            Smält smöret i en stekpanna.
            Skär äpplena i klyftor.
            Stek äpplena mjuka med socker och kanel.
            """;

        var baseResult = _parser.Parse(baseRecipe);
        var permutedResult = _parser.Parse(permutedRecipe);

        baseResult.Success.Should().BeTrue();
        permutedResult.Success.Should().BeTrue();

        var baseR = baseResult.DraftRecipe!;
        var permR = permutedResult.DraftRecipe!;

        // Metamorphic invariants:
        permR.Title.Should().Be(baseR.Title);
        permR.Servings.Should().Be(baseR.Servings);
        permR.ServingsUnit.Should().Be(baseR.ServingsUnit);
        permR.Ingredients.Should().HaveCount(baseR.Ingredients.Count);
        for (int i = 0; i < baseR.Ingredients.Count; i++)
        {
            permR.Ingredients[i].Name.Should().Be(baseR.Ingredients[i].Name);
            permR.Ingredients[i].Quantity.Should().Be(baseR.Ingredients[i].Quantity);
            permR.Ingredients[i].Unit.Should().Be(baseR.Ingredients[i].Unit);
        }
        permR.Instructions.Should().HaveCount(baseR.Instructions.Count);

        // Permuted order check:
        permR.Instructions[0].InstructionText.Should().Be(baseR.Instructions[1].InstructionText);
        permR.Instructions[1].InstructionText.Should().Be(baseR.Instructions[0].InstructionText);
        permR.Instructions[2].InstructionText.Should().Be(baseR.Instructions[2].InstructionText);
    }

    [Fact]
    public void BlankLinePermutationTest_VariedBlankLines_ShouldYieldIdenticalRecipe()
    {
        // Variant with multiple blank lines, no blank line after title, and trailing empty lines
        const string variedWhitespaceRecipe = """
            Ugnsrostade morötter, 4 portioner
            500 g morötter
            1–2 msk olivolja
            ½ tsk flingsalt
            1 tsk honung
            Färsk timjan
            Svartpeppar


            Sätt ugnen på 225 grader.
            Skala morötterna och dela dem på längden.
            Blanda morötterna med olja, honung och kryddor.
            Rosta i ugnen i 20 minuter.



            """;

        var standardResult = _parser.Parse(RecipeManualTestFixtures.Test20_UgnsrostadeMorotter);
        var variedResult = _parser.Parse(variedWhitespaceRecipe);

        standardResult.Success.Should().BeTrue();
        variedResult.Success.Should().BeTrue();

        var std = standardResult.DraftRecipe!;
        var varR = variedResult.DraftRecipe!;

        varR.Title.Should().Be(std.Title);
        varR.Servings.Should().Be(std.Servings);
        varR.ServingsUnit.Should().Be(std.ServingsUnit);
        varR.Ingredients.Should().HaveCount(std.Ingredients.Count);
        varR.Instructions.Should().HaveCount(std.Instructions.Count);

        for (int i = 0; i < std.Ingredients.Count; i++)
        {
            varR.Ingredients[i].Name.Should().Be(std.Ingredients[i].Name);
            varR.Ingredients[i].Quantity.Should().Be(std.Ingredients[i].Quantity);
            varR.Ingredients[i].QuantityMax.Should().Be(std.Ingredients[i].QuantityMax);
        }

        for (int i = 0; i < std.Instructions.Count; i++)
        {
            varR.Instructions[i].InstructionText.Should().Be(std.Instructions[i].InstructionText);
        }
    }

    [Fact]
    public void Test21_IngredientHeaderWithoutInstructionHeader_ShouldExtract3IngredientsAnd3Instructions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test21_EnkelTomatsoppa);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Enkel tomatsoppa");
        recipe.Ingredients.Should().HaveCount(3);
        recipe.Ingredients[0].Name.Should().Contain("krossade tomater");
        recipe.Ingredients[1].Name.Should().Be("vatten");
        recipe.Ingredients[2].Name.Should().Be("Salt");

        recipe.Instructions.Should().HaveCount(3);
        recipe.Instructions[0].InstructionText.Should().Be("Koka upp tomater och vatten.");
        recipe.Instructions[1].InstructionText.Should().Be("Låt sjuda i 10 minuter.");
        recipe.Instructions[2].InstructionText.Should().Be("Smaka av med salt.");
    }

    [Fact]
    public void Test22_DescriptivePassiveInstructions_ShouldExtract5IngredientsAndAll4Instructions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test22_EnkelPotatisgratang);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Enkel potatisgratäng");
        recipe.Ingredients.Should().HaveCount(5);

        recipe.Instructions.Should().HaveCount(4);
        recipe.Instructions[0].InstructionText.Should().Be("Potatisen ska skivas tunt.");
        recipe.Instructions[1].InstructionText.Should().Be("Grädden blandas med pressad vitlök.");
        recipe.Instructions[2].InstructionText.Should().Be("Allt läggs i en ugnsform.");
        recipe.Instructions[3].InstructionText.Should().Be("Gratängen gräddas i 200 grader i 45 minuter.");
    }

    [Fact]
    public void Test23_InstructionStartingWithNumber_ShouldPreserveLeadingNumberAndAll3Instructions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test23_KramigSvamppasta);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Krämig svamppasta");
        recipe.Servings.Should().Be(2);
        recipe.Ingredients.Should().HaveCount(3);

        recipe.Instructions.Should().HaveCount(3);
        recipe.Instructions[0].InstructionText.Should().Be("Koka pastan enligt anvisningen.");
        recipe.Instructions[1].InstructionText.Should().Be("2 minuter senare tillsätter du grädden.");
        recipe.Instructions[2].InstructionText.Should().Be("Blanda allt och servera.");
    }

    [Fact]
    public void Test24_NonRecipeWithPortionsAndIngredientHeader_ShouldBeRejectedAsNonRecipe()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test24_MiddagstipsFor4Portioner);

        result.Success.Should().BeFalse();
        result.DraftRecipe.Should().BeNull();
        result.FailureReason.Should().Be(RecipeImportFailureReason.NoRecipeData);
    }

    [Fact]
    public void Test25_InstructionHeaderWithoutIngredientHeader_ShouldExtract4IngredientsAnd3Instructions()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test25_SnabbYoghurtsas);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Snabb yoghurtsås");
        recipe.Ingredients.Should().HaveCount(4);
        recipe.Instructions.Should().HaveCount(3);
        recipe.Instructions[0].InstructionText.Should().Be("Pressa vitlöken.");
        recipe.Instructions[1].InstructionText.Should().Be("Blanda alla ingredienser.");
        recipe.Instructions[2].InstructionText.Should().Be("Smaka av med salt och peppar.");
    }

    [Fact]
    public void Test26_InstructionStartingWithQuantity_ShouldPreserveQuantityInstructionInInstructionBlock()
    {
        var result = _parser.Parse(RecipeManualTestFixtures.Test26_EnkelRisotto);

        result.Success.Should().BeTrue();
        result.DraftRecipe.Should().NotBeNull();
        var recipe = result.DraftRecipe!;

        recipe.Title.Should().Be("Enkel risotto");
        recipe.Servings.Should().Be(2);
        recipe.Ingredients.Should().HaveCount(3);

        recipe.Instructions.Should().HaveCount(3);
        recipe.Instructions[0].InstructionText.Should().Be("Smält smöret i en kastrull.");
        recipe.Instructions[1].InstructionText.Should().Be("1 dl vatten tillsätts om riset blir för torrt.");
        recipe.Instructions[2].InstructionText.Should().Be("Rör om tills riset är krämigt.");
    }

    [Fact]
    public void Section4A_IngredientHeaderWithoutInstructionHeader_NoBlankLineAndPassiveFirstStep_ShouldParseCorrectly()
    {
        const string text = """
            Enkel tomatsoppa
            Ingredienser:
            400 g krossade tomater
            2 dl vatten
            Färsk timjan
            Tomaterna kokas upp med vatten.
            Soppan sjuder i 10 minuter.
            Smaka av med salt.
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;
        recipe.Title.Should().Be("Enkel tomatsoppa");
        recipe.Ingredients.Should().HaveCount(3);
        recipe.Ingredients[2].Name.Should().Contain("timjan");

        recipe.Instructions.Should().HaveCount(3);
        recipe.Instructions[0].InstructionText.Should().Be("Tomaterna kokas upp med vatten.");
        recipe.Instructions[1].InstructionText.Should().Be("Soppan sjuder i 10 minuter.");
        recipe.Instructions[2].InstructionText.Should().Be("Smaka av med salt.");
    }

    [Fact]
    public void Section4B_InstructionsStartingWithNumbers_ShouldDistinguishStepNumber_TimePhrase_AndQuantityInstruction()
    {
        const string text = """
            Risotto med svamp, 2 portioner

            Ingredienser:
            2 dl risottoris
            150 g svamp
            1 dl grädde

            Gör så här:
            1. Fräs svampen i en kastrull.
            2. Koka pastan.
            2 minuter senare tillsätter du grädden.
            1 dl vatten tillsätts om riset blir för torrt.
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        var recipe = result.DraftRecipe!;
        recipe.Ingredients.Should().HaveCount(3);
        recipe.Instructions.Should().HaveCount(4);

        // "2. Koka pastan." -> numbered step with text "Koka pastan."
        recipe.Instructions[1].StepNumber.Should().Be(2);
        recipe.Instructions[1].InstructionText.Should().Be("Koka pastan.");

        // "2 minuter senare tillsätter du grädden." -> unnumbered step preserving leading "2"
        recipe.Instructions[2].InstructionText.Should().Be("2 minuter senare tillsätter du grädden.");

        // "1 dl vatten tillsätts om riset blir för torrt." -> instruction, not ingredient
        recipe.Instructions[3].InstructionText.Should().Be("1 dl vatten tillsätts om riset blir för torrt.");
    }

    [Fact]
    public void Section4C_LegitimateIncompleteRecipe_WithQuantifiedIngredientsOnly_ShouldStillBeAcceptedWithWarning()
    {
        const string text = """
            Pannkakssmet, 4 portioner

            Ingredienser:
            2,5 dl vetemjöl
            6 dl mjölk
            3 ägg
            """;

        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        result.IsUncertain.Should().BeTrue();
        result.WarningMessage.Should().Contain("Inga tillagningssteg kunde identifieras automatiskt");
        result.DraftRecipe!.Ingredients.Should().HaveCount(3);
        result.DraftRecipe.Instructions.Should().BeEmpty();
    }
}

