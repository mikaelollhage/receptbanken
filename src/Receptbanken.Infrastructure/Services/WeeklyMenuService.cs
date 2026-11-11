using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

public sealed class TodaysDinnerDto
{
    public required CalendarWeek CalendarWeek { get; init; }
    public required DateOnly LocalDate { get; init; }
    public bool HasConfirmedMenu { get; init; }
    public Recipe? Recipe { get; init; }
    public int Servings { get; init; } = 4;
}

public class WeeklyMenuService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly WeeklyMenuGenerator _generator;
    private readonly PantryIngredientMatcher _pantryMatcher;
    private readonly ILogger<WeeklyMenuService> _logger;

    public WeeklyMenuService(
        IDbContextFactory<AppDbContext> contextFactory,
        WeeklyMenuGenerator generator,
        PantryIngredientMatcher pantryMatcher,
        ILogger<WeeklyMenuService> logger)
    {
        _contextFactory = contextFactory;
        _generator = generator;
        _pantryMatcher = pantryMatcher;
        _logger = logger;
    }

    public WeeklyMenuGenerator Generator => _generator;

    /// <summary>
    /// Returns the persisted <see cref="WeeklyMenu"/> (Draft or Confirmed) for the ISO week containing <paramref name="dateInWeek"/>,
    /// or <c>null</c> if no menu has been saved for that week yet.
    /// </summary>
    public async Task<WeeklyMenu?> GetSavedMenuForWeekAsync(DateOnly dateInWeek, CancellationToken cancellationToken = default)
    {
        var week = CalendarWeek.FromDate(dateInWeek);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var menu = await LoadMenuByWeekStartAsync(context, week.WeekStartDate, cancellationToken);
        if (menu == null)
            return null;

        EnsureCompleteDetachedMenu(menu, week);
        return menu;
    }

    /// <summary>
    /// Returns the saved <see cref="WeeklyMenu"/> for the week if one exists in the database;
    /// otherwise builds a new in-memory draft initialized with the 7 days (Måndag–Söndag) and default categories per C1.
    /// Does not create duplicate categories.
    /// </summary>
    public async Task<WeeklyMenu> GetOrCreateWeekMenuAsync(DateOnly dateInWeek, CancellationToken cancellationToken = default)
    {
        var week = CalendarWeek.FromDate(dateInWeek);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await LoadMenuByWeekStartAsync(context, week.WeekStartDate, cancellationToken);
        if (existing != null)
        {
            EnsureCompleteDetachedMenu(existing, week);
            return existing;
        }

        var categories = await context.Categories
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return CreateDefaultDraftMenu(week, categories);
    }

    public static WeeklyMenu CreateDefaultDraftMenu(CalendarWeek week, IReadOnlyCollection<Category> existingCategories)
    {
        var menu = new WeeklyMenu
        {
            WeekStartDate = week.WeekStartDate,
            IsoYear = week.IsoYear,
            IsoWeek = week.IsoWeek,
            Status = WeeklyMenuStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        for (int dayIndex = 0; dayIndex < 7; dayIndex++)
        {
            var defaultCategoryName = CalendarWeek.GetDefaultCategoryName(dayIndex);
            var matchedCat = existingCategories.FirstOrDefault(
                c => string.Equals(c.Name.Trim(), defaultCategoryName, StringComparison.OrdinalIgnoreCase));

            var day = new WeeklyMenuDay
            {
                DayIndex = dayIndex,
                Date = week.GetDateForDayIndex(dayIndex)
            };

            if (matchedCat != null)
            {
                day.SelectedCategories.Add(new WeeklyMenuDayCategory
                {
                    CategoryId = matchedCat.Id,
                    Category = matchedCat
                });
            }

            menu.Days.Add(day);
        }

        return menu;
    }

    /// <summary>
    /// Saves the weekly menu as a <see cref="WeeklyMenuStatus.Draft"/>.
    /// </summary>
    public Task<WeeklyMenu> SaveDraftAsync(WeeklyMenu menu, CancellationToken cancellationToken = default) =>
        SaveMenuAsync(menu, WeeklyMenuStatus.Draft, cancellationToken);

    /// <summary>
    /// Confirms ("spikar") the weekly menu or saves edits to an already confirmed weekly menu.
    /// </summary>
    public Task<WeeklyMenu> ConfirmMenuAsync(WeeklyMenu menu, CancellationToken cancellationToken = default) =>
        SaveMenuAsync(menu, WeeklyMenuStatus.Confirmed, cancellationToken);

    /// <summary>
    /// Persists a weekly menu (inserting or updating in-place for the calendar week) with the specified status.
    /// Guarantees a single menu per calendar week and prevents history double-counting.
    /// </summary>
    public async Task<WeeklyMenu> SaveMenuAsync(
        WeeklyMenu menu,
        WeeklyMenuStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);

        var week = CalendarWeek.FromDate(menu.WeekStartDate);
        WeeklyMenuGenerator.EnsureSevenDaysInitialized(menu);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.WeeklyMenus
            .Include(m => m.PantryItems)
            .Include(m => m.Days)
                .ThenInclude(d => d.SelectedCategories)
            .FirstOrDefaultAsync(
                m => m.WeekStartDate == week.WeekStartDate || (m.IsoYear == week.IsoYear && m.IsoWeek == week.IsoWeek),
                cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // Validate that referenced RecipeIds and CategoryIds actually exist in the database
        var validRecipeIds = await context.Recipes
            .Select(r => r.Id)
            .ToHashSetAsync(cancellationToken);

        var validCategoryIds = await context.Categories
            .Select(c => c.Id)
            .ToHashSetAsync(cancellationToken);

        if (existing == null)
        {
            existing = new WeeklyMenu
            {
                WeekStartDate = week.WeekStartDate,
                IsoYear = week.IsoYear,
                IsoWeek = week.IsoWeek,
                Status = status,
                ConfirmedAt = status == WeeklyMenuStatus.Confirmed ? (menu.ConfirmedAt ?? now) : null,
                CreatedAt = now,
                UpdatedAt = now
            };

            PopulatePantryAndDaysOnEntity(existing, menu, week, validRecipeIds, validCategoryIds);
            context.WeeklyMenus.Add(existing);
        }
        else
        {
            existing.WeekStartDate = week.WeekStartDate;
            existing.IsoYear = week.IsoYear;
            existing.IsoWeek = week.IsoWeek;
            existing.Status = status;
            existing.UpdatedAt = now;
            if (status == WeeklyMenuStatus.Confirmed)
            {
                existing.ConfirmedAt ??= now;
            }
            else
            {
                existing.ConfirmedAt = null;
            }

            // Replace pantry items
            context.WeeklyMenuPantryItems.RemoveRange(existing.PantryItems);
            existing.PantryItems.Clear();

            var distinctPantry = menu.PantryItems
                .OrderBy(p => p.OrderIndex)
                .Select(p => p.IngredientName?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < distinctPantry.Count; i++)
            {
                existing.PantryItems.Add(new WeeklyMenuPantryItem
                {
                    WeeklyMenuId = existing.Id,
                    OrderIndex = i + 1,
                    IngredientName = distinctPantry[i]!
                });
            }

            // Update or create the 7 days in-place
            for (int dayIndex = 0; dayIndex < 7; dayIndex++)
            {
                var incomingDay = menu.Days.FirstOrDefault(d => d.DayIndex == dayIndex);
                var existingDay = existing.Days.FirstOrDefault(d => d.DayIndex == dayIndex);

                int? safeRecipeId = incomingDay?.RecipeId is int rid && validRecipeIds.Contains(rid) ? rid : null;
                int? safeServings = safeRecipeId.HasValue ? (incomingDay?.Servings ?? incomingDay?.Recipe?.Servings ?? 4) : null;

                var distinctCatIds = (incomingDay?.SelectedCategories ?? new List<WeeklyMenuDayCategory>())
                    .Select(sc => sc.CategoryId)
                    .Where(cid => validCategoryIds.Contains(cid))
                    .Distinct()
                    .ToList();

                if (existingDay == null)
                {
                    existingDay = new WeeklyMenuDay
                    {
                        WeeklyMenuId = existing.Id,
                        DayIndex = dayIndex,
                        Date = week.GetDateForDayIndex(dayIndex),
                        RecipeId = safeRecipeId,
                        Servings = safeServings,
                        SelectedCategories = distinctCatIds
                            .Select(cid => new WeeklyMenuDayCategory { CategoryId = cid })
                            .ToList()
                    };
                    existing.Days.Add(existingDay);
                }
                else
                {
                    existingDay.Date = week.GetDateForDayIndex(dayIndex);
                    existingDay.RecipeId = safeRecipeId;
                    existingDay.Servings = safeServings;

                    context.WeeklyMenuDayCategories.RemoveRange(existingDay.SelectedCategories);
                    existingDay.SelectedCategories = distinctCatIds
                        .Select(cid => new WeeklyMenuDayCategory
                        {
                            WeeklyMenuDayId = existingDay.Id,
                            CategoryId = cid
                        })
                        .ToList();
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Sparade veckomeny för ISO-vecka {IsoYear}-W{IsoWeek:D2} ({WeekStartDate}) med status {Status}",
            week.IsoYear, week.IsoWeek, week.WeekStartDate, status);

        var reloaded = await LoadMenuByWeekStartAsync(context, week.WeekStartDate, cancellationToken);
        EnsureCompleteDetachedMenu(reloaded!, week);

        // Preserve any transient status messages from the in-memory menu onto the reloaded days
        foreach (var day in reloaded!.Days)
        {
            var srcDay = menu.Days.FirstOrDefault(d => d.DayIndex == day.DayIndex);
            if (srcDay != null && !day.RecipeId.HasValue)
            {
                day.StatusMessage = srcDay.StatusMessage;
            }
        }

        return reloaded;
    }

    /// <summary>
    /// Derives historical recipe statistics from all confirmed weekly menus in the database (excluding <paramref name="targetWeekStartDate"/>).
    /// </summary>
    public async Task<Dictionary<int, RecipeHistoryStats>> GetHistoryLookupAsync(
        DateOnly targetWeekStartDate,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var confirmedMenus = await context.WeeklyMenus
            .AsNoTracking()
            .Where(m => m.Status == WeeklyMenuStatus.Confirmed)
            .Include(m => m.Days)
            .ToListAsync(cancellationToken);

        return WeeklyMenuGenerator.BuildHistoryLookup(confirmedMenus, targetWeekStartDate);
    }

    /// <summary>
    /// Generates a full 7-day weekly menu proposal on <paramref name="menu"/> using current database recipes and confirmed history.
    /// Does not automatically confirm or save the menu.
    /// </summary>
    public async Task<WeeklyMenuGenerationResult> GenerateWeekProposalAsync(
        WeeklyMenu menu,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var allRecipes = await LoadAllCandidateRecipesAsync(context, cancellationToken);
        var confirmedMenus = await context.WeeklyMenus
            .AsNoTracking()
            .Where(m => m.Status == WeeklyMenuStatus.Confirmed)
            .Include(m => m.Days)
            .ToListAsync(cancellationToken);

        var history = WeeklyMenuGenerator.BuildHistoryLookup(confirmedMenus, menu.WeekStartDate);
        return _generator.GenerateWeek(menu, allRecipes, history);
    }

    /// <summary>
    /// Re-rolls a single day ("Slumpa nytt") on <paramref name="menu"/> without changing other days or updating planning history.
    /// </summary>
    public async Task<DayRerollResult> RerollDayProposalAsync(
        WeeklyMenu menu,
        int dayIndex,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var allRecipes = await LoadAllCandidateRecipesAsync(context, cancellationToken);
        var confirmedMenus = await context.WeeklyMenus
            .AsNoTracking()
            .Where(m => m.Status == WeeklyMenuStatus.Confirmed)
            .Include(m => m.Days)
            .ToListAsync(cancellationToken);

        var history = WeeklyMenuGenerator.BuildHistoryLookup(confirmedMenus, menu.WeekStartDate);
        return _generator.RerollDay(menu, dayIndex, allRecipes, history);
    }

    /// <summary>
    /// Moves or swaps recipes between two days. Day categories remain on their respective day.
    /// </summary>
    public void MoveOrSwapDayRecipe(WeeklyMenu menu, int sourceDayIndex, int targetDayIndex) =>
        _generator.MoveOrSwapRecipe(menu, sourceDayIndex, targetDayIndex);

    /// <summary>
    /// Retrieves today's planned dinner for the start page ("Dagens middag") if a confirmed weekly menu exists for the current local week.
    /// </summary>
    public async Task<TodaysDinnerDto> GetTodaysDinnerAsync(
        DateOnly localToday,
        CancellationToken cancellationToken = default)
    {
        var week = CalendarWeek.FromDate(localToday);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var confirmedMenu = await context.WeeklyMenus
            .AsNoTracking()
            .Where(m => m.Status == WeeklyMenuStatus.Confirmed && m.WeekStartDate == week.WeekStartDate)
            .Include(m => m.Days)
                .ThenInclude(d => d.Recipe)
                    .ThenInclude(r => r!.Instructions)
            .Include(m => m.Days)
                .ThenInclude(d => d.Recipe)
                    .ThenInclude(r => r!.RecipeCategories)
                        .ThenInclude(rc => rc.Category)
            .FirstOrDefaultAsync(cancellationToken);

        if (confirmedMenu == null)
        {
            return new TodaysDinnerDto
            {
                CalendarWeek = week,
                LocalDate = localToday,
                HasConfirmedMenu = false,
                Recipe = null
            };
        }

        int todayIndex = CalendarWeek.GetDayIndex(localToday);
        var todayEntry = confirmedMenu.Days.FirstOrDefault(d => d.Date == localToday)
                         ?? confirmedMenu.Days.FirstOrDefault(d => d.DayIndex == todayIndex);

        var recipe = todayEntry?.Recipe;
        int servings = todayEntry?.Servings ?? recipe?.Servings ?? 4;

        return new TodaysDinnerDto
        {
            CalendarWeek = week,
            LocalDate = localToday,
            HasConfirmedMenu = true,
            Recipe = recipe,
            Servings = servings
        };
    }

    /// <summary>
    /// Retrieves the meal plan for the start page (Home) containing both today's planned dinner and tomorrow's meal,
    /// supporting both Confirmed and Draft saved menus and handling week transitions (e.g. Sunday to Monday).
    /// </summary>
    public async Task<HomeMealPlanDto> GetHomeMealPlanAsync(
        DateOnly localToday,
        CancellationToken cancellationToken = default)
    {
        var today = localToday;
        var tomorrow = localToday.AddDays(1);
        var todayWeek = CalendarWeek.FromDate(today);
        var tomorrowWeek = CalendarWeek.FromDate(tomorrow);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var targetMondays = new List<DateOnly> { todayWeek.WeekStartDate };
        if (tomorrowWeek.WeekStartDate != todayWeek.WeekStartDate)
        {
            targetMondays.Add(tomorrowWeek.WeekStartDate);
        }

        var savedMenus = await context.WeeklyMenus
            .AsNoTracking()
            .Where(m => targetMondays.Contains(m.WeekStartDate))
            .Include(m => m.Days)
                .ThenInclude(d => d.Recipe)
                    .ThenInclude(r => r!.Instructions)
            .Include(m => m.Days)
                .ThenInclude(d => d.Recipe)
                    .ThenInclude(r => r!.RecipeCategories)
                        .ThenInclude(rc => rc.Category)
            .ToListAsync(cancellationToken);

        var todayMenu = savedMenus.FirstOrDefault(m => m.WeekStartDate == todayWeek.WeekStartDate);
        var tomorrowMenu = tomorrowWeek.WeekStartDate == todayWeek.WeekStartDate
            ? todayMenu
            : savedMenus.FirstOrDefault(m => m.WeekStartDate == tomorrowWeek.WeekStartDate);

        var todayMeal = ExtractDayMeal(todayWeek, today, todayMenu);
        var tomorrowMeal = ExtractDayMeal(tomorrowWeek, tomorrow, tomorrowMenu);

        return new HomeMealPlanDto
        {
            CalendarWeek = todayWeek,
            Today = today,
            Tomorrow = tomorrow,
            TodayMeal = todayMeal,
            TomorrowMeal = tomorrowMeal
        };
    }

    private static PlannedDayMealDto ExtractDayMeal(CalendarWeek week, DateOnly targetDate, WeeklyMenu? menu)
    {
        if (menu == null)
        {
            return new PlannedDayMealDto
            {
                CalendarWeek = week,
                Date = targetDate,
                HasSavedMenu = false,
                MenuStatus = null,
                Recipe = null,
                Servings = 4
            };
        }

        int targetDayIndex = CalendarWeek.GetDayIndex(targetDate);
        var dayEntry = menu.Days.FirstOrDefault(d => d.Date == targetDate)
                       ?? menu.Days.FirstOrDefault(d => d.DayIndex == targetDayIndex);

        var recipe = dayEntry?.Recipe;
        int servings = dayEntry?.Servings ?? recipe?.Servings ?? 4;

        return new PlannedDayMealDto
        {
            CalendarWeek = week,
            Date = targetDate,
            HasSavedMenu = true,
            MenuStatus = menu.Status,
            Recipe = recipe,
            Servings = servings
        };
    }

    /// <summary>
    /// Returns non-trivial ingredient names from existing recipes for autocomplete suggestions in "Ingredienser hemma".
    /// </summary>
    public async Task<List<string>> GetKnownIngredientSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var rawNames = await context.Ingredients
            .AsNoTracking()
            .Select(i => i.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        return rawNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && !_pantryMatcher.IsTrivial(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void PopulatePantryAndDaysOnEntity(
        WeeklyMenu target,
        WeeklyMenu source,
        CalendarWeek week,
        HashSet<int> validRecipeIds,
        HashSet<int> validCategoryIds)
    {
        var distinctPantry = source.PantryItems
            .OrderBy(p => p.OrderIndex)
            .Select(p => p.IngredientName?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        target.PantryItems = distinctPantry
            .Select((name, idx) => new WeeklyMenuPantryItem
            {
                OrderIndex = idx + 1,
                IngredientName = name!
            })
            .ToList();

        target.Days.Clear();
        for (int dayIndex = 0; dayIndex < 7; dayIndex++)
        {
            var srcDay = source.Days.FirstOrDefault(d => d.DayIndex == dayIndex);
            int? safeRecipeId = srcDay?.RecipeId is int rid && validRecipeIds.Contains(rid) ? rid : null;
            int? safeServings = safeRecipeId.HasValue ? (srcDay?.Servings ?? srcDay?.Recipe?.Servings ?? 4) : null;

            var distinctCatIds = (srcDay?.SelectedCategories ?? new List<WeeklyMenuDayCategory>())
                .Select(sc => sc.CategoryId)
                .Where(cid => validCategoryIds.Contains(cid))
                .Distinct()
                .ToList();

            target.Days.Add(new WeeklyMenuDay
            {
                DayIndex = dayIndex,
                Date = week.GetDateForDayIndex(dayIndex),
                RecipeId = safeRecipeId,
                Servings = safeServings,
                SelectedCategories = distinctCatIds
                    .Select(cid => new WeeklyMenuDayCategory { CategoryId = cid })
                    .ToList()
            });
        }
    }

    private static async Task<WeeklyMenu?> LoadMenuByWeekStartAsync(
        AppDbContext context,
        DateOnly weekStartDate,
        CancellationToken cancellationToken)
    {
        return await context.WeeklyMenus
            .AsNoTracking()
            .Where(m => m.WeekStartDate == weekStartDate)
            .Include(m => m.PantryItems.OrderBy(p => p.OrderIndex))
            .Include(m => m.Days.OrderBy(d => d.DayIndex))
                .ThenInclude(d => d.Recipe)
                    .ThenInclude(r => r!.RecipeCategories)
                        .ThenInclude(rc => rc.Category)
            .Include(m => m.Days)
                .ThenInclude(d => d.Recipe)
                    .ThenInclude(r => r!.Ingredients)
            .Include(m => m.Days)
                .ThenInclude(d => d.SelectedCategories)
                    .ThenInclude(sc => sc.Category)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<List<Recipe>> LoadAllCandidateRecipesAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.Recipes
            .AsNoTracking()
            .Include(r => r.RecipeCategories)
                .ThenInclude(rc => rc.Category)
            .Include(r => r.Ingredients)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    private static void EnsureCompleteDetachedMenu(WeeklyMenu menu, CalendarWeek week)
    {
        menu.WeekStartDate = week.WeekStartDate;
        menu.IsoYear = week.IsoYear;
        menu.IsoWeek = week.IsoWeek;
        menu.PantryItems = menu.PantryItems.OrderBy(p => p.OrderIndex).ToList();
        WeeklyMenuGenerator.EnsureSevenDaysInitialized(menu);
    }
}

