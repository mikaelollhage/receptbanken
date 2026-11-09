using Receptbanken.Core.Models;

namespace Receptbanken.Infrastructure.Services;

public enum RecipeShoppingConfirmStatus
{
    Success = 0,
    NoSelection = 1,
    Conflict = 2,
    RecipeNotFound = 3
}

public sealed class RecipePreviewItemDto
{
    public required string PreviewItemKey { get; init; }
    public required ShoppingListItem Item { get; init; }
    public bool IsSelectedByDefault { get; init; } = true;
    public string? ExistingInUncheckedText { get; set; }
    public string? ExistingInCheckedText { get; set; }
    public string? ExistingOverlapSummary { get; set; }
    public string? ConflictWarning { get; set; }
}

public sealed class RecipeShoppingPreview
{
    public required int RecipeId { get; init; }
    public required string RecipeTitle { get; init; }
    public required int BaseServings { get; init; }
    public required int TargetServings { get; init; }
    public required string ServingsUnit { get; init; }
    public required string PreviewStamp { get; init; }
    public required IReadOnlyList<RecipePreviewItemDto> Items { get; init; }

    public int TotalItemsCount => Items.Count;
}

public sealed record RecipeShoppingConfirmResult(
    RecipeShoppingConfirmStatus Status,
    int AddedItemsCount,
    RecipeShoppingPreview UpdatedPreview,
    string? Message = null)
{
    public bool IsSuccess => Status is RecipeShoppingConfirmStatus.Success or RecipeShoppingConfirmStatus.NoSelection;
    public bool IsConflict => Status == RecipeShoppingConfirmStatus.Conflict;
}
