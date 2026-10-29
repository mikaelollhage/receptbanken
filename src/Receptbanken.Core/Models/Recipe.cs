namespace Receptbanken.Core.Models;

public class Recipe
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceHost { get; set; }
    public string? ImagePath { get; set; }
    public int? PrepTimeMinutes { get; set; }
    public int? CookTimeMinutes { get; set; }
    public int Servings { get; set; } = 4;
    public string ServingsUnit { get; set; } = "portioner";
    public string? Notes { get; set; }
    public bool IsFavorite { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int? TotalTimeMinutes =>
        (PrepTimeMinutes ?? 0) + (CookTimeMinutes ?? 0) > 0
            ? (PrepTimeMinutes ?? 0) + (CookTimeMinutes ?? 0)
            : null;

    public List<Ingredient> Ingredients { get; set; } = new();
    public List<InstructionStep> Instructions { get; set; } = new();
    public List<RecipeCategory> RecipeCategories { get; set; } = new();
}

