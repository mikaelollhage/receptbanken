namespace Receptbanken.Core.Models;

public class Ingredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public int OrderIndex { get; set; }
    public string RawText { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public decimal? QuantityMax { get; set; }
    public string? Unit { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public string? Preparation { get; set; }
    public string? GroupHeader { get; set; }
}

