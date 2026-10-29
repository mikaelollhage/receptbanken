using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class MockImageStorageService : IImageStorageService
{
    public List<string> DeletedImages { get; } = new();

    public Task DeleteImageAsync(string? imagePath, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            DeletedImages.Add(imagePath);
        }
        return Task.CompletedTask;
    }

    public bool IsValidImageExtension(string fileName) => true;

    public Task<string> SaveImageAsync(Stream stream, string originalFileName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"images/recipes/{Guid.NewGuid():N}.webp");
    }

    public bool ValidateImageBytes(ReadOnlySpan<byte> headerBytes) => true;
}

public class TestDbContextFactory : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly string _dbPath;

    public TestDbContextFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"receptbanken_test_{Guid.NewGuid():N}.db");
        using var context = CreateDbContext();
        context.Database.Migrate();
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        return new AppDbContext(options);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch { }
    }
}

public class RecipeServiceTests : IDisposable
{
    private readonly TestDbContextFactory _factory;
    private readonly MockImageStorageService _mockImageStorage;
    private readonly RecipeService _service;

    public RecipeServiceTests()
    {
        _factory = new TestDbContextFactory();
        _mockImageStorage = new MockImageStorageService();
        _service = new RecipeService(_factory, _mockImageStorage, NullLogger<RecipeService>.Instance);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task CreateRecipeAsync_ShouldPersistRecipeWithIngredientsAndInstructions()
    {
        // Arrange
        var recipe = new Recipe
        {
            Title = "Krämig Kantarellpasta",
            Description = "En ljuvlig höstklassiker med färska kantareller och parmesan.",
            PrepTimeMinutes = 15,
            CookTimeMinutes = 20,
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients = new List<Ingredient>
            {
                new() { Name = "kantareller", Quantity = 300, Unit = "g", RawText = "300 g färska kantareller" },
                new() { Name = "tagliatelle", Quantity = 400, Unit = "g", RawText = "400 g tagliatelle" },
                new() { Name = "vispgrädde", Quantity = 2.5m, Unit = "dl", RawText = "2,5 dl vispgrädde" }
            },
            Instructions = new List<InstructionStep>
            {
                new() { InstructionText = "Rensa och stek svampen torr i en panna tills vätskan kokat in." },
                new() { InstructionText = "Tillsätt smör och scharlottenlök och stek glansigt." },
                new() { InstructionText = "Slå på grädden och låt sjuda ihop medan pastan kokar." }
            }
        };

        // Act
        var created = await _service.CreateRecipeAsync(recipe);

        // Assert
        created.Id.Should().BeGreaterThan(0);

        var fetched = await _service.GetRecipeByIdAsync(created.Id);
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("Krämig Kantarellpasta");
        fetched.Ingredients.Should().HaveCount(3);
        fetched.Instructions.Should().HaveCount(3);
        fetched.Ingredients[0].OrderIndex.Should().Be(1);
        fetched.Instructions[0].StepNumber.Should().Be(1);
    }

    [Fact]
    public async Task GetRecipesAsync_ShouldFilterBySearchQueryAcrossTitleAndIngredients()
    {
        // Arrange
        var r1 = new Recipe
        {
            Title = "Klassisk Bolognese",
            Description = "Långkokt italiensk köttfärssås.",
            Ingredients = new List<Ingredient>
            {
                new() { Name = "nötfärs", Quantity = 500, Unit = "g", RawText = "500 g nötfärs" }
            }
        };

        var r2 = new Recipe
        {
            Title = "Grön Ärtsoppa",
            Description = "Fräsch soppa med mynta.",
            Ingredients = new List<Ingredient>
            {
                new() { Name = "gröna ärtor", Quantity = 400, Unit = "g", RawText = "400 g frysta ärtor" }
            }
        };

        await _service.CreateRecipeAsync(r1);
        await _service.CreateRecipeAsync(r2);

        // Act
        var resultsTitle = await _service.GetRecipesAsync(searchQuery: "bolognese");
        var resultsIng = await _service.GetRecipesAsync(searchQuery: "ärtor");

        // Assert
        resultsTitle.Should().ContainSingle(r => r.Title == "Klassisk Bolognese");
        resultsIng.Should().ContainSingle(r => r.Title == "Grön Ärtsoppa");
    }

    [Fact]
    public async Task GetRecipesAsync_ShouldFilterByFavorites()
    {
        // Arrange
        var r1 = new Recipe { Title = "Favoritkaka", IsFavorite = true };
        var r2 = new Recipe { Title = "Vardagsgröt", IsFavorite = false };

        await _service.CreateRecipeAsync(r1);
        await _service.CreateRecipeAsync(r2);

        // Act
        var favs = await _service.GetRecipesAsync(favoritesOnly: true);

        // Assert
        favs.Should().ContainSingle(r => r.Title == "Favoritkaka");
        favs.Should().NotContain(r => r.Title == "Vardagsgröt");
    }

    [Fact]
    public async Task ToggleFavoriteAsync_ShouldInvertFavoriteFlag()
    {
        // Arrange
        var r = await _service.CreateRecipeAsync(new Recipe { Title = "Testrätt", IsFavorite = false });

        // Act & Assert 1
        var fav1 = await _service.ToggleFavoriteAsync(r.Id);
        fav1.Should().BeTrue();

        // Act & Assert 2
        var fav2 = await _service.ToggleFavoriteAsync(r.Id);
        fav2.Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateRecipeAsync_ShouldCreateExactCopyExceptTitleAndId()
    {
        // Arrange
        var original = await _service.CreateRecipeAsync(new Recipe
        {
            Title = "Chili con Carne",
            Description = "Het gryta med bönor och spiskummin.",
            Servings = 6,
            Ingredients = new List<Ingredient>
            {
                new() { Name = "svarta bönor", Quantity = 1, Unit = "burk", RawText = "1 burk svarta bönor" }
            },
            Instructions = new List<InstructionStep>
            {
                new() { InstructionText = "Bryn färsen och kryddorna." }
            }
        });

        // Act
        var copy = await _service.DuplicateRecipeAsync(original.Id);

        // Assert
        copy.Id.Should().NotBe(original.Id);
        copy.Title.Should().Be("Chili con Carne (Kopia)");
        copy.Servings.Should().Be(6);
        copy.Ingredients.Should().HaveCount(1);
        copy.Ingredients[0].Name.Should().Be("svarta bönor");
        copy.Instructions.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteRecipeAsync_ShouldCascadeDeleteIngredientsAndInstructions_AndRemoveImage()
    {
        // Arrange
        var recipe = await _service.CreateRecipeAsync(new Recipe
        {
            Title = "Recept som ska raderas",
            ImagePath = "images/recipes/test-to-delete.webp",
            Ingredients = new List<Ingredient>
            {
                new() { Name = "test råvara", RawText = "test" }
            },
            Instructions = new List<InstructionStep>
            {
                new() { InstructionText = "test steg" }
            }
        });

        // Act
        await _service.DeleteRecipeAsync(recipe.Id);

        // Assert
        var fetched = await _service.GetRecipeByIdAsync(recipe.Id);
        fetched.Should().BeNull();

        await using var context = _factory.CreateDbContext();
        var remainingIngs = await context.Ingredients.Where(i => i.RecipeId == recipe.Id).ToListAsync();
        remainingIngs.Should().BeEmpty();

        var remainingSteps = await context.Instructions.Where(s => s.RecipeId == recipe.Id).ToListAsync();
        remainingSteps.Should().BeEmpty();

        _mockImageStorage.DeletedImages.Should().Contain("images/recipes/test-to-delete.webp");
    }
}

