using Receptbanken.Core.Models;

namespace Receptbanken.Infrastructure.Services;

/// <summary>
/// Represents a planned meal for a specific calendar day in the weekly menu (Draft or Confirmed).
/// </summary>
public sealed class PlannedDayMealDto
{
    public required CalendarWeek CalendarWeek { get; init; }
    public required DateOnly Date { get; init; }
    public bool HasSavedMenu { get; init; }
    public WeeklyMenuStatus? MenuStatus { get; init; }
    public Recipe? Recipe { get; init; }
    public int Servings { get; init; } = 4;

    public bool HasRecipe => Recipe != null;
    public bool IsConfirmed => MenuStatus == WeeklyMenuStatus.Confirmed;
    public bool IsDraft => MenuStatus == WeeklyMenuStatus.Draft;
}

/// <summary>
/// Composite view model for the start page (Home) containing both today's dinner and tomorrow's meal.
/// </summary>
public sealed class HomeMealPlanDto
{
    public required CalendarWeek CalendarWeek { get; init; }
    public required DateOnly Today { get; init; }
    public required DateOnly Tomorrow { get; init; }
    public required PlannedDayMealDto TodayMeal { get; init; }
    public required PlannedDayMealDto TomorrowMeal { get; init; }
}
