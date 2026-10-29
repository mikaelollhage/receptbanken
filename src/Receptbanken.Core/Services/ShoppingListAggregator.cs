using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

public class ShoppingListAggregator
{
    private readonly ServingCalculator _servingCalculator;

    public ShoppingListAggregator(ServingCalculator servingCalculator)
    {
        _servingCalculator = servingCalculator;
    }

    /// <summary>
    /// Aggregates incoming shopping list items into an existing list of unchecked items when
    /// ingredient names and units are compatible. Incompatible units remain separate entries.
    /// </summary>
    public List<ShoppingListItem> MergeItems(
        IEnumerable<ShoppingListItem> existingItems,
        IEnumerable<ShoppingListItem> newItems)
    {
        var result = existingItems.ToList();

        foreach (var incoming in newItems)
        {
            var normalizedName = NormalizeKey(incoming.Name);
            var normalizedUnit = NormalizeUnit(incoming.Unit);

            if (string.IsNullOrWhiteSpace(normalizedName))
                continue;

            var match = result.FirstOrDefault(existing =>
                !existing.IsChecked &&
                NormalizeKey(existing.Name) == normalizedName &&
                NormalizeUnit(existing.Unit) == normalizedUnit &&
                existing.Quantity.HasValue == incoming.Quantity.HasValue);

            if (match != null)
            {
                if (match.Quantity.HasValue && incoming.Quantity.HasValue)
                {
                    match.Quantity = Math.Round(match.Quantity.Value + incoming.Quantity.Value, 2, MidpointRounding.AwayFromZero);
                }

                match.Unit = string.IsNullOrWhiteSpace(normalizedUnit) ? null : normalizedUnit;
                match.RawText = FormatRawText(match.Quantity, match.Unit, match.Name);
            }
            else
            {
                var copy = new ShoppingListItem
                {
                    RecipeId = incoming.RecipeId,
                    Name = incoming.Name.Trim(),
                    Quantity = incoming.Quantity,
                    Unit = string.IsNullOrWhiteSpace(normalizedUnit) ? null : normalizedUnit,
                    Department = incoming.Department,
                    IsChecked = incoming.IsChecked,
                    CreatedAt = incoming.CreatedAt,
                    RawText = FormatRawText(incoming.Quantity, normalizedUnit, incoming.Name.Trim())
                };
                result.Add(copy);
            }
        }

        return result;
    }

    public ShoppingListItem CreateFromRecipeIngredient(
        Ingredient ingredient,
        int baseServings,
        int targetServings,
        int? recipeId = null)
    {
        var scaledQuantity = _servingCalculator.ScaleQuantity(ingredient.Quantity, baseServings, targetServings);
        var unit = NormalizeUnit(ingredient.Unit);
        var name = ingredient.Name.Trim();

        return new ShoppingListItem
        {
            RecipeId = recipeId,
            Name = name,
            Quantity = scaledQuantity,
            Unit = string.IsNullOrWhiteSpace(unit) ? null : unit,
            IsChecked = false,
            CreatedAt = DateTimeOffset.UtcNow,
            RawText = FormatRawText(scaledQuantity, unit, name)
        };
    }

    public string FormatRawText(decimal? quantity, string? unit, string name)
    {
        var parts = new List<string>();
        if (quantity.HasValue)
        {
            parts.Add(_servingCalculator.FormatQuantity(quantity.Value));
        }
        if (!string.IsNullOrWhiteSpace(unit))
        {
            parts.Add(unit.Trim());
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name.Trim());
        }
        return string.Join(" ", parts);
    }

    private static string NormalizeKey(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim().ToLowerInvariant();

    private static string NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return string.Empty;

        var lower = unit.Trim().TrimEnd('.').ToLowerInvariant();
        return lower switch
        {
            "stycken" or "styck" => "st",
            "förpackning" or "förpackningar" => "förp",
            "pkt" => "paket",
            "klyfta" => "klyftor",
            "portion" or "portioner" => "port",
            _ => lower
        };
    }
}

