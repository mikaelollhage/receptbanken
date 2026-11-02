using System.ComponentModel.DataAnnotations.Schema;

namespace Receptbanken.Core.Models;

public class WeeklyMenu
{
    public int Id { get; set; }

    /// <summary>
    /// Monday of the ISO 8601 calendar week. Unique identifier for the week.
    /// </summary>
    public DateOnly WeekStartDate { get; set; }

    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }

    public WeeklyMenuStatus Status { get; set; } = WeeklyMenuStatus.Draft;
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WeeklyMenuPantryItem> PantryItems { get; set; } = new();
    public List<WeeklyMenuDay> Days { get; set; } = new();

    [NotMapped]
    public List<string> PantryIngredients
    {
        get => PantryItems
            .OrderBy(p => p.OrderIndex)
            .Select(p => p.IngredientName)
            .ToList();
        set
        {
            PantryItems = (value ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((name, idx) => new WeeklyMenuPantryItem
                {
                    WeeklyMenuId = Id,
                    OrderIndex = idx + 1,
                    IngredientName = name
                })
                .ToList();
        }
    }
}

public class WeeklyMenuDay
{
    public int Id { get; set; }
    public int WeeklyMenuId { get; set; }
    public WeeklyMenu? WeeklyMenu { get; set; }

    /// <summary>
    /// 0 = Måndag .. 6 = Söndag
    /// </summary>
    public int DayIndex { get; set; }

    public DateOnly Date { get; set; }

    public int? RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    public int? Servings { get; set; }

    public List<WeeklyMenuDayCategory> SelectedCategories { get; set; } = new();

    /// <summary>
    /// Transient message shown when generation or re-rolling could not find a candidate for this day.
    /// </summary>
    [NotMapped]
    public string? StatusMessage { get; set; }
}

public class WeeklyMenuDayCategory
{
    public int WeeklyMenuDayId { get; set; }
    public WeeklyMenuDay? WeeklyMenuDay { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }
}

public class WeeklyMenuPantryItem
{
    public int Id { get; set; }
    public int WeeklyMenuId { get; set; }
    public WeeklyMenu? WeeklyMenu { get; set; }
    public int OrderIndex { get; set; }
    public string IngredientName { get; set; } = string.Empty;
}

