using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

/// <summary>
/// Configurable, documented weight parameters for weekly menu recipe generation.
/// Kept in a single domain configuration class to avoid magic numbers in UI components.
/// </summary>
public sealed class WeeklyMenuGenerationWeights
{
    /// <summary>Base weight assigned to every qualified candidate recipe.</summary>
    public double BaseWeight { get; init; } = 100.0;

    /// <summary>Bonus given to recipes that have never appeared in a confirmed weekly menu.</summary>
    public double NeverPlannedBonus { get; init; } = 60.0;

    /// <summary>Bonus per week elapsed since the recipe was last planned in a confirmed menu.</summary>
    public double RecencyBonusPerWeek { get; init; } = 10.0;

    /// <summary>Maximum cap for the recency bonus on previously planned recipes.</summary>
    public double MaxRecencyBonus { get; init; } = 50.0;

    /// <summary>
    /// Maximum bonus for low historical frequency, scaled as <c>LowFrequencyMaxBonus / (1 + TotalOccurrences)</c>.
    /// </summary>
    public double LowFrequencyMaxBonus { get; init; } = 40.0;

    /// <summary>Bonus added for the first matched non-trivial pantry ingredient (scaled by ingredient relevance).</summary>
    public double FirstPantryMatchBonus { get; init; } = 55.0;

    /// <summary>Bonus added for each additional matched non-trivial pantry ingredient.</summary>
    public double AdditionalPantryMatchBonus { get; init; } = 35.0;

    /// <summary>Maximum total bonus from pantry ingredient matches.</summary>
    public double MaxPantryBonus { get; init; } = 130.0;

    /// <summary>
    /// Strong penalty multiplier applied when a recipe was planned in the immediately preceding week (week - 1).
    /// Reduces likelihood by 88% while keeping the recipe eligible if few alternatives exist.
    /// </summary>
    public double PreviousWeekPenaltyMultiplier { get; init; } = 0.12;

    /// <summary>
    /// Moderate penalty multiplier applied when a recipe was planned two weeks ago (week - 2).
    /// </summary>
    public double TwoWeeksAgoPenaltyMultiplier { get; init; } = 0.55;

    /// <summary>
    /// Minimum weight floor so no qualified recipe is ever permanently excluded solely due to history.
    /// </summary>
    public double MinimumWeight { get; init; } = 5.0;
}

/// <summary>
/// Historical planning statistics for a single recipe derived strictly from confirmed weekly menus.
/// </summary>
public sealed record RecipeHistoryStats(
    int RecipeId,
    int TotalConfirmedOccurrences,
    DateOnly? LastPlannedWeekStartDate,
    int? WeeksSinceLastPlanned,
    bool WasPlannedPreviousWeek);

public sealed record WeeklyMenuGenerationResult(
    int FilledDaysCount,
    int EmptyDaysCount,
    IReadOnlyList<string> Warnings)
{
    public bool AllDaysFilled => FilledDaysCount == 7 && EmptyDaysCount == 0;
}

public sealed record DayRerollResult(
    bool Changed,
    int? NewRecipeId,
    string? Message);

/// <summary>
/// Testable domain service for weighted weekly menu generation, single-day re-rolling, and day-to-day recipe movement.
/// </summary>
public class WeeklyMenuGenerator
{
    private readonly IRandomProvider _random;
    private readonly PantryIngredientMatcher _pantryMatcher;
    private readonly WeeklyMenuGenerationWeights _weights;

    public WeeklyMenuGenerator(
        IRandomProvider? random = null,
        PantryIngredientMatcher? pantryMatcher = null,
        WeeklyMenuGenerationWeights? weights = null)
    {
        _random = random ?? new SystemRandomProvider();
        _pantryMatcher = pantryMatcher ?? new PantryIngredientMatcher();
        _weights = weights ?? new WeeklyMenuGenerationWeights();
    }

    public WeeklyMenuGenerationWeights Weights => _weights;

