using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Models;

namespace Receptbanken.Infrastructure.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(AppDbContext context, ILogger logger, CancellationToken cancellationToken = default)
    {
        // Apply EF Core migrations to create/update database schema
        await context.Database.MigrateAsync(cancellationToken);

        // Enable SQLite WAL mode and foreign keys
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);

        // Seed default categories if none exist
        if (!await context.Categories.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Initierar standardkategorier...");

            var categories = new List<Category>
            {
                new() { Name = "Vardagsmat", ColorHex = "#e07a5f" },
                new() { Name = "Fest & Helg", ColorHex = "#f2a65a" },
                new() { Name = "Pasta", ColorHex = "#e76f51" },
                new() { Name = "Vegetariskt", ColorHex = "#2a9d8f" },
                new() { Name = "Soppor & Grytor", ColorHex = "#264653" },
                new() { Name = "Bakning & Desserter", ColorHex = "#e9c46a" }
            };

            context.Categories.AddRange(categories);
            await context.SaveChangesAsync(cancellationToken);

            // Add an initial classic recipe: "Klassiska Svenska Pannkakor"
            var vardagsmat = categories.First(c => c.Name == "Vardagsmat");
            var sampleRecipe = new Recipe
            {
                Title = "Klassiska Svenska Pannkakor",
                Description = "Frasiga, gyllengula tunna pannkakor som passar perfekt till torsdagsmiddagen eller helgbrunchen.",
                PrepTimeMinutes = 10,
                CookTimeMinutes = 20,
                Servings = 4,
                ServingsUnit = "portioner",
                Notes = "Låt gärna smeten vila i 15-20 minuter innan gräddning så att mjölet sväller och pannkakorna blir extra fina.",
                IsFavorite = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Ingredients = new List<Ingredient>
                {
                    new() { OrderIndex = 1, RawText = "2,5 dl vetemjöl", Quantity = 2.5m, Unit = "dl", Name = "vetemjöl" },
                    new() { OrderIndex = 2, RawText = "0,5 tsk salt", Quantity = 0.5m, Unit = "tsk", Name = "salt" },
                    new() { OrderIndex = 3, RawText = "6 dl mjölk", Quantity = 6m, Unit = "dl", Name = "mjölk" },
                    new() { OrderIndex = 4, RawText = "3 st ägg", Quantity = 3m, Unit = "st", Name = "ägg" },
                    new() { OrderIndex = 5, RawText = "3 msk smör, smält", Quantity = 3m, Unit = "msk", Name = "smör", Preparation = "smält" },
                    new() { OrderIndex = 6, RawText = "sylt och grädde till servering", Name = "sylt och grädde", Preparation = "till servering", GroupHeader = "Till servering" }
                },
                Instructions = new List<InstructionStep>
                {
                    new() { StepNumber = 1, InstructionText = "Blanda vetemjöl och salt i en rymlig bunke." },
                    new() { StepNumber = 2, InstructionText = "Tillsätt hälften av mjölken och vispa till en helt slät smet utan klumpar." },
                    new() { StepNumber = 3, InstructionText = "Häll i resten av mjölken och vispa ner äggen ett i taget. Rör till sist ner det smälta smöret." },
                    new() { StepNumber = 4, InstructionText = "Hetta upp en stekpanna eller pannkakslagg med en liten klick smör. Häll i ett tunt lager smet och grädda tills ovansidan stelnat och undersidan fått fin färg." },
                    new() { StepNumber = 5, InstructionText = "Vänd pannkakan och grädda andra sidan i ca 1 minut. Servera nystekta med sylt och lättvispad grädde!" }
                },
                RecipeCategories = new List<RecipeCategory>
                {
                    new() { CategoryId = vardagsmat.Id }
                }
            };

            context.Recipes.Add(sampleRecipe);
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Skapade introduktionsrecept: {Title}", sampleRecipe.Title);
        }
    }
}

