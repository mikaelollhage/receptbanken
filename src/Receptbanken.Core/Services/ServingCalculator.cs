using System.Globalization;
using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

public class ServingCalculator
{
    private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");

    public decimal? ScaleQuantity(decimal? originalQuantity, int baseServings, int targetServings)
    {
        if (!originalQuantity.HasValue || baseServings <= 0 || targetServings <= 0)
            return originalQuantity;

        if (baseServings == targetServings)
            return originalQuantity.Value;

        var scaled = originalQuantity.Value * targetServings / baseServings;
        return Math.Round(scaled, 2, MidpointRounding.AwayFromZero);
    }

    public string FormatQuantity(decimal? quantity)
    {
        if (!quantity.HasValue)
            return string.Empty;

        var rounded = Math.Round(quantity.Value, 2, MidpointRounding.AwayFromZero);
        if (rounded == Math.Floor(rounded))
        {
            return ((int)rounded).ToString(SwedishCulture);
        }

        return rounded.ToString("0.##", SwedishCulture);
    }

    public string FormatQuantityRange(decimal? quantity, decimal? quantityMax)
    {
        if (!quantity.HasValue)
            return string.Empty;

        var minFormatted = FormatQuantity(quantity);
        if (quantityMax.HasValue && quantityMax.Value > quantity.Value)
        {
            var maxFormatted = FormatQuantity(quantityMax);
            return $"{minFormatted}–{maxFormatted}";
        }

        return minFormatted;
    }

    public string FormatScaledIngredientPrefix(Ingredient ingredient, int baseServings, int targetServings)
    {
        if (!ingredient.Quantity.HasValue || baseServings <= 0 || targetServings <= 0)
        {
            return !string.IsNullOrWhiteSpace(ingredient.Unit)
                ? $"{ingredient.Unit.Trim()} "
                : string.Empty;
        }

        var minScaled = ScaleQuantity(ingredient.Quantity, baseServings, targetServings);
        var minFormatted = FormatQuantity(minScaled);

        string quantityPart = minFormatted;
        if (ingredient.QuantityMax.HasValue && ingredient.QuantityMax.Value > ingredient.Quantity.Value)
        {
            var maxScaled = ScaleQuantity(ingredient.QuantityMax, baseServings, targetServings);
            var maxFormatted = FormatQuantity(maxScaled);
            quantityPart = $"{minFormatted}–{maxFormatted}";
        }

        if (!string.IsNullOrWhiteSpace(ingredient.Unit))
        {
            return $"{quantityPart} {ingredient.Unit.Trim()} ";
        }

        return $"{quantityPart} ";
    }
}

