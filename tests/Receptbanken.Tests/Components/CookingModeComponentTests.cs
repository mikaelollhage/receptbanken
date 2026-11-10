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
        nextBtn.ClassList.Should().Contain("btn-primary-custom");
        nextBtn.ClassList.Should().Contain("btn-lg");
        nextBtn.Click();

        cut.Markup.Should().Contain("Steg 2 av 2");
        cut.Markup.Should().Contain("Rulla i pärlsocker och ställ i kylen.");

        // On the final step, verify completion button has primary-custom and btn-lg without green inline background
        var completeLink = cut.FindAll("a").First(a => a.TextContent.Contains("Klart! Tillbaka till receptet"));
        completeLink.ClassList.Should().Contain("btn-primary-custom");
        completeLink.ClassList.Should().Contain("btn-lg");
        var completeStyle = completeLink.GetAttribute("style") ?? string.Empty;
        completeStyle.Should().NotContain("#16a34a");
        completeStyle.Should().NotContain("background");
        completeLink.GetAttribute("href").Should().Be($"/recept/{created.Id}");

        // Previous step button has btn-secondary-custom and btn-lg
        var prevBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Föregående steg"));
        prevBtn.ClassList.Should().Contain("btn-secondary-custom");
        prevBtn.ClassList.Should().Contain("btn-lg");

        // Toggle ingredients drawer and verify scaled quantity (4 -> 8 servings => 200 g smör)
        var toggleIngBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Visa ingredienser"));
        toggleIngBtn.Click();

        cut.Markup.Should().Contain("200 g");
        cut.Markup.Should().Contain("smör");
    }
}

