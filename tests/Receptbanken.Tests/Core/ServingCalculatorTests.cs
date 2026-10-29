using FluentAssertions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class ServingCalculatorTests
{
    private readonly ServingCalculator _calculator = new();

    [Theory]
    [InlineData(2.0, 4, 8, 4.0)]
    [InlineData(1.5, 4, 2, 0.75)]
    [InlineData(100, 4, 6, 150)]
    [InlineData(1.0, 4, 3, 0.75)]
    public void ScaleQuantity_ShouldScaleProportionally(
        decimal original, int baseServings, int targetServings, decimal expected)
    {
        var result = _calculator.ScaleQuantity(original, baseServings, targetServings);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(2.0, "2")]
    [InlineData(1.5, "1,5")]
    [InlineData(0.25, "0,25")]
    [InlineData(0.333, "0,33")]
    public void FormatQuantity_ShouldUseSwedishCommaDecimalsWithoutTrailingZeros(decimal value, string expected)
    {
        _calculator.FormatQuantity(value).Should().Be(expected);
    }

    [Fact]
    public void FormatScaledIngredientPrefix_WithRangeAndUnit_ShouldScaleBothMinAndMax()
    {
        var ingredient = new Ingredient
        {
            Quantity = 1m,
            QuantityMax = 2m,
            Unit = "klyftor",
            Name = "vitlök"
        };

        var prefix = _calculator.FormatScaledIngredientPrefix(ingredient, 4, 8);
        prefix.Should().Be("2–4 klyftor ");
    }

    [Fact]
    public void FormatScaledIngredientPrefix_WhenNoQuantity_ShouldReturnEmptyString()
    {
        var ingredient = new Ingredient
        {
            Quantity = null,
            Unit = null,
            Name = "salt och peppar efter smak"
        };

        _calculator.FormatScaledIngredientPrefix(ingredient, 4, 8).Should().BeEmpty();
    }
}