    /// <summary>
    /// Derives historical recipe statistics from saved weekly menus.
    /// Only menus with <see cref="WeeklyMenuStatus.Confirmed"/> are counted, and the target week itself is excluded
    /// so editing an already-confirmed menu never double-counts its own week.
    /// </summary>
    public static Dictionary<int, RecipeHistoryStats> BuildHistoryLookup(
        IEnumerable<WeeklyMenu> savedMenus,
        DateOnly targetWeekStartDate)
    {
        var targetMonday = CalendarWeek.FromDate(targetWeekStartDate).WeekStartDate;
        var previousWeekMonday = targetMonday.AddDays(-7);

        var confirmedOtherMenus = (savedMenus ?? Array.Empty<WeeklyMenu>())
            .Where(m => m.Status == WeeklyMenuStatus.Confirmed)
            .Select(m => (Menu: m, Monday: CalendarWeek.FromDate(m.WeekStartDate).WeekStartDate))
            .Where(x => x.Monday != targetMonday)
            .ToList();

        var occurrencesByRecipe = new Dictionary<int, List<DateOnly>>();

        foreach (var (menu, monday) in confirmedOtherMenus)
        {
            // Distinct RecipeIds within a single week's confirmed menu or per day occurrence:
            // each day has at most one recipe; collect all non-null RecipeIds from the confirmed menu.
            foreach (var day in menu.Days)
            {
                if (day.RecipeId.HasValue)
                {
                    int recipeId = day.RecipeId.Value;
                    if (!occurrencesByRecipe.TryGetValue(recipeId, out var weekDates))
                    {
                        weekDates = new List<DateOnly>();
                        occurrencesByRecipe[recipeId] = weekDates;
                    }
                    weekDates.Add(monday);
                }
            }
        }

        var result = new Dictionary<int, RecipeHistoryStats>();
        foreach (var (recipeId, weekMondays) in occurrencesByRecipe)
        {
            int totalOccurrences = weekMondays.Count;
            bool wasPlannedPreviousWeek = weekMondays.Contains(previousWeekMonday);

            // Prefer most recent week before targetMonday; if only future weeks exist, pick closest week
            var pastOrEqualMondays = weekMondays.Where(d => d < targetMonday).ToList();
            DateOnly lastPlannedMonday = pastOrEqualMondays.Count > 0
                ? pastOrEqualMondays.Max()
                : weekMondays.OrderBy(d => Math.Abs(targetMonday.DayNumber - d.DayNumber)).First();

            int weeksDistance = Math.Max(1, Math.Abs(CalendarWeek.WeeksBetween(lastPlannedMonday, targetMonday)));
            if (wasPlannedPreviousWeek)
            {
                weeksDistance = 1;
                lastPlannedMonday = previousWeekMonday;
            }

            result[recipeId] = new RecipeHistoryStats(
                RecipeId: recipeId,
                TotalConfirmedOccurrences: totalOccurrences,
                LastPlannedWeekStartDate: lastPlannedMonday,
                WeeksSinceLastPlanned: weeksDistance,
                WasPlannedPreviousWeek: wasPlannedPreviousWeek);
        }

        return result;
    }

