namespace Receptbanken.Core.Models;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ColorHex { get; set; }
    public List<RecipeCategory> RecipeCategories { get; set; } = new();
}

