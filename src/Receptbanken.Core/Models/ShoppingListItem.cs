namespace Receptbanken.Core.Models;

public class ShoppingListItem
{
    public int Id { get; set; }
    public int? RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public string RawText { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public decimal? QuantityMax { get; set; }
    public string? Unit { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public bool IsChecked { get; set; }
    public int SortOrder { get; set; }
    public string? Department { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