    /// <summary>
    /// Computes the selection weight for a candidate recipe.
    /// Note: <see cref="Recipe.IsFavorite"/> is intentionally ignored per requirement E1.
    /// </summary>
    public double CalculateRecipeWeight(
        Recipe recipe,
        RecipeHistoryStats? history,
        IEnumerable<string>? pantryIngredients = null)
    {
        double weight = _weights.BaseWeight;

        int occurrences = history?.TotalConfirmedOccurrences ?? 0;
        int? weeksSinceLast = history?.WeeksSinceLastPlanned;
        bool plannedPrevWeek = history?.WasPlannedPreviousWeek ?? false;

        // 1. Recency bonus
        if (!weeksSinceLast.HasValue || occurrences == 0)
        {
            weight += _weights.NeverPlannedBonus;
        }
        else
        {
            double recencyBonus = Math.Min(weeksSinceLast.Value * _weights.RecencyBonusPerWeek, _weights.MaxRecencyBonus);
            weight += recencyBonus;
        }

        // 2. Low historical frequency bonus
        double lowFrequencyBonus = _weights.LowFrequencyMaxBonus / (1.0 + occurrences);
        weight += lowFrequencyBonus;

        // 3. Pantry ingredient match bonus
        var pantryEval = _pantryMatcher.EvaluateRecipe(recipe, pantryIngredients);
        if (pantryEval.MatchedPantryCount > 0 && pantryEval.TotalRelevanceScore > 0)
        {
            double pantryBonus;
            if (pantryEval.MatchedPantryCount == 1)
            {
                pantryBonus = _weights.FirstPantryMatchBonus * pantryEval.TotalRelevanceScore;
            }
            else
            {
                double firstPart = _weights.FirstPantryMatchBonus * Math.Min(1.2, pantryEval.TotalRelevanceScore);
                double extraScore = Math.Max(0.0, pantryEval.TotalRelevanceScore - 1.0);
                pantryBonus = firstPart + (_weights.AdditionalPantryMatchBonus * extraScore);
            }

            weight += Math.Min(pantryBonus, _weights.MaxPantryBonus);
        }

        // 4. High historical frequency dampening (gentle curve so frequent recipes get lower probability)
        if (occurrences > 2)
        {
            double frequencyDampening = 1.0 / (1.0 + ((occurrences - 2) * 0.18));
            weight *= frequencyDampening;
        }

        // 5. Recent week penalties
        if (plannedPrevWeek || weeksSinceLast == 1)
        {
            weight *= _weights.PreviousWeekPenaltyMultiplier;
        }
        else if (weeksSinceLast == 2)
        {
            weight *= _weights.TwoWeeksAgoPenaltyMultiplier;
        }

        // 6. Enforce minimum floor so no recipe is ever permanently excluded
        return Math.Max(_weights.MinimumWeight, weight);
    }

    /// <summary>
    /// Filters recipes that qualify for a given day's selected categories.
    /// Selected categories use OR semantics; if no categories are selected, all recipes qualify.
    /// </summary>
    public static List<Recipe> GetQualifiedRecipesForDay(
        IReadOnlyList<Recipe> allRecipes,
        IEnumerable<int> selectedCategoryIds)
    {
        var categorySet = selectedCategoryIds?.Where(id => id > 0).ToHashSet() ?? new HashSet<int>();
        if (categorySet.Count == 0)
        {
            return allRecipes.ToList();
        }

        return allRecipes
            .Where(r => r.RecipeCategories.Any(rc => categorySet.Contains(rc.CategoryId)))
            .ToList();
    }

    /// <summary>
    /// Generates a full 7-day weekly menu proposal.
    /// Uses maximum bipartite matching lookahead to ensure a greedy choice on one day never starves another day
    /// when a different valid distribution of recipes could fill more days.
    /// </summary>
    public WeeklyMenuGenerationResult GenerateWeek(
        WeeklyMenu menu,
        IReadOnlyList<Recipe> allRecipes,
        IReadOnlyDictionary<int, RecipeHistoryStats>? historyByRecipeId = null)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(allRecipes);

        EnsureSevenDaysInitialized(menu);
        var history = historyByRecipeId ?? new Dictionary<int, RecipeHistoryStats>();
        var pantry = menu.PantryIngredients;

        // Precompute weights for all recipes
        var weightsByRecipeId = new Dictionary<int, double>(allRecipes.Count);
        foreach (var recipe in allRecipes)
        {
            history.TryGetValue(recipe.Id, out var stats);
            weightsByRecipeId[recipe.Id] = CalculateRecipeWeight(recipe, stats, pantry);
        }

        // Determine qualified candidates per day
        var orderedDays = menu.Days.OrderBy(d => d.DayIndex).ToList();
        var candidatesByDayIndex = new Dictionary<int, List<Recipe>>(7);

        foreach (var day in orderedDays)
        {
            day.RecipeId = null;
            day.Recipe = null;
            day.Servings = null;
            day.StatusMessage = null;

            var selectedCatIds = day.SelectedCategories.Select(sc => sc.CategoryId);
            candidatesByDayIndex[day.DayIndex] = GetQualifiedRecipesForDay(allRecipes, selectedCatIds);
        }

        var warnings = new List<string>();

