using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class CategoryServiceTests : IDisposable
{
    private readonly TestDbContextFactory _factory;
    private readonly CategoryService _service;

    public CategoryServiceTests()
    {
        _factory = new TestDbContextFactory();
        _service = new CategoryService(_factory, NullLogger<CategoryService>.Instance);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task CreateCategoryAsync_ShouldAddCategory()
    {
        // Arrange
        var cat = new Category { Name = "Grillat", ColorHex = "#e07a5f" };

        // Act
        var created = await _service.CreateCategoryAsync(cat);

        // Assert
        created.Id.Should().BeGreaterThan(0);
        var categories = await _service.GetCategoriesAsync();
        categories.Should().Contain(c => c.Name == "Grillat");
    }

    [Fact]
    public async Task CreateCategoryAsync_DuplicateName_ShouldThrowException()
    {
        // Arrange
        await _service.CreateCategoryAsync(new Category { Name = "Frukost" });

        // Act & Assert
        var act = async () => await _service.CreateCategoryAsync(new Category { Name = "frukost" });
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*finns redan*");
    }

    [Fact]
    public async Task UpdateCategoryAsync_ShouldUpdateNameAndColor()
    {
        // Arrange
        var cat = await _service.CreateCategoryAsync(new Category { Name = "Vego", ColorHex = "#2a9d8f" });

        // Act
        cat.Name = "Vegetariskt & Veganskt";
        cat.ColorHex = "#8ab17d";
        var updated = await _service.UpdateCategoryAsync(cat);

        // Assert
        updated.Name.Should().Be("Vegetariskt & Veganskt");
        updated.ColorHex.Should().Be("#8ab17d");
    }

    [Fact]
    public async Task DeleteCategoryAsync_ShouldRemoveCategory()
    {
        // Arrange
        var cat = await _service.CreateCategoryAsync(new Category { Name = "Tillfällig" });

        // Act
        await _service.DeleteCategoryAsync(cat.Id);

        // Assert
        var fetched = await _service.GetCategoryByIdAsync(cat.Id);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task GetCategoriesAsync_ShouldCalculateRecipeCountCorrectly()
    {
        // Arrange
        var cat = await _service.CreateCategoryAsync(new Category { Name = "Pasta" });

        await using (var context = _factory.CreateDbContext())
        {
            var recipe = new Recipe
            {
                Title = "Spaghetti Carbonara",
                RecipeCategories = new List<RecipeCategory>
                {
                    new() { CategoryId = cat.Id }
                }
            };
            context.Recipes.Add(recipe);
            await context.SaveChangesAsync();
        }

        // Act
        var categories = await _service.GetCategoriesAsync();

        // Assert
        var pasta = categories.FirstOrDefault(c => c.Name == "Pasta");
        pasta.Should().NotBeNull();
        pasta!.RecipeCount.Should().Be(1);
    }
}

