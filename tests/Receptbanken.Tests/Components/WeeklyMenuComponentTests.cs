using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Components.WeeklyMenu;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class WeeklyMenuComponentTests : TestContext
{
    private sealed class FixedLocalDateProvider : ILocalDateProvider
    {
        private DateOnly _today;

        public FixedLocalDateProvider(DateOnly today)
        {
            _today = today;
        }

        public DateOnly GetLocalToday() => _today;

        public void SetClientLocalDate(DateOnly clientDate) => _today = clientDate;
    }

    private async Task<(TestDbContextFactory DbFactory, RecipeService RecipeService, CategoryService CategoryService, WeeklyMenuService WeeklyMenuService)> SetupServicesAsync(DateOnly localToday)
    {
        var dbFactory = new TestDbContextFactory();
        await using (var db = dbFactory.CreateDbContext())
        {
            await DbInitializer.InitializeAsync(db, NullLogger.Instance);
        }

        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        var weeklyMenuService = new WeeklyMenuService(dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        var localDateProvider = new FixedLocalDateProvider(localToday);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(localDateProvider);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(pantryMatcher);
        Services.AddSingleton(generator);
        Services.AddSingleton(weeklyMenuService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (dbFactory, recipeService, categoryService, weeklyMenuService);
    }

    [Fact]
    public async Task Home_WhenConfirmedWeeklyMenuExistsForToday_ShouldRenderTodaysDinnerCardWithLinks()
    {
        var localToday = new DateOnly(2026, 11, 2); // Monday, Week 45
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var marryMe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Marry me chicken",
                PrepTimeMinutes = 15,
                CookTimeMinutes = 30,
                Servings = 4,
                ServingsUnit = "portioner",
                Instructions = [new InstructionStep { StepNumber = 1, InstructionText = "Tillaga kycklingen." }]
            },
            [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = marryMe.Id;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".todays-dinner-card");
            card.TextContent.Should().Contain("DAGENS MIDDAG");
            card.TextContent.Should().Contain("Marry me chicken");
            card.TextContent.Should().Contain("45 minuter");
            card.TextContent.Should().Contain("4 portioner");
            card.TextContent.Should().Contain("Vecka 45");

            cut.Find("a.todays-dinner-view-btn").GetAttribute("href").Should().Be($"/recept/{marryMe.Id}");
            cut.Find("a.todays-dinner-cook-btn").GetAttribute("href").Should().Be($"/recept/{marryMe.Id}/laga?portioner=4");
        });
    }

    [Fact]
    public async Task Home_WhenNoConfirmedWeeklyMenuExists_ShouldNotRenderLargeDinnerCard_OnlyDiscreetLink()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, _, _, _) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".todays-dinner-card").Should().BeEmpty();
            var discreet = cut.Find(".todays-dinner-discreet-link");
            discreet.GetAttribute("href").Should().Be("/veckomeny");
            discreet.TextContent.Should().Contain("Vecka 45");
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_GenerateConfirmRerollAndMove_ShouldWorkEndToEnd()
    {
        var localToday = new DateOnly(2026, 11, 2); // Monday, Week 45
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;
        var festId = cats.First(c => c.Name == "Fest & Helg").Id;

        for (int i = 1; i <= 6; i++)
        {
            await recipeService.CreateRecipeAsync(
                new Recipe { Title = $"Vardagsrätt {i}", PrepTimeMinutes = 20, CookTimeMinutes = 15, Servings = 4 },
                [vardagId]);
        }

        for (int i = 1; i <= 3; i++)
        {
            await recipeService.CreateRecipeAsync(
                new Recipe { Title = $"Helgrätt {i}", PrepTimeMinutes = 30, CookTimeMinutes = 30, Servings = 4 },
                [festId]);
        }

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Vecka 45, 2026");
            cut.FindAll(".weekly-day-card").Should().HaveCount(7);
        });

        // 1. Add pantry ingredient "Kycklingfilé"
        var pantryInput = cut.Find("input.pantry-input");
        pantryInput.Input("Kycklingfilé");
        cut.Find("button.pantry-add-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".pantry-tag-chip").Should().ContainSingle(e => e.TextContent.Contains("Kycklingfilé"));
        });

        // 2. Click "Generera veckomeny" -> since pantry was modified, confirm dialog is shown; confirm it
        cut.Find("button.generate-week-btn").Click();
        cut.WaitForAssertion(() =>
        {
            var confirmBtn = cut.FindAll(".modal-dialog-custom button.btn-danger-custom").FirstOrDefault();
            if (confirmBtn != null)
            {
                confirmBtn.Click();
            }
        });

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-recipe-title").Should().HaveCount(7);
        });

        // 3. Re-roll Monday (dayIndex 0) via "Slumpa nytt"
        var mondayTitleBefore = cut.FindAll(".weekly-day-recipe-title").ElementAt(0).TextContent.Trim();
        var tuesdayTitleBefore = cut.FindAll(".weekly-day-recipe-title").ElementAt(1).TextContent.Trim();

        cut.FindAll("button.weekly-day-reroll-btn").ElementAt(0).Click();

        cut.WaitForAssertion(() =>
        {
            var mondayTitleAfter = cut.FindAll(".weekly-day-recipe-title").ElementAt(0).TextContent.Trim();
            mondayTitleAfter.Should().NotBe(mondayTitleBefore);
            cut.FindAll(".weekly-day-recipe-title").ElementAt(1).TextContent.Trim().Should().Be(tuesdayTitleBefore);
        });

        // 4. Move Monday's recipe to Friday (dayIndex 4) via "Flytta till…" while keeping categories on their respective days
        var mondayMovedTitle = cut.FindAll(".weekly-day-recipe-title").ElementAt(0).TextContent.Trim();
        var fridayMovedTitle = cut.FindAll(".weekly-day-recipe-title").ElementAt(4).TextContent.Trim();

        cut.FindAll("button.weekly-day-move-toggle").ElementAt(0).Click();
        cut.Find(".weekly-day-card[data-day-index='0'] button.weekly-day-move-option[data-target-day-index='4']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-recipe-title").ElementAt(0).TextContent.Trim().Should().Be(fridayMovedTitle);
            cut.FindAll(".weekly-day-recipe-title").ElementAt(4).TextContent.Trim().Should().Be(mondayMovedTitle);

            // Categories stay on their day (Monday still has Vardagsmat, Friday still has Fest & Helg)
            cut.Find(".weekly-day-card[data-day-index='0'] .day-category-chip").TextContent.Should().Contain("Vardagsmat");
            cut.Find(".weekly-day-card[data-day-index='4'] .day-category-chip").TextContent.Should().Contain("Fest & Helg");
        });

        // 5. Confirm ("Spika veckomenyn")
        cut.Find("button.confirm-week-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-status-badge.status-confirmed").TextContent.Should().Contain("Spikad");
        });

        var persisted = await weeklyMenuService.GetSavedMenuForWeekAsync(localToday);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(WeeklyMenuStatus.Confirmed);
        persisted.PantryIngredients.Should().Contain("Kycklingfilé");

        // 6. Drag-and-Drop on confirmed menu (Tuesday index 1 -> Wednesday index 2) and explicit save of edited confirmed menu
        var tuesdayRecipeBeforeDrag = cut.FindAll(".weekly-day-recipe-title").ElementAt(1).TextContent.Trim();
        var wednesdayRecipeBeforeDrag = cut.FindAll(".weekly-day-recipe-title").ElementAt(2).TextContent.Trim();

        cut.Find(".weekly-day-card[data-day-index='1']").DragStart();
        cut.Find(".weekly-day-card[data-day-index='2']").Drop();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-recipe-title").ElementAt(1).TextContent.Trim().Should().Be(wednesdayRecipeBeforeDrag);
            cut.FindAll(".weekly-day-recipe-title").ElementAt(2).TextContent.Trim().Should().Be(tuesdayRecipeBeforeDrag);
            cut.Find(".weekly-status-badge.status-unsaved").TextContent.Should().Contain("Osparade ändringar");
        });

        // Save changes explicitly on the confirmed menu
        cut.Find("button.confirm-week-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty();
        });
    }
}