        if (allRecipes.Count == 0)
        {
            const string noRecipesMsg = "Det finns inga recept i receptbanken ännu.";
            foreach (var day in orderedDays)
            {
                day.StatusMessage = noRecipesMsg;
            }
            warnings.Add(noRecipesMsg);
            return new WeeklyMenuGenerationResult(0, 7, warnings);
        }

        // Separate days with 0 candidates from potentially fillable days
        var potentiallyFillableDays = new List<WeeklyMenuDay>();
        foreach (var day in orderedDays)
        {
            if (candidatesByDayIndex[day.DayIndex].Count == 0)
            {
                var dayName = CalendarWeek.GetSwedishDayName(day.DayIndex);
                day.StatusMessage = "Inga recept matchar dagens valda kategorier.";
                warnings.Add($"{dayName}: {day.StatusMessage}");
            }
            else
            {
                potentiallyFillableDays.Add(day);
            }
        }

        // Order potentially fillable days by fewest candidates first (most constrained first)
        var evaluationOrder = potentiallyFillableDays
            .OrderBy(d => candidatesByDayIndex[d.DayIndex].Count)
            .ThenBy(d => d.DayIndex)
            .ToList();

        var usedRecipeIds = new HashSet<int>();
        int remainingTargetMatches = GetMaxBipartiteMatchingSize(evaluationOrder, candidatesByDayIndex, usedRecipeIds);

        for (int i = 0; i < evaluationOrder.Count; i++)
        {
            var currentDay = evaluationOrder[i];
            var remainingDaysAfterCurrent = evaluationOrder.Skip(i + 1).ToList();

            var unusedCandidates = candidatesByDayIndex[currentDay.DayIndex]
                .Where(r => !usedRecipeIds.Contains(r.Id))
                .ToList();

            if (unusedCandidates.Count == 0 || remainingTargetMatches <= 0)
            {
                var dayName = CalendarWeek.GetSwedishDayName(currentDay.DayIndex);
                currentDay.StatusMessage = "För få unika recept för dagens kategorival.";
                warnings.Add($"{dayName}: {currentDay.StatusMessage}");
                continue;
            }

            // Filter to candidates that preserve the maximum matching cardinality for the remaining days
            var safeCandidates = new List<Recipe>();
            foreach (var candidate in unusedCandidates)
            {
                usedRecipeIds.Add(candidate.Id);
                int maxAfterPick = GetMaxBipartiteMatchingSize(remainingDaysAfterCurrent, candidatesByDayIndex, usedRecipeIds);
                usedRecipeIds.Remove(candidate.Id);

                if (1 + maxAfterPick >= remainingTargetMatches)
                {
                    safeCandidates.Add(candidate);
                }
            }

            if (safeCandidates.Count == 0)
            {
                // Current day cannot be included without reducing total matched days (should only happen if remainingTargetMatches == maxWithoutCurrent)
                int maxWithoutCurrent = GetMaxBipartiteMatchingSize(remainingDaysAfterCurrent, candidatesByDayIndex, usedRecipeIds);
                if (maxWithoutCurrent == remainingTargetMatches)
                {
                    var dayName = CalendarWeek.GetSwedishDayName(currentDay.DayIndex);
                    currentDay.StatusMessage = "För få unika recept för dagens kategorival.";
                    warnings.Add($"{dayName}: {currentDay.StatusMessage}");
                    continue;
                }

                safeCandidates = unusedCandidates;
            }

            var chosen = SelectWeightedRandom(safeCandidates, weightsByRecipeId);
            currentDay.RecipeId = chosen.Id;
            currentDay.Recipe = chosen;
            currentDay.Servings = chosen.Servings;
            currentDay.StatusMessage = null;

            usedRecipeIds.Add(chosen.Id);
            remainingTargetMatches--;
        }

        int filledCount = orderedDays.Count(d => d.RecipeId.HasValue);
        int emptyCount = 7 - filledCount;

