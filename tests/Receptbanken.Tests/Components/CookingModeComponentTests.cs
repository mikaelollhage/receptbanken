using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;

namespace Receptbanken.Tests.Components;

public class CookingModeComponentTests : TestContext
{
    [Fact]
    public async Task CookingMode_ShouldNavigateStepsAndToggleIngredientDrawer()
    {
        using var dbFactory = new TestDbContextFactory();
        var recipeService = new RecipeService(dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        var created = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Chokladbollar",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 100m, Unit = "g", Name = "smör", RawText = "100 g smör" },
                    new Ingredient { OrderIndex = 2, Quantity = 3m, Unit = "dl", Name = "havregryn", RawText = "3 dl havregryn" }
                ],
                Instructions =
                [
                    new InstructionStep { StepNumber = 1, InstructionText = "Rör ihop smör och socker." },
                    new InstructionStep { StepNumber = 2, InstructionText = "Rulla i pärlsocker och ställ i kylen." }
                ]
            },
            []);

        Services.AddSingleton(recipeService);
        Services.AddSingleton(new ServingCalculator());
        JSInterop.Mode = JSRuntimeMode.Loose;

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/recept/{created.Id}/laga?portioner=8");

        var cut = RenderComponent<CookingMode>(parameters => parameters
            .Add(p => p.Id, created.Id));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Steg 1 av 2");
            cut.Markup.Should().Contain("Rör ihop smör och socker.");
        });

        // Click "Nästa steg →"
        var nextBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Nästa steg"));
        nextBtn.Click();

        cut.Markup.Should().Contain("Steg 2 av 2");
        cut.Markup.Should().Contain("Rulla i pärlsocker och ställ i kylen.");

        // Toggle ingredients drawer and verify scaled quantity (4 -> 8 servings => 200 g smör)
        var toggleIngBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Visa ingredienser"));
        toggleIngBtn.Click();

        cut.Markup.Should().Contain("200 g");
        cut.Markup.Should().Contain("smör");
    }
}

