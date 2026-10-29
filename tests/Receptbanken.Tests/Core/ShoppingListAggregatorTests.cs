using FluentAssertions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Tests.Core;

public class ShoppingListAggregatorTests
{
    private readonly ShoppingListAggregator _aggregator = new(new ServingCalculator());

    [Fact]
    public void MergeItems_WhenSameNameAndCompatibleUnit_ShouldSumQuantities()
    {
        var existing = new List<ShoppingListItem>
        {
            new() { Id = 1, Name = "Mjölk", Quantity = 2m, Unit = "dl", RawText = "2 dl Mjölk" }
        };

        var incoming = new List<ShoppingListItem>
        {
            new() { Name = "mjölk", Quantity = 3.5m, Unit = "DL" }
        };

        var merged = _aggregator.MergeItems(existing, incoming);

        merged.Should().HaveCount(1);
        merged[0].Quantity.Should().Be(5.5m);
        merged[0].Unit.Should().Be("dl");
        merged[0].RawText.Should().Be("5,5 dl Mjölk");
    }

    [Fact]
    public void MergeItems_WhenSameNameButDifferentUnits_ShouldKeepSeparateEntries()
    {
        var existing = new List<ShoppingListItem>
        {
            new() { Id = 1, Name = "Smör", Quantity = 100m, Unit = "g", RawText = "100 g Smör" }
        };

        var incoming = new List<ShoppingListItem>
        {
            new() { Name = "Smör", Quantity = 2m, Unit = "msk" }
        };

        var merged = _aggregator.MergeItems(existing, incoming);

        merged.Should().HaveCount(2);
        merged[0].Unit.Should().Be("g");
        merged[1].Unit.Should().Be("msk");
    }

    [Fact]
    public void MergeItems_WhenExistingItemIsAlreadyChecked_ShouldCreateNewUncheckedItem()
    {
        var existing = new List<ShoppingListItem>
        {
            new() { Id = 1, Name = "Ägg", Quantity = 2m, Unit = "st", IsChecked = true }
        };

        var incoming = new List<ShoppingListItem>
        {
            new() { Name = "Ägg", Quantity = 4m, Unit = "stycken", IsChecked = false }
        };

        var merged = _aggregator.MergeItems(existing, incoming);

        merged.Should().HaveCount(2);
        merged[0].IsChecked.Should().BeTrue();
        merged[0].Quantity.Should().Be(2m);
        merged[1].IsChecked.Should().BeFalse();
        merged[1].Quantity.Should().Be(4m);
        merged[1].Unit.Should().Be("st");
    }

    [Theory]
    [InlineData(4, null, "portins", "4 portins")]
    [InlineData(4, null, "kycklingfiléer", "4 kycklingfiléer")]
    [InlineData(0.5, "dl", "vetemjöl", "0,5 dl vetemjöl")]
    [InlineData(2, "msk", "olivolja", "2 msk olivolja")]
    [InlineData(2, null, "vitlöksklyftor", "2 vitlöksklyftor")]
    [InlineData(1, "burk", "krossade tomater (à 400 g)", "1 burk krossade tomater (à 400 g)")]
    public void FormatRawText_ShouldMaintainProperSpacingAndSwedishDecimals(
        double quantity, string? unit, string name, string expected)
    {
        var formatted = _aggregator.FormatRawText((decimal)quantity, unit, name);
        formatted.Should().Be(expected);
    }
}

