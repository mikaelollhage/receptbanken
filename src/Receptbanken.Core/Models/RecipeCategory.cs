namespace Receptbanken.Core.Models;

public class RecipeCategory
{
    public int RecipeId { get; set; }
    public Recipe Recipe { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
}

