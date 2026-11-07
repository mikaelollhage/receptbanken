using FluentAssertions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class WeeklyMenuGeneratorTests
{
    private sealed class FixedSequenceRandomProvider : IRandomProvider
    {
        private readonly double[] _values;
        private int _index;

        public FixedSequenceRandomProvider(params double[] values)
        {
            _values = values.Length > 0 ? values : [0.0];
        }

        public double NextDouble()
        {
            var val = _values[_index % _values.Length];
            _index++;
            return val;
        }
    }

    private static Recipe CreateRecipe(
        int id,
        string title,
        int[]? categoryIds = null,
        string[]? ingredientNames = null,
        bool isFavorite = false)
    {
        return new Recipe
        {
            Id = id,
            Title = title,
            Servings = 4,
            ServingsUnit = "portioner",
            IsFavorite = isFavorite,
            RecipeCategories = (categoryIds ?? []).Select(cid => new RecipeCategory
            {
                RecipeId = id,
                CategoryId = cid,
                Category = new Category { Id = cid, Name = $"Kat-{cid}" }
            }).ToList(),
            Ingredients = (ingredientNames ?? []).Select((name, idx) => new Ingredient
            {
                Id = idx + 1,
                RecipeId = id,
                OrderIndex = idx + 1,
                Name = name,
                Quantity = 400m,
                Unit = "g",
                RawText = $"400 g {name}"
            }).ToList()
        };
    }

    private static WeeklyMenu CreateEmptyMenuForWeek(DateOnly weekStart, int? defaultCategoryId = null)
    {
        var week = CalendarWeek.FromDate(weekStart);
        var menu = new WeeklyMenu
        {
            WeekStartDate = week.WeekStartDate,
            IsoYear = week.IsoYear,
            IsoWeek = week.IsoWeek,
            Status = WeeklyMenuStatus.Draft
        };

        for (int i = 0; i < 7; i++)
        {
            var day = new WeeklyMenuDay
            {
                DayIndex = i,
                Date = week.GetDateForDayIndex(i)
            };
            if (defaultCategoryId.HasValue)
            {
                day.SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = defaultCategoryId.Value });
            }
            menu.Days.Add(day);
        }

        return menu;
    }

    [Fact]
    public void GenerateWeek_WhenEnoughRecipesExist_ShouldSelectSevenUniqueRecipes()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42));
        var recipes = Enumerable.Range(1, 10)
            .Select(i => CreateRecipe(i, $"Recept {i}", categoryIds: [1]))
            .ToList();

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2), defaultCategoryId: 1);

        var result = generator.GenerateWeek(menu, recipes);

        result.AllDaysFilled.Should().BeTrue();
        result.FilledDaysCount.Should().Be(7);
        result.EmptyDaysCount.Should().Be(0);

        var assignedIds = menu.Days.Select(d => d.RecipeId).ToList();
        assignedIds.Should().AllSatisfy(id => id.Should().NotBeNull());
        assignedIds.Distinct().Should().HaveCount(7, "samma recept får inte dupliceras inom samma veckomeny");
    }

    [Fact]
    public void GenerateWeek_CategoryFilter_AndMultipleCategoriesWithOrSemantics_AndNoCategoriesSelected_ShouldWorkCorrectly()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(7));

        // Cat 1 = Vardagsmat, Cat 2 = Pasta, Cat 3 = Vegetariskt, Cat 4 = Soppa
        var r1 = CreateRecipe(1, "Vardagsgryta", categoryIds: [1]);
        var r2 = CreateRecipe(2, "Carbonara", categoryIds: [2]);
        var r3 = CreateRecipe(3, "Linssoppa", categoryIds: [3, 4]);
        var r4 = CreateRecipe(4, "Okategoriserad rätt", categoryIds: []);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        // Day 0: Only Cat 2 (Pasta) -> must pick r2
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 });
        // Day 1: Cat 1 OR Cat 3 -> can pick r1 or r3
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 3 });
        // Day 2: Cat 4 -> must pick r3 (so Day 1 must get r1!)
        menu.Days[2].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 4 });
        // Day 3: No categories selected -> any remaining recipe qualifies (must pick r4)
        // Days 4, 5, 6: Category 99 (no matching recipes)
        menu.Days[4].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        menu.Days[5].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        menu.Days[6].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });

        var result = generator.GenerateWeek(menu, [r1, r2, r3, r4]);

        menu.Days[0].RecipeId.Should().Be(2);
        menu.Days[1].RecipeId.Should().Be(1);
        menu.Days[2].RecipeId.Should().Be(3);
        menu.Days[3].RecipeId.Should().Be(4);
        menu.Days[4].RecipeId.Should().BeNull();
        menu.Days[4].StatusMessage.Should().Contain("Inga recept matchar");
        result.FilledDaysCount.Should().Be(4);
    }

    [Fact]
    public void GenerateWeek_WhenTooFewRecipesExist_ShouldLeaveEmptyDaysWithClearMessageAndNeverDuplicate()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(99));
        var recipes = new List<Recipe>
        {
            CreateRecipe(1, "Köttfärssås", categoryIds: [1]),
            CreateRecipe(2, "Korv stroganoff", categoryIds: [1]),
            CreateRecipe(3, "Pannkakor", categoryIds: [1])
        };

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2), defaultCategoryId: 1);

        var result = generator.GenerateWeek(menu, recipes);

        result.FilledDaysCount.Should().Be(3);
        result.EmptyDaysCount.Should().Be(4);

        var filledIds = menu.Days.Where(d => d.RecipeId.HasValue).Select(d => d.RecipeId!.Value).ToList();
        filledIds.Should().BeEquivalentTo([1, 2, 3]);

        var emptyDays = menu.Days.Where(d => !d.RecipeId.HasValue).ToList();
        emptyDays.Should().HaveCount(4);
        emptyDays.Should().AllSatisfy(d => d.StatusMessage.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void GenerateWeek_ShouldAvoidGreedyStarvation_WhenConstrainedDayNeedsSpecificRecipe()
    {
        // Even if random provider returns 0.0 (which would greedily pick Recipe 1 first on Day 0),
        // Day 1 ONLY accepts Recipe 1, while Day 0 accepts Recipe 1 or Recipe 2.
        // The generator must assign Recipe 2 to Day 0 and Recipe 1 to Day 1 so both days are filled!
        var generator = new WeeklyMenuGenerator(new FixedSequenceRandomProvider(0.0));

        var r1 = CreateRecipe(1, "Lyxpasta", categoryIds: [1, 2]);
        var r2 = CreateRecipe(2, "Köttbullar", categoryIds: [1]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 }); // Matches r1, r2
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 }); // Matches ONLY r1
        for (int i = 2; i < 7; i++)
        {
            menu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 }); // Empty
        }

        generator.GenerateWeek(menu, [r1, r2]);

        menu.Days[0].RecipeId.Should().Be(2);
        menu.Days[1].RecipeId.Should().Be(1);
    }

    [Fact]
    public void CalculateRecipeWeight_ShouldPenalizeRecentAndFrequentRecipes_WithoutPermanentlyExcludingThem()
    {
        var generator = new WeeklyMenuGenerator();
        var recipe = CreateRecipe(1, "Fiskgratäng", categoryIds: [1]);

        var targetMonday = new DateOnly(2026, 11, 2);
        var prevWeekMonday = targetMonday.AddDays(-7);
        var sixWeeksAgoMonday = targetMonday.AddDays(-42);

        var neverPlannedWeight = generator.CalculateRecipeWeight(recipe, history: null);

        var plannedSixWeeksAgoOnce = new RecipeHistoryStats(
            RecipeId: 1,
            TotalConfirmedOccurrences: 1,
            LastPlannedWeekStartDate: sixWeeksAgoMonday,
            WeeksSinceLastPlanned: 6,
            WasPlannedPreviousWeek: false);

        var plannedSixWeeksAgoOften = new RecipeHistoryStats(
            RecipeId: 1,
            TotalConfirmedOccurrences: 8,
            LastPlannedWeekStartDate: sixWeeksAgoMonday,
            WeeksSinceLastPlanned: 6,
            WasPlannedPreviousWeek: false);

        var plannedLastWeek = new RecipeHistoryStats(
            RecipeId: 1,
            TotalConfirmedOccurrences: 1,
            LastPlannedWeekStartDate: prevWeekMonday,
            WeeksSinceLastPlanned: 1,
            WasPlannedPreviousWeek: true);

        double weightSixWeeksOnce = generator.CalculateRecipeWeight(recipe, plannedSixWeeksAgoOnce);
        double weightSixWeeksOften = generator.CalculateRecipeWeight(recipe, plannedSixWeeksAgoOften);
        double weightLastWeek = generator.CalculateRecipeWeight(recipe, plannedLastWeek);

        neverPlannedWeight.Should().BeGreaterThan(weightSixWeeksOnce);
        weightSixWeeksOnce.Should().BeGreaterThan(weightSixWeeksOften);
        weightSixWeeksOften.Should().BeGreaterThan(weightLastWeek);
        weightLastWeek.Should().BeGreaterThanOrEqualTo(generator.Weights.MinimumWeight, "inga recept får uteslutas permanent");
    }

    [Fact]
    public void CalculateRecipeWeight_ShouldGiveNoBonusToFavoriteRecipes()
    {
        var generator = new WeeklyMenuGenerator();
        var regularRecipe = CreateRecipe(1, "Rätt A", categoryIds: [1], ingredientNames: ["kycklingfilé"], isFavorite: false);
        var favoriteRecipe = CreateRecipe(2, "Rätt B", categoryIds: [1], ingredientNames: ["kycklingfilé"], isFavorite: true);

        double wRegular = generator.CalculateRecipeWeight(regularRecipe, history: null, pantryIngredients: ["kycklingfilé"]);
        double wFavorite = generator.CalculateRecipeWeight(favoriteRecipe, history: null, pantryIngredients: ["kycklingfilé"]);

        wFavorite.Should().Be(wRegular, "favoritmarkerade recept ska inte få någon särskild bonus (E1)");
    }

    [Fact]
    public void PantryIngredientMatcher_And_CalculateRecipeWeight_ShouldMatchInflectionsAndSynonyms_AndIgnoreTrivialStaples()
    {
        var matcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(pantryMatcher: matcher);

        // Inflections & Synonyms per D2
        matcher.IsMatch("Kycklingfilé", "kycklingfiléer").Should().BeTrue();
        matcher.IsMatch("kycklingfiléer", "Kycklingfilé").Should().BeTrue();
        matcher.IsMatch("Köttfärs", "nötfärs").Should().BeTrue();
        matcher.IsMatch("Lax", "laxfilé").Should().BeTrue();
        matcher.IsMatch("laxfilé", "färsk lax").Should().BeTrue();

        // Negative cases: no aggressive fuzzy matching
        matcher.IsMatch("lök", "vitlök").Should().BeFalse();
        matcher.IsMatch("lök", "purjolök").Should().BeFalse();
        matcher.IsMatch("mjöl", "mjölk").Should().BeFalse();
        matcher.IsMatch("ris", "chorizo").Should().BeFalse();

        // Trivial staples should not match or give bonus
        matcher.IsMatch("salt", "salt").Should().BeFalse();
        matcher.IsMatch("vatten", "vatten").Should().BeFalse();

        var recipeWithSaltAndWater = CreateRecipe(1, "Enkel soppa", ingredientNames: ["salt", "vatten", "potatis"]);
        var recipeWithChickenAndSalmon = CreateRecipe(2, "Kycklingpanna", ingredientNames: ["kycklingfiléer"]);

        double baseWeightSaltWater = generator.CalculateRecipeWeight(recipeWithSaltAndWater, null, pantryIngredients: []);
        double pantryWeightSaltWater = generator.CalculateRecipeWeight(recipeWithSaltAndWater, null, pantryIngredients: ["salt", "vatten"]);
        pantryWeightSaltWater.Should().Be(baseWeightSaltWater, "salt och vatten ska inte öka poängen");

        double baseChicken = generator.CalculateRecipeWeight(recipeWithChickenAndSalmon, null, pantryIngredients: []);
        double matchedChicken = generator.CalculateRecipeWeight(recipeWithChickenAndSalmon, null, pantryIngredients: ["Kycklingfilé"]);
        matchedChicken.Should().BeGreaterThan(baseChicken);
    }

    [Fact]
    public void BuildHistoryLookup_ShouldOnlyCountConfirmedMenus_AndExcludeDraftsAndTargetWeek()
    {
        var targetMonday = new DateOnly(2026, 11, 2);
        var prevMonday = targetMonday.AddDays(-7);
        var twoWeeksAgo = targetMonday.AddDays(-14);

        var confirmedPrevWeek = new WeeklyMenu
        {
            WeekStartDate = prevMonday,
            Status = WeeklyMenuStatus.Confirmed,
            Days = [new WeeklyMenuDay { DayIndex = 0, Date = prevMonday, RecipeId = 10 }]
        };

        var draftTwoWeeksAgo = new WeeklyMenu
        {
            WeekStartDate = twoWeeksAgo,
            Status = WeeklyMenuStatus.Draft,
            Days = [new WeeklyMenuDay { DayIndex = 0, Date = twoWeeksAgo, RecipeId = 20 }]
        };

        var confirmedTargetWeekItself = new WeeklyMenu
        {
            WeekStartDate = targetMonday,
            Status = WeeklyMenuStatus.Confirmed,
            Days = [new WeeklyMenuDay { DayIndex = 0, Date = targetMonday, RecipeId = 30 }]
        };

        var lookup = WeeklyMenuGenerator.BuildHistoryLookup(
            [confirmedPrevWeek, draftTwoWeeksAgo, confirmedTargetWeekItself],
            targetMonday);

        lookup.Should().ContainKey(10);
        lookup[10].TotalConfirmedOccurrences.Should().Be(1);
        lookup[10].WasPlannedPreviousWeek.Should().BeTrue();
        lookup[10].WeeksSinceLastPlanned.Should().Be(1);

        lookup.Should().NotContainKey(20, "utkast ska inte påverka historiken");
        lookup.Should().NotContainKey(30, "den egna veckan ska inte dubbelräknas mot sig själv vid redigering");
    }

    [Fact]
    public void RerollDay_ShouldReplaceSingleDay_KeepOtherDays_AvoidCurrentAndOtherDaysRecipes_AndReportWhenNoOtherCandidate()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(123));
        var recipes = Enumerable.Range(1, 8)
            .Select(i => CreateRecipe(i, $"Recept {i}", categoryIds: [1]))
            .ToList();

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2), defaultCategoryId: 1);
        for (int i = 0; i < 7; i++)
        {
            menu.Days[i].RecipeId = recipes[i].Id;
            menu.Days[i].Recipe = recipes[i];
        }

        // Only Recipe 8 is unused across the other 6 days and different from Day 2's current Recipe 3!
        var rerollResult = generator.RerollDay(menu, dayIndex: 2, recipes);

        rerollResult.Changed.Should().BeTrue();
        rerollResult.NewRecipeId.Should().Be(8);
        menu.Days[2].RecipeId.Should().Be(8);

        // Other 6 days are completely unchanged
        menu.Days[0].RecipeId.Should().Be(1);
        menu.Days[1].RecipeId.Should().Be(2);
        menu.Days[3].RecipeId.Should().Be(4);
        menu.Days[4].RecipeId.Should().Be(5);
        menu.Days[5].RecipeId.Should().Be(6);
        menu.Days[6].RecipeId.Should().Be(7);

        // If only 7 recipes exist total, re-rolling Day 2 has no other unused candidate -> returns clear message
        var sevenRecipesOnly = recipes.Take(7).ToList();
        menu.Days[2].RecipeId = 3;
        menu.Days[2].Recipe = recipes[2];

        var noOtherResult = generator.RerollDay(menu, dayIndex: 2, sevenRecipesOnly);
        noOtherResult.Changed.Should().BeFalse();
        noOtherResult.Message.Should().NotBeNullOrWhiteSpace();
        menu.Days[2].RecipeId.Should().Be(3);
    }

    [Fact]
    public void MoveOrSwapRecipe_ShouldSwapRecipesOrMoveToEmptyDay_AndKeepCategoriesOnEachDay()
    {
        var generator = new WeeklyMenuGenerator();
        var r1 = CreateRecipe(1, "Vardagspasta", categoryIds: [1]);
        var r2 = CreateRecipe(2, "Helgstek", categoryIds: [2]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        // Monday has Category 1 and Recipe 1
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Recipe = r1;
        menu.Days[0].Servings = 4;

        // Friday has Category 2 and Recipe 2
        menu.Days[4].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 });
        menu.Days[4].RecipeId = r2.Id;
        menu.Days[4].Recipe = r2;
        menu.Days[4].Servings = 6;

        // Tuesday has Category 3 and is empty
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 3 });

        // 1. Swap Monday (0) and Friday (4)
        generator.MoveOrSwapRecipe(menu, 0, 4);

        menu.Days[0].RecipeId.Should().Be(2);
        menu.Days[0].Servings.Should().Be(6);
        menu.Days[0].SelectedCategories.Select(c => c.CategoryId).Should().Equal([1], "kategorin tillhör måndagen");

        menu.Days[4].RecipeId.Should().Be(1);
        menu.Days[4].Servings.Should().Be(4);
        menu.Days[4].SelectedCategories.Select(c => c.CategoryId).Should().Equal([2], "kategorin tillhör fredagen");

        // 2. Move from Friday (4) to empty Tuesday (1)
        generator.MoveOrSwapRecipe(menu, 4, 1);

        menu.Days[1].RecipeId.Should().Be(1);
        menu.Days[1].SelectedCategories.Select(c => c.CategoryId).Should().Equal([3], "tisdagens kategori ligger kvar");
        menu.Days[4].RecipeId.Should().BeNull("fredagen blev tom efter flytt till tom dag");
        menu.Days[4].SelectedCategories.Select(c => c.CategoryId).Should().Equal([2]);
    }

    [Fact]
    public void GenerateWeek_PantryFirst_WhenPantryIngredientSpecified_ShouldGuaranteeMatchingRecipeOnEligibleDay()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42));

        // 10 recipes in Category 1 (Bakning & Desserter), but only Recipe 5 has "blåbär"
        var recipes = Enumerable.Range(1, 10)
            .Select(i => CreateRecipe(i, i == 5 ? "Blåbärspannacotta" : $"Dessert {i}", categoryIds: [1], ingredientNames: i == 5 ? ["blåbär"] : ["socker", "vetemjöl"]))
            .ToList();

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        // Day 0 has Category 1 (Bakning & Desserter)
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        // Days 1..6 have Category 99 (no recipes)
        for (int i = 1; i < 7; i++)
        {
            menu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        }

        // Specify "Blåbär" under Ingredienser hemma
        menu.PantryIngredients = ["Blåbär"];

        var result = generator.GenerateWeek(menu, recipes);

        result.FilledDaysCount.Should().Be(1);
        menu.Days[0].RecipeId.Should().Be(5, "Pantry-first ska garantera att Blåbärspannacotta väljs när blåbär är specificerat hemma");
    }

    [Fact]
    public void RerollDay_PantryFirst_ShouldPickPantryMatchingRecipeFirst_AndFallbackWhenAlreadySelected()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(99));

        var r1 = CreateRecipe(1, "Chokladmousse", categoryIds: [1], ingredientNames: ["choklad"]);
        var r2 = CreateRecipe(2, "Blåbärspannacotta", categoryIds: [1], ingredientNames: ["blåbär"]);
        var r3 = CreateRecipe(3, "Äppelpaj", categoryIds: [1], ingredientNames: ["äpple"]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Recipe = r1;
        menu.PantryIngredients = ["Blåbär"];

        // 1. Reroll Day 0: Even though r3 (Äppelpaj) is also available, r2 (Blåbärspannacotta) matches the pantry ingredient and must be chosen!
        var reroll1 = generator.RerollDay(menu, dayIndex: 0, [r1, r2, r3]);
        reroll1.Changed.Should().BeTrue();
        reroll1.NewRecipeId.Should().Be(2);
        menu.Days[0].RecipeId.Should().Be(2);

        // 2. Reroll Day 0 again when Blåbärspannacotta is currently displayed:
        // No other blueberry recipe exists, so it smoothly falls back to other available candidates (r1 or r3) without getting stuck.
        var reroll2 = generator.RerollDay(menu, dayIndex: 0, [r1, r2, r3]);
        reroll2.Changed.Should().BeTrue();
        reroll2.NewRecipeId.Should().BeOneOf(1, 3);
    }

    [Fact]
    public void GenerateWeek_PantryFirst_WithMultiplePantryIngredients_ShouldSatisfyAcrossDifferentDays()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(1));

        var dessert1 = CreateRecipe(1, "Kladdkaka", categoryIds: [1]);
        var dessert2 = CreateRecipe(2, "Blåbärspannacotta", categoryIds: [1], ingredientNames: ["blåbär"]);
        var fish1 = CreateRecipe(3, "Torskrygg", categoryIds: [2], ingredientNames: ["torsk"]);
        var fish2 = CreateRecipe(4, "Laxfilé med dillsås", categoryIds: [2], ingredientNames: ["lax"]);
        var meat1 = CreateRecipe(5, "Köttfärssås", categoryIds: [3], ingredientNames: ["nötfärs"]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 }); // Bakning
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 }); // Fisk
        menu.Days[2].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 3 }); // Kött
        for (int i = 3; i < 7; i++)
        {
            menu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        }

        menu.PantryIngredients = ["Blåbär", "Lax"];

        var result = generator.GenerateWeek(menu, [dessert1, dessert2, fish1, fish2, meat1]);

        result.FilledDaysCount.Should().Be(3);
        menu.Days[0].RecipeId.Should().Be(2, "Dag 0 ska välja Blåbärspannacotta för att tillgodose Blåbär");
        menu.Days[1].RecipeId.Should().Be(4, "Dag 1 ska välja Laxfilé för att tillgodose Lax");
        menu.Days[2].RecipeId.Should().Be(5);
    }
}