        return new WeeklyMenuGenerationResult(filledCount, emptyCount, warnings);
    }

    /// <summary>
    /// Re-rolls a single day ("Slumpa nytt").
    /// Keeps all other days' recipes unchanged, respects the day's selected categories, avoids recipes already used
    /// on other days, and avoids the currently displayed recipe on this day whenever another candidate exists.
    /// Does not modify planning history.
    /// </summary>
    public DayRerollResult RerollDay(
        WeeklyMenu menu,
        int dayIndex,
        IReadOnlyList<Recipe> allRecipes,
        IReadOnlyDictionary<int, RecipeHistoryStats>? historyByRecipeId = null)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(allRecipes);

        var day = menu.Days.FirstOrDefault(d => d.DayIndex == dayIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(dayIndex), $"Dag med index {dayIndex} hittades inte.");

        var selectedCatIds = day.SelectedCategories.Select(sc => sc.CategoryId);
        var categoryCandidates = GetQualifiedRecipesForDay(allRecipes, selectedCatIds);

        if (categoryCandidates.Count == 0)
        {
            const string msg = "Inga recept matchar dagens valda kategorier.";
            day.StatusMessage = msg;
            return new DayRerollResult(false, day.RecipeId, msg);
        }

        var otherDaysRecipeIds = menu.Days
            .Where(d => d.DayIndex != dayIndex && d.RecipeId.HasValue)
            .Select(d => d.RecipeId!.Value)
            .ToHashSet();

        var availableNotOnOtherDays = categoryCandidates
            .Where(r => !otherDaysRecipeIds.Contains(r.Id))
            .ToList();

        if (availableNotOnOtherDays.Count == 0)
        {
            const string msg = "Alla recept som matchar dagens kategorier används redan på andra dagar i veckomenyn.";
            day.StatusMessage = msg;
            return new DayRerollResult(false, day.RecipeId, msg);
        }

        int? currentRecipeId = day.RecipeId;
        var preferredCandidates = currentRecipeId.HasValue
            ? availableNotOnOtherDays.Where(r => r.Id != currentRecipeId.Value).ToList()
            : availableNotOnOtherDays;

        if (preferredCandidates.Count == 0)
        {
            const string msg = "Det finns inga andra lediga recept som matchar dagens kategorival.";
            day.StatusMessage = msg;
            return new DayRerollResult(false, day.RecipeId, msg);
        }

        var history = historyByRecipeId ?? new Dictionary<int, RecipeHistoryStats>();
        var pantry = menu.PantryIngredients;
        var weightsByRecipeId = new Dictionary<int, double>(preferredCandidates.Count);

        foreach (var candidate in preferredCandidates)
        {
            history.TryGetValue(candidate.Id, out var stats);
            weightsByRecipeId[candidate.Id] = CalculateRecipeWeight(candidate, stats, pantry);
        }

        var chosen = SelectWeightedRandom(preferredCandidates, weightsByRecipeId);
        day.RecipeId = chosen.Id;
        day.Recipe = chosen;
        day.Servings = chosen.Servings;
        day.StatusMessage = null;

        return new DayRerollResult(true, chosen.Id, null);
    }

    /// <summary>
    /// Moves or swaps recipes between two days in the weekly menu.
    /// Used by both desktop drag-and-drop and mobile/iPad "Flytta till…".
    /// Day categories remain attached to their respective day per requirement F3.
    /// </summary>
    public void MoveOrSwapRecipe(WeeklyMenu menu, int sourceDayIndex, int targetDayIndex)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (sourceDayIndex == targetDayIndex)
            return;

        var sourceDay = menu.Days.FirstOrDefault(d => d.DayIndex == sourceDayIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(sourceDayIndex));
        var targetDay = menu.Days.FirstOrDefault(d => d.DayIndex == targetDayIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(targetDayIndex));

        (sourceDay.RecipeId, targetDay.RecipeId) = (targetDay.RecipeId, sourceDay.RecipeId);
        (sourceDay.Recipe, targetDay.Recipe) = (targetDay.Recipe, sourceDay.Recipe);
        (sourceDay.Servings, targetDay.Servings) = (targetDay.Servings, sourceDay.Servings);

        sourceDay.StatusMessage = null;
        targetDay.StatusMessage = null;
    }

    private Recipe SelectWeightedRandom(
        IReadOnlyList<Recipe> candidates,
        IReadOnlyDictionary<int, double> weightsByRecipeId)
    {
        if (candidates.Count == 1)
            return candidates[0];

        double totalWeight = 0.0;
        foreach (var candidate in candidates)
        {
            totalWeight += weightsByRecipeId.GetValueOrDefault(candidate.Id, _weights.BaseWeight);
        }

        if (totalWeight <= 0.0)
        {
            int fallbackIdx = Math.Clamp((int)(_random.NextDouble() * candidates.Count), 0, candidates.Count - 1);
            return candidates[fallbackIdx];
        }

        double sample = Math.Clamp(_random.NextDouble(), 0.0, 0.999999999999) * totalWeight;
        double cumulative = 0.0;

        for (int i = 0; i < candidates.Count; i++)
        {
            cumulative += weightsByRecipeId.GetValueOrDefault(candidates[i].Id, _weights.BaseWeight);
            if (sample < cumulative)
            {
                return candidates[i];
            }
        }

        return candidates[^1];
    }

    /// <summary>
    /// Computes the maximum number of days in <paramref name="days"/> that can be assigned distinct recipes
    /// from <paramref name="candidatesByDayIndex"/> excluding <paramref name="alreadyUsedRecipeIds"/>.
    /// Uses Kuhn's augmenting path algorithm for maximum bipartite matching.
    /// </summary>
    private static int GetMaxBipartiteMatchingSize(
        IReadOnlyList<WeeklyMenuDay> days,
        IReadOnlyDictionary<int, List<Recipe>> candidatesByDayIndex,
        HashSet<int> alreadyUsedRecipeIds)
    {
        if (days.Count == 0)
            return 0;

        // Map recipeId -> assigned day list index (0..days.Count-1)
        var matchByRecipeId = new Dictionary<int, int>();
        int matchedCount = 0;

        for (int dayListIdx = 0; dayListIdx < days.Count; dayListIdx++)
        {
            var visitedRecipes = new HashSet<int>();
            if (TryFindAugmentingPath(dayListIdx, days, candidatesByDayIndex, alreadyUsedRecipeIds, visitedRecipes, matchByRecipeId))
            {
                matchedCount++;
            }
        }

        return matchedCount;
    }

    private static bool TryFindAugmentingPath(
        int dayListIdx,
        IReadOnlyList<WeeklyMenuDay> days,
        IReadOnlyDictionary<int, List<Recipe>> candidatesByDayIndex,
        HashSet<int> alreadyUsedRecipeIds,
        HashSet<int> visitedRecipes,
        Dictionary<int, int> matchByRecipeId)
    {
        var day = days[dayListIdx];
        if (!candidatesByDayIndex.TryGetValue(day.DayIndex, out var candidates))
            return false;

        foreach (var candidate in candidates)
        {
            int recipeId = candidate.Id;
            if (alreadyUsedRecipeIds.Contains(recipeId) || !visitedRecipes.Add(recipeId))
                continue;

            if (!matchByRecipeId.TryGetValue(recipeId, out int existingDayIdx) ||
                TryFindAugmentingPath(existingDayIdx, days, candidatesByDayIndex, alreadyUsedRecipeIds, visitedRecipes, matchByRecipeId))
            {
                matchByRecipeId[recipeId] = dayListIdx;
                return true;
            }
        }

        return false;
    }

    public static void EnsureSevenDaysInitialized(WeeklyMenu menu)
    {
        var calendarWeek = CalendarWeek.FromDate(menu.WeekStartDate == default
            ? DateOnly.FromDateTime(DateTime.Today)
            : menu.WeekStartDate);

        menu.WeekStartDate = calendarWeek.WeekStartDate;
        menu.IsoYear = calendarWeek.IsoYear;
        menu.IsoWeek = calendarWeek.IsoWeek;

        for (int i = 0; i < 7; i++)
        {
            var existingDay = menu.Days.FirstOrDefault(d => d.DayIndex == i);
            if (existingDay == null)
            {
                menu.Days.Add(new WeeklyMenuDay
                {
                    DayIndex = i,
                    Date = calendarWeek.GetDateForDayIndex(i)
                });
            }
            else
            {
                existingDay.Date = calendarWeek.GetDateForDayIndex(i);
            }
        }

        menu.Days = menu.Days.OrderBy(d => d.DayIndex).ToList();
    }
}

