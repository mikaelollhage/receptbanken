using FluentAssertions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class ShoppingListAggregatorTests
{
    private readonly ShoppingListAggregator _aggregator = new(new ServingCalculator());

    [Fact]
    public void MergeItems_WhenSameNameAndCompatibleUnit_ShouldSumQuantities()
    {
        var existing = new List<ShoppingListItem>
        {
            new() { Id = 1, Name = "Mjölk", Quantity = 2m, Unit = "dl", RawText = "2 dl Mjölk" }
        };

        var incoming = new List<ShoppingListItem>
        {
            new() { Name = "mjölk", Quantity = 3.5m, Unit = "DL" }
        };

        var merged = _aggregator.MergeItems(existing, incoming);

        merged.Should().HaveCount(1);
        merged[0].Quantity.Should().Be(5.5m);
        merged[0].Unit.Should().Be("dl");
        merged[0].RawText.Should().Be("5,5 dl Mjölk");
    }

    [Fact]
    public void MergeItems_WhenSameNameButDifferentUnits_ShouldKeepSeparateEntries()
    {
        var existing = new List<ShoppingListItem>
        {
            new() { Id = 1, Name = "Smör", Quantity = 100m, Unit = "g", RawText = "100 g Smör" }
        };

        var incoming = new List<ShoppingListItem>
        {
            new() { Name = "Smör", Quantity = 2m, Unit = "msk" }
        };

        var merged = _aggregator.MergeItems(existing, incoming);

        merged.Should().HaveCount(2);
        merged[0].Unit.Should().Be("g");
        merged[1].Unit.Should().Be("msk");
    }

    [Fact]
    public void MergeItems_WhenExistingItemIsAlreadyChecked_ShouldCreateNewUncheckedItem()
    {
        var existing = new List<ShoppingListItem>
        {
            new() { Id = 1, Name = "Ägg", Quantity = 2m, Unit = "st", IsChecked = true }
        };

        var incoming = new List<ShoppingListItem>
        {
            new() { Name = "Ägg", Quantity = 4m, Unit = "stycken", IsChecked = false }
        };

        var merged = _aggregator.MergeItems(existing, incoming);

        merged.Should().HaveCount(2);
        merged[0].IsChecked.Should().BeTrue();
        merged[0].Quantity.Should().Be(2m);
        merged[1].IsChecked.Should().BeFalse();
        merged[1].Quantity.Should().Be(4m);
        merged[1].Unit.Should().Be("st");
    }

    [Theory]
    [InlineData(4, null, "portins", "4 portins")]
    [InlineData(4, null, "kycklingfiléer", "4 kycklingfiléer")]
    [InlineData(0.5, "dl", "vetemjöl", "0,5 dl vetemjöl")]
    [InlineData(2, "msk", "olivolja", "2 msk olivolja")]
    [InlineData(2, null, "vitlöksklyftor", "2 vitlöksklyftor")]
    [InlineData(1, "burk", "krossade tomater (à 400 g)", "1 burk krossade tomater (à 400 g)")]
    public void FormatRawText_ShouldMaintainProperSpacingAndSwedishDecimals(
        double quantity, string? unit, string name, string expected)
    {
        var formatted = _aggregator.FormatRawText((decimal)quantity, unit, name);
        formatted.Should().Be(expected);
    }

    // =========================================================================
    // DEL I1 – Enhetstester för normalisering och enheter (1–10)
    // =========================================================================

    [Fact]
    public void MergeItems_I1_1_SameVolumeUnit_ShouldSumQuantities()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "blåbär" },
                new ShoppingListItem { Quantity = 1m, Unit = "dl", Name = "blåbär" }
            ]);

        merged.Should().ContainSingle();
        merged[0].Quantity.Should().Be(3m);
        merged[0].Unit.Should().Be("dl");
        merged[0].Name.Should().Be("blåbär");
        merged[0].RawText.Should().Be("3 dl blåbär");
    }

    [Fact]
    public void MergeItems_I1_2_CompatibleWeightUnitsGramsAndKilograms_ShouldConvertAndSumToGrams()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 200m, Unit = "g", Name = "nötfärs" },
                new ShoppingListItem { Quantity = 0.5m, Unit = "kg", Name = "nötfärs" }
            ]);

        merged.Should().ContainSingle();
        merged[0].Quantity.Should().Be(700m);
        merged[0].Unit.Should().Be("g");
        merged[0].Name.Should().Be("nötfärs");
        merged[0].RawText.Should().Be("700 g nötfärs");
    }

    [Fact]
    public void MergeItems_I1_3_CompatibleKitchenVolumeMeasuresMskAndTsk_ShouldConvertAndSumToMl()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 1m, Unit = "msk", Name = "soja" },
                new ShoppingListItem { Quantity = 2m, Unit = "tsk", Name = "soja" }
            ]);

        merged.Should().ContainSingle();
        merged[0].Quantity.Should().Be(25m);
        merged[0].Unit.Should().Be("ml");
        merged[0].Name.Should().Be("soja");
        merged[0].RawText.Should().Be("25 ml soja");
    }

    [Fact]
    public void MergeItems_I1_4_SafeInflectionGulLokAndGulaLokar_ShouldMergeToCanonicalSingular()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 2m, Unit = "st", Name = "gul lök" },
                new ShoppingListItem { Quantity = 1m, Unit = "st", Name = "gula lökar" }
            ]);

        merged.Should().ContainSingle();
        merged[0].Quantity.Should().Be(3m);
        merged[0].Unit.Should().Be("st");
        merged[0].Name.Should().Be("gul lök");
        merged[0].RawText.Should().Be("3 st gul lök");
    }

    [Fact]
    public void MergeItems_I1_5_IncompatibleUnitsPiecesAndGrams_ShouldKeepSeparateEntries()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 2m, Unit = "st", Name = "tomater" },
                new ShoppingListItem { Quantity = 200m, Unit = "g", Name = "tomater" }
            ]);

        merged.Should().HaveCount(2);
        merged[0].RawText.Should().Be("2 st tomater");
        merged[1].RawText.Should().Be("200 g tomater");
    }

    [Fact]
    public void MergeItems_I1_6_IncompatibleUnitsGramsAndKlick_ShouldKeepSeparateEntries()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 100m, Unit = "g", Name = "smör" },
                new ShoppingListItem { Quantity = 1m, Unit = "klick", Name = "smör" }
            ]);

        merged.Should().HaveCount(2);
        merged[0].RawText.Should().Be("100 g smör");
        merged[1].RawText.Should().Be("1 klick smör");
    }

    [Fact]
    public void MergeItems_I1_7_DistinctDairyProductsVispgraddeAndMatlagningsgradde_ShouldNotMerge()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "vispgrädde" },
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "matlagningsgrädde" },
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "syrad grädde" }
            ]);

        merged.Should().HaveCount(3);
    }

    [Fact]
    public void MergeItems_I1_8_GraddeAndGraddfil_ShouldNotMerge()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "grädde" },
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "gräddfil" }
            ]);

        merged.Should().HaveCount(2);
    }

    [Fact]
    public void MergeItems_I1_9_GulLokAndRodLok_ShouldNotMerge()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 1m, Unit = "st", Name = "gul lök" },
                new ShoppingListItem { Quantity = 1m, Unit = "st", Name = "röd lök" }
            ]);

        merged.Should().HaveCount(2);
    }

    [Fact]
    public void MergeItems_I1_10_FarskAndTorkadKoriander_AndOtherProductDistinctions_ShouldNotMerge()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 1m, Unit = "kruka", Name = "färsk koriander" },
                new ShoppingListItem { Quantity = 1m, Unit = "tsk", Name = "torkad koriander" },
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "färska blåbär" },
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "frysta blåbär" },
                new ShoppingListItem { Quantity = 400m, Unit = "g", Name = "falukorv" },
                new ShoppingListItem { Quantity = 400m, Unit = "g", Name = "chorizo" },
                new ShoppingListItem { Quantity = 300m, Unit = "g", Name = "spaghetti" },
                new ShoppingListItem { Quantity = 300m, Unit = "g", Name = "lasagneplattor" }
            ]);

        merged.Should().HaveCount(8);
    }

    // =========================================================================
    // DEL I2 – Enhetstester för kommentarer, förberedelse och ospecificerade mängder (11–18)
    // =========================================================================

    [Fact]
    public void MergeItems_I2_11_QuantifiedPlusUnquantifiedBlueberries_ShouldKeepQuantityAndAddExtraComment()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 2m, Unit = "dl", Name = "blåbär" },
                new ShoppingListItem { Name = "blåbär till garnering" }
            ]);

        merged.Should().ContainSingle();
        merged[0].Name.Should().Be("blåbär");
        merged[0].Quantity.Should().Be(2m);
        merged[0].Unit.Should().Be("dl");
        merged[0].RawText.Should().Be("2 dl blåbär");
        merged[0].Comment.Should().Be("Extra blåbär till garnering, mängd ej angiven");
    }

    [Fact]
    public void MergeItems_I2_12_QuantifiedCreamPlusWhippedCreamForGarnish_ShouldMergeToCreamWithExtraComment()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 3m, Unit = "dl", Name = "grädde" },
                new ShoppingListItem { Name = "vispad grädde till garnering" }
            ]);

        merged.Should().ContainSingle();
        merged[0].Name.Should().Be("grädde");
        merged[0].Quantity.Should().Be(3m);
        merged[0].Unit.Should().Be("dl");
        merged[0].RawText.Should().Be("3 dl grädde");
        merged[0].Comment.Should().Be("Extra vispad grädde till garnering, mängd ej angiven");
    }

    [Fact]
    public void MergeItems_I2_13_TwoUnquantifiedBlueberriesForGarnishAndServing_ShouldMergeWithCombinedComment()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Name = "blåbär till garnering" },
                new ShoppingListItem { Name = "blåbär till servering" }
            ]);

        merged.Should().ContainSingle();
        merged[0].Name.Should().Be("blåbär");
        merged[0].Quantity.Should().BeNull();
        merged[0].Unit.Should().BeNull();
        merged[0].RawText.Should().Be("blåbär");
        merged[0].Comment.Should().Be("Till garnering och servering, mängd ej angiven");
    }

    [Fact]
    public void CreateFromRecipeIngredient_I2_14_PreparationSmalt_ShouldBePreservedInComment()
    {
        var ingredient = new Ingredient
        {
            Quantity = 100m,
            Unit = "g",
            Name = "smör",
            Preparation = "smält",
            RawText = "100 g smör, smält"
        };

        var item = _aggregator.CreateFromRecipeIngredient(ingredient, baseServings: 4, targetServings: 4);

        item.Name.Should().Be("smör");
        item.Quantity.Should().Be(100m);
        item.Unit.Should().Be("g");
        item.Comment.Should().Be("smält");
        item.RawText.Should().Be("100 g smör");
    }

    [Fact]
    public void CreateFromRecipeIngredient_I2_15_PackageSizeComment_ShouldBePreserved()
    {
        var ingredient = new Ingredient
        {
            Quantity = 1m,
            Unit = "burk",
            Name = "krossade tomater",
            Comment = "à 400 g",
            RawText = "1 burk krossade tomater (à 400 g)"
        };

        var item = _aggregator.CreateFromRecipeIngredient(ingredient, baseServings: 4, targetServings: 4);

        item.Name.Should().Be("krossade tomater");
        item.Quantity.Should().Be(1m);
        item.Unit.Should().Be("burk");
        item.Comment.Should().Be("à 400 g");
        item.RawText.Should().Be("1 burk krossade tomater");
    }

    [Fact]
    public void MergeItems_I2_16_DifferentPackageSizeComments_ShouldNotMergeSilently()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 1m, Unit = "burk", Name = "krossade tomater", Comment = "à 400 g" },
                new ShoppingListItem { Quantity = 1m, Unit = "burk", Name = "krossade tomater", Comment = "à 200 g" }
            ]);

        merged.Should().HaveCount(2);
        merged[0].Comment.Should().Be("à 400 g");
        merged[1].Comment.Should().Be("à 200 g");
    }

    [Fact]
    public void CreateFromRecipeIngredient_I2_17_GroupHeaderTillGarnering_ShouldBePreservedWhileStructuralHeaderIgnored()
    {
        var garnishIngredient = new Ingredient
        {
            Name = "blåbär",
            GroupHeader = "Till garnering",
            RawText = "blåbär"
        };
        var sauceIngredient = new Ingredient
        {
            Quantity = 2m,
            Unit = "dl",
            Name = "grädde",
            GroupHeader = "Sås",
            RawText = "2 dl grädde"
        };

        var garnishItem = _aggregator.CreateFromRecipeIngredient(garnishIngredient, 4, 4);
        var sauceItem = _aggregator.CreateFromRecipeIngredient(sauceIngredient, 4, 4);

        garnishItem.Name.Should().Be("blåbär");
        garnishItem.Comment.Should().Be("Till garnering");

        sauceItem.Name.Should().Be("grädde");
        sauceItem.Comment.Should().BeNull("rena strukturrubriker som 'Sås' eller 'Fyllning' ska inte skräpa ner varje inköpsrad");
    }

    [Fact]
    public void CreateFromRecipeIngredient_And_MergeItems_I2_18_QuantityMaxInterval_ShouldScaleAndSumProperly()
    {
        var ingredient = new Ingredient
        {
            Quantity = 1m,
            QuantityMax = 2m,
            Unit = "tsk",
            Name = "spiskummin",
            RawText = "1–2 tsk spiskummin"
        };

        // Scale from 4 to 8 servings -> 2–4 tsk spiskummin
        var scaled = _aggregator.CreateFromRecipeIngredient(ingredient, baseServings: 4, targetServings: 8);
        scaled.Quantity.Should().Be(2m);
        scaled.QuantityMax.Should().Be(4m);
        scaled.Unit.Should().Be("tsk");
        scaled.Name.Should().Be("spiskummin");
        scaled.RawText.Should().Be("2–4 tsk spiskummin");

        // Sum interval 1–2 tsk + 1 tsk -> 2–3 tsk
        var itemRange = _aggregator.CreateFromRecipeIngredient(ingredient, baseServings: 4, targetServings: 4);
        var itemSingle = new ShoppingListItem { Quantity = 1m, Unit = "tsk", Name = "spiskummin" };

        var merged = _aggregator.MergeItems([], [itemRange, itemSingle]);
        merged.Should().ContainSingle();
        merged[0].Quantity.Should().Be(2m);
        merged[0].QuantityMax.Should().Be(3m);
        merged[0].RawText.Should().Be("2–3 tsk spiskummin");
    }

    // =========================================================================
    // DEL I4 – Regressionsfall från faktisk användning
    // =========================================================================

    [Fact]
    public void Regression_I4_BlueberryAndWhippedCreamGarnish_WhenInNameCommentPreparationOrGroupHeader_ShouldPreserveAllInformation()
    {
        // Variant 1: "till garnering" in Ingredient.Name (via SwedishIngredientParser)
        var parser = new SwedishIngredientParser();
        var rawLines = new[]
        {
            "2 st gelatinblad",
            "2 dl blåbär",
            "1 msk pressad citron",
            "3 dl grädde",
            "blåbär till garnering",
            "vispad grädde till garnering"
        };

        var parsedIngredients = rawLines
            .Select((line, idx) => parser.Parse(line, idx + 1))
            .Select(ing => _aggregator.CreateFromRecipeIngredient(ing, 4, 4))
            .ToList();

        var mergedFromName = _aggregator.MergeItems([], parsedIngredients);
        AssertBlueberryAndCreamRegressionResult(mergedFromName);

        // Variant 2: "till garnering" in Ingredient.Comment
        var fromComment = new[]
        {
            new Ingredient { Quantity = 2m, Unit = "st", Name = "gelatinblad", RawText = "2 st gelatinblad" },
            new Ingredient { Quantity = 2m, Unit = "dl", Name = "blåbär", RawText = "2 dl blåbär" },
            new Ingredient { Quantity = 1m, Unit = "msk", Name = "pressad citron", RawText = "1 msk pressad citron" },
            new Ingredient { Quantity = 3m, Unit = "dl", Name = "grädde", RawText = "3 dl grädde" },
            new Ingredient { Name = "blåbär", Comment = "till garnering", RawText = "blåbär (till garnering)" },
            new Ingredient { Name = "vispad grädde", Comment = "till garnering", RawText = "vispad grädde (till garnering)" }
        }.Select(i => _aggregator.CreateFromRecipeIngredient(i, 4, 4)).ToList();

        AssertBlueberryAndCreamRegressionResult(_aggregator.MergeItems([], fromComment));

        // Variant 3: "till garnering" in Ingredient.Preparation
        var fromPreparation = new[]
        {
            new Ingredient { Quantity = 2m, Unit = "st", Name = "gelatinblad", RawText = "2 st gelatinblad" },
            new Ingredient { Quantity = 2m, Unit = "dl", Name = "blåbär", RawText = "2 dl blåbär" },
            new Ingredient { Quantity = 1m, Unit = "msk", Name = "pressad citron", RawText = "1 msk pressad citron" },
            new Ingredient { Quantity = 3m, Unit = "dl", Name = "grädde", RawText = "3 dl grädde" },
            new Ingredient { Name = "blåbär", Preparation = "till garnering", RawText = "blåbär, till garnering" },
            new Ingredient { Name = "vispad grädde", Preparation = "till garnering", RawText = "vispad grädde, till garnering" }
        }.Select(i => _aggregator.CreateFromRecipeIngredient(i, 4, 4)).ToList();

        AssertBlueberryAndCreamRegressionResult(_aggregator.MergeItems([], fromPreparation));

        // Variant 4: "Till garnering" in Ingredient.GroupHeader
        var fromGroupHeader = new[]
        {
            new Ingredient { Quantity = 2m, Unit = "st", Name = "gelatinblad", GroupHeader = "Pannacotta", RawText = "2 st gelatinblad" },
            new Ingredient { Quantity = 2m, Unit = "dl", Name = "blåbär", GroupHeader = "Pannacotta", RawText = "2 dl blåbär" },
            new Ingredient { Quantity = 1m, Unit = "msk", Name = "pressad citron", GroupHeader = "Pannacotta", RawText = "1 msk pressad citron" },
            new Ingredient { Quantity = 3m, Unit = "dl", Name = "grädde", GroupHeader = "Pannacotta", RawText = "3 dl grädde" },
            new Ingredient { Name = "blåbär", GroupHeader = "Till garnering", RawText = "blåbär" },
            new Ingredient { Name = "vispad grädde", GroupHeader = "Till garnering", RawText = "vispad grädde" }
        }.Select(i => _aggregator.CreateFromRecipeIngredient(i, 4, 4)).ToList();

        AssertBlueberryAndCreamRegressionResult(_aggregator.MergeItems([], fromGroupHeader));
    }

    private static void AssertBlueberryAndCreamRegressionResult(List<ShoppingListItem> items)
    {
        items.Should().HaveCount(4);

        var gelatin = items.Single(x => x.Name == "gelatinblad");
        gelatin.Quantity.Should().Be(2m);
        gelatin.Unit.Should().Be("st");
        gelatin.RawText.Should().Be("2 st gelatinblad");
        gelatin.Comment.Should().BeNull();

        var lemon = items.Single(x => x.Name == "pressad citron");
        lemon.Quantity.Should().Be(1m);
        lemon.Unit.Should().Be("msk");
        lemon.RawText.Should().Be("1 msk pressad citron");
        lemon.Comment.Should().BeNull();

        var blueberries = items.Single(x => x.Name == "blåbär");
        blueberries.Quantity.Should().Be(2m);
        blueberries.Unit.Should().Be("dl");
        blueberries.RawText.Should().Be("2 dl blåbär");
        blueberries.Comment.Should().Be("Extra blåbär till garnering, mängd ej angiven");

        var cream = items.Single(x => x.Name == "grädde");
        cream.Quantity.Should().Be(3m);
        cream.Unit.Should().Be("dl");
        cream.RawText.Should().Be("3 dl grädde");
        cream.Comment.Should().Be("Extra vispad grädde till garnering, mängd ej angiven");
    }

    // =========================================================================
    // ETAPP 3B.1A – Regressionstester för utskrivna svenska enhetsnamn
    // =========================================================================

    [Fact]
    public void MergeItems_3B1A_200gPlus05Kilo_ShouldNormalizeAndSumTo700g_ForBothStructuredAndLegacySavedRows()
    {
        // 1. When Unit is "kilo"
        var mergedStructured = _aggregator.MergeItems(
            [],
            [
                new ShoppingListItem { Quantity = 200m, Unit = "g", Name = "nötfärs" },
                new ShoppingListItem { Quantity = 0.5m, Unit = "kilo", Name = "nötfärs" }
            ]);

        mergedStructured.Should().ContainSingle();
        mergedStructured[0].Quantity.Should().Be(700m);
        mergedStructured[0].Unit.Should().Be("g");
        mergedStructured[0].Name.Should().Be("nötfärs");
        mergedStructured[0].RawText.Should().Be("700 g nötfärs");

        // 2. When an older saved Ingredient had Unit = null and Name = "kilo nötfärs"
        var legacyIngredient = new Ingredient
        {
            Quantity = 0.5m,
            Unit = null,
            Name = "kilo nötfärs",
            RawText = "0,5 kilo nötfärs"
        };
        var fromLegacyRecipe = _aggregator.CreateFromRecipeIngredient(legacyIngredient, 4, 4);
        fromLegacyRecipe.Unit.Should().Be("kg");
        fromLegacyRecipe.Name.Should().Be("nötfärs");

        var mergedWithLegacy = _aggregator.MergeItems(
            [new ShoppingListItem { Quantity = 200m, Unit = "gram", Name = "nötfärs" }],
            [fromLegacyRecipe]);

        mergedWithLegacy.Should().ContainSingle();
        mergedWithLegacy[0].Quantity.Should().Be(700m);
        mergedWithLegacy[0].Unit.Should().Be("g");
        mergedWithLegacy[0].Name.Should().Be("nötfärs");
        mergedWithLegacy[0].RawText.Should().Be("700 g nötfärs");
    }

    [Fact]
    public void MergeItems_3B1A_WrittenOutSwedishUnits_ShouldNormalizeAndConvertConsistently()
    {
        var merged = _aggregator.MergeItems(
            [],
            [
                // Weight: kilogram + hekto + gram -> 1000 + 200 + 50 = 1250 g
                new ShoppingListItem { Quantity = 1m, Unit = "kilogram", Name = "potatis" },
                new ShoppingListItem { Quantity = 2m, Unit = "hekto", Name = "potatis" },
                new ShoppingListItem { Quantity = 50m, Unit = "gram", Name = "potatis" },

                // Metric volume: liter + deciliter + centiliter -> 10 + 2 + 0,5 = 12,5 dl
                new ShoppingListItem { Quantity = 1m, Unit = "liter", Name = "mjölk" },
                new ShoppingListItem { Quantity = 2m, Unit = "deciliter", Name = "mjölk" },
                new ShoppingListItem { Quantity = 5m, Unit = "centiliter", Name = "mjölk" },

                // Kitchen volume: matsked + teskedar + kryddmått + milliliter -> 15 + 10 + 2 + 3 = 30 ml
                new ShoppingListItem { Quantity = 1m, Unit = "matsked", Name = "soja" },
                new ShoppingListItem { Quantity = 2m, Unit = "teskedar", Name = "soja" },
                new ShoppingListItem { Quantity = 2m, Unit = "kryddmått", Name = "soja" },
                new ShoppingListItem { Quantity = 3m, Unit = "milliliter", Name = "soja" },

                // Incompatible units remain separate: stycken vs gram
                new ShoppingListItem { Quantity = 2m, Unit = "stycken", Name = "tomater" },
                new ShoppingListItem { Quantity = 200m, Unit = "gram", Name = "tomater" }
            ]);

        merged.Should().HaveCount(5);

        var potatoes = merged.Single(x => x.Name == "potatis");
        potatoes.Quantity.Should().Be(1250m);
        potatoes.Unit.Should().Be("g");
        potatoes.RawText.Should().Be("1250 g potatis");

        var milk = merged.Single(x => x.Name == "mjölk");
        milk.Quantity.Should().Be(12.5m);
        milk.Unit.Should().Be("dl");
        milk.RawText.Should().Be("12,5 dl mjölk");

        var soy = merged.Single(x => x.Name == "soja");
        soy.Quantity.Should().Be(30m);
        soy.Unit.Should().Be("ml");
        soy.RawText.Should().Be("30 ml soja");

        merged.Where(x => x.Name == "tomater").Should().HaveCount(2);
    }
}

