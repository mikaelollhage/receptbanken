namespace Receptbanken.Core.Models;

public enum ShoppingListReorderStatus
{
    Success,
    NoChange,
    VersionConflict,
    InvalidOperation
}

public sealed record ShoppingListReorderResult(
    ShoppingListReorderStatus Status,
    long CurrentOrderingVersion,
    IReadOnlyList<ShoppingListItem> Items,
    string? ErrorMessage = null)
{
    public bool Succeeded => Status is ShoppingListReorderStatus.Success or ShoppingListReorderStatus.NoChange;
    public bool IsConflict => Status == ShoppingListReorderStatus.VersionConflict;
}
