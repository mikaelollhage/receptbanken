using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Infrastructure.Services;

public enum WeeklyMenuConfirmStatus
{
    Success = 0,
    NoSelection = 1,
    Conflict = 2,
    MenuNotFound = 3
}

public sealed record WeeklyMenuPlannedMealDto(
    int DayIndex,
    string DayName,
    DateOnly Date,
    int RecipeId,
    string RecipeTitle,
    int BaseServings,
    int PlannedServings,
    string ServingsUnit,
    bool IsFullyAdded,
    bool IsPartiallyAdded);

public sealed record WeeklyMenuDayContributionDto(
    int? ExistingContributionId,
    int DayIndex,
    int RecipeId,
    string RecipeTitle,
    string ContributionKey,
    int PlannedServings,
    int AlreadyAddedServings,
    decimal? FullQuantity,
    decimal? FullQuantityMax,
    string? Unit,
    bool IsPortionIncreaseDelta);

public sealed class WeeklyMenuPreviewItemDto
{
    public required string PreviewItemKey { get; init; }
    public required ShoppingListItem Item { get; init; }
    public required bool IsAlreadyAdded { get; init; }
    public required bool IsSelectedByDefault { get; init; }
    public required string SourceDaysSummary { get; init; }
    public string? AlreadyAddedSummary { get; init; }
    public string? ExistingInUncheckedText { get; set; }
    public string? ExistingInCheckedText { get; set; }
    public string? ExistingOverlapSummary { get; set; }
    public string? ConflictWarning { get; set; }
    public bool WasRemovedFromShoppingList { get; set; }
    public required IReadOnlyList<WeeklyMenuDayContributionDto> SourceContributions { get; init; }
}

public sealed class WeeklyMenuShoppingPreview
{
    public required CalendarWeek CalendarWeek { get; init; }
    public int? WeeklyMenuId { get; init; }
    public bool HasSavedMenu { get; init; }
    public WeeklyMenuStatus Status { get; init; } = WeeklyMenuStatus.Draft;
    public required string PreviewStamp { get; init; }
    public required IReadOnlyList<WeeklyMenuPlannedMealDto> PlannedMeals { get; init; }
    public required IReadOnlyList<WeeklyMenuPreviewItemDto> Items { get; init; }
    public bool HasReplacedOrClearedMealsWithPriorAdditions { get; init; }

    public int NewItemsCount => Items.Count(x => !x.IsAlreadyAdded);
    public int AlreadyAddedCount => Items.Count(x => x.IsAlreadyAdded || !string.IsNullOrWhiteSpace(x.AlreadyAddedSummary));
}

public sealed record WeeklyMenuShoppingConfirmResult(
    WeeklyMenuConfirmStatus Status,
    int AddedItemsCount,
    int AlreadyAddedCount,
    int ConflictCount,
    WeeklyMenuShoppingPreview UpdatedPreview,
    string? Message = null)
{
    public bool IsSuccess => Status is WeeklyMenuConfirmStatus.Success or WeeklyMenuConfirmStatus.NoSelection;
    public bool IsConflict => Status == WeeklyMenuConfirmStatus.Conflict;
}
