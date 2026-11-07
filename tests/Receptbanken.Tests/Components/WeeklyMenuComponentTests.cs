using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
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

        // 4. Move Monday's recipe down to Tuesday (dayIndex 1) via the new drag handle and move-down button
        var mondayMovedTitle = cut.FindAll(".weekly-day-recipe-title").ElementAt(0).TextContent.Trim();
        var tuesdayMovedTitle = cut.FindAll(".weekly-day-recipe-title").ElementAt(1).TextContent.Trim();

        // Old "Flytta till…" button must no longer exist
        cut.FindAll("button.weekly-day-move-toggle").Should().BeEmpty();

        // Click the drag handle to open reorder controls directly under header
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-drag-handle").Click();

        cut.WaitForAssertion(() =>
        {
            var reorderBar = cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-reorder-controls");
            reorderBar.Should().NotBeNull();
            // Monday has disabled "Flytta upp" and active "Flytta ned"
            reorderBar.QuerySelector(".weekly-move-up-btn")!.HasAttribute("disabled").Should().BeTrue();
            reorderBar.QuerySelector(".weekly-move-down-btn")!.HasAttribute("disabled").Should().BeFalse();
        });

        // Click "Flytta ned"
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-move-down-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-recipe-title").ElementAt(0).TextContent.Trim().Should().Be(tuesdayMovedTitle);
            cut.FindAll(".weekly-day-recipe-title").ElementAt(1).TextContent.Trim().Should().Be(mondayMovedTitle);

            // Reorder controls stay open on the moved recipe's new day (Tuesday / index 1)
            var tuesdayReorder = cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-reorder-controls");
            tuesdayReorder.Should().NotBeNull();
            tuesdayReorder.QuerySelectorAll(".weekly-move-up-btn").Should().ContainSingle();
            tuesdayReorder.QuerySelectorAll(".weekly-move-down-btn").Should().ContainSingle();
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

        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").DragStart();
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

    [Fact]
    public async Task WeeklyMenu_DragHandle_OnlyVisibleWhenRecipeIsAssigned()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var recipe = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Enkel pasta", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        // Only Monday has a recipe
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Recipe = recipe;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            // Monday has the drag handle
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-drag-handle").Should().NotBeNull();
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-drag-handle").GetAttribute("draggable").Should().Be("true");

            // Tuesday (empty) must NOT have a drag handle
            cut.FindAll(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").Should().BeEmpty();

            // Neither day has old "Flytta till…" button
            cut.FindAll("button.weekly-day-move-toggle").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task WeeklyMenu_MoveButtons_StayOpenAcrossConsecutiveMoves_AndHandleBounds()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;
        var festId = cats.First(c => c.Name == "Fest & Helg").Id;

        for (int i = 0; i < 7; i++)
        {
            await recipeService.CreateRecipeAsync(
                new Recipe { Title = $"Recept {i}", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
                [vardagId, festId]);
        }

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.Find("button.generate-week-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-recipe-title").Should().HaveCount(7);
        });

        // 1. Click handle on Sunday (index 6, the bottom card)
        cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").GetAttribute("aria-expanded").Should().Be("true");
            var sundayControls = cut.Find(".weekly-day-card[data-day-index='6'] .weekly-day-reorder-controls");
            sundayControls.QuerySelector(".weekly-move-up-btn")!.HasAttribute("disabled").Should().BeFalse();
            // Sunday has disabled "Flytta ned"
            sundayControls.QuerySelector(".weekly-move-down-btn")!.HasAttribute("disabled").Should().BeTrue();
        });

        var sundayRecipeTitle = cut.FindAll(".weekly-day-recipe-title").ElementAt(6).TextContent.Trim();

        // 2. Click "Flytta upp" -> Recipe moves from Sunday (6) to Saturday (5)
        cut.Find(".weekly-day-card[data-day-index='6'] .weekly-move-up-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-recipe-title").ElementAt(5).TextContent.Trim().Should().Be(sundayRecipeTitle);
            // Reorder controls and active state must follow the recipe and stay open on Saturday (5)
            cut.Find(".weekly-day-card[data-day-index='5'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").ClassList.Should().NotContain("is-reorder-active");
            var saturdayControls = cut.Find(".weekly-day-card[data-day-index='5'] .weekly-day-reorder-controls");
            saturdayControls.Should().NotBeNull();
            saturdayControls.QuerySelectorAll(".weekly-move-up-btn").Should().ContainSingle();
            saturdayControls.QuerySelectorAll(".weekly-move-down-btn").Should().ContainSingle();
        });

        // 3. Click "Flytta upp" again without re-opening menu -> Recipe moves from Saturday (5) to Friday (4)
        cut.Find(".weekly-day-card[data-day-index='5'] .weekly-move-up-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-recipe-title").ElementAt(4).TextContent.Trim().Should().Be(sundayRecipeTitle);
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            var fridayControls = cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-reorder-controls");
            fridayControls.Should().NotBeNull();
        });

        // 4. Click handle on Friday to toggle controls closed
        cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-reorder-controls").Should().BeEmpty();
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").ClassList.Should().NotContain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").GetAttribute("aria-expanded").Should().Be("false");
        });
    }

    [Fact]
    public async Task WeeklyMenu_TouchDragInterop_SwapsDaysAndMaintainsCategories()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;
        var festId = cats.First(c => c.Name == "Fest & Helg").Id;

        var r1 = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Måndagssoppa", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);
        var r2 = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Fredagsstek", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [festId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Recipe = r1;
        menu.Days[4].RecipeId = r2.Id;
        menu.Days[4].Recipe = r2;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card").Should().HaveCount(7);
        });

        // Simulate touch drag from Monday (0) to Friday (4) via JSInvokable methods
        await cut.InvokeAsync(() => cut.Instance.OnTouchDragStartDayAsync(0));
        await cut.InvokeAsync(() => cut.Instance.OnTouchDropDayAsync(0, 4));

        cut.WaitForAssertion(() =>
        {
            // Monday now has Fredagsstek, Friday has Måndagssoppa
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Fredagsstek");
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Måndagssoppa");

            // Categories remain on their respective days
            cut.Find(".weekly-day-card[data-day-index='0'] .day-category-chip").TextContent.Should().Contain("Vardagsmat");
            cut.Find(".weekly-day-card[data-day-index='4'] .day-category-chip").TextContent.Should().Contain("Fest & Helg");

            // Unsaved changes badge is visible
            cut.Find(".weekly-status-badge.status-unsaved").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task WeeklyMenu_KeyboardArrowKeys_ConsecutiveMovesFollowSameRecipe_AndRespectBoundaries()
    {
        var localToday = new DateOnly(2026, 11, 2); // Monday, Week 45
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        // Recipes on Wednesday (2), Thursday (3), Friday (4), Saturday (5)
        var pasta = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Pasta", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);
        var fisk = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Fisk", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);
        var gryta = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Gryta", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);
        var soppa = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Soppa", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[2].RecipeId = pasta.Id;
        menu.Days[2].Recipe = pasta;
        menu.Days[3].RecipeId = fisk.Id;
        menu.Days[3].Recipe = fisk;
        menu.Days[4].RecipeId = gryta.Id;
        menu.Days[4].Recipe = gryta;
        menu.Days[5].RecipeId = soppa.Id;
        menu.Days[5].Recipe = soppa;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card").Should().HaveCount(7);
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        // 0. ArrowUp / ArrowDown with closed (inactive) menu must NOT move anything
        cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Fisk");
            cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty("piltangenter i inaktivt läge ska inte ändra menyn");
        });

        // Activate reorder mode on Wednesday (2) -> mere activation does not mark unsaved changes
        cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").GetAttribute("aria-expanded").Should().Be("true");
            cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty("enbart aktivering av flyttläge ska inte ge osparade ändringar");
        });

        // 1. Move Pasta down from Wednesday (2) to Thursday (3) via ArrowDown
        cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Fisk");
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
        });

        // 2. Press ArrowDown from the new position (Thursday, 3) -> Pasta moves to Friday (4)
        cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
        });

        // 3. Press ArrowDown from Friday (4) -> Pasta moves to Saturday (5)
        cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='5'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Soppa");
            cut.Find(".weekly-day-card[data-day-index='5'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
        });

        // 4. Now press ArrowUp 3 times in succession to bring Pasta back: Saturday (5) -> Friday (4) -> Thursday (3) -> Wednesday (2)
        cut.Find(".weekly-day-card[data-day-index='5'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Fisk");
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.Find(".weekly-day-card[data-day-index='5'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Soppa");
        });

        // 5. Test boundary on Monday (0): Move Pasta to Tuesday (1) then Monday (0)
        cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        // Press ArrowUp on Monday (0): Pasta must stay on Monday
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        // 6. Test boundary on Sunday (6): Move Pasta down 6 times until Sunday
        for (int i = 0; i < 6; i++)
        {
            int currentDay = i;
            cut.Find($".weekly-day-card[data-day-index='{currentDay}'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
            int nextDay = i + 1;
            cut.WaitForAssertion(() =>
            {
                cut.Find($".weekly-day-card[data-day-index='{nextDay}'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            });
        }

        // Now on Sunday (6), press ArrowDown: Pasta must stay on Sunday
        cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        // 7. Escape closes the menu and deactivates the handle; subsequent ArrowUp does nothing
        cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-reorder-controls").Should().BeEmpty();
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").ClassList.Should().NotContain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").GetAttribute("aria-expanded").Should().Be("false");
        });

        cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });
    }

    [Fact]
    public async Task WeeklyMenu_ActiveReorderState_FollowsRecipeAcrossInteractions()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var pasta = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Pasta", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);
        var fisk = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Fisk", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);
        var gryta = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Gryta", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        // Monday (0): Pasta, Wednesday (2): Fisk, Thursday (3): Gryta. Tuesday (1) is empty!
        menu.Days[0].RecipeId = pasta.Id;
        menu.Days[0].Recipe = pasta;
        menu.Days[2].RecipeId = fisk.Id;
        menu.Days[2].Recipe = fisk;
        menu.Days[3].RecipeId = gryta.Id;
        menu.Days[3].Recipe = gryta;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card").Should().HaveCount(7);
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Fisk");
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
        });

        // 1. Click handle on Wednesday (2) -> opens menu and sets .is-reorder-active and .reorder-open
        cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").GetAttribute("aria-expanded").Should().Be("true");
            cut.Find(".weekly-day-card[data-day-index='2']").ClassList.Should().Contain("reorder-open");
            cut.FindAll(".weekly-day-reorder-controls").Should().ContainSingle();
        });

        // 2. Click handle on Thursday (3) -> only Thursday is active, Wednesday is deactivated
        cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").ClassList.Should().NotContain("is-reorder-active");
            cut.FindAll(".weekly-day-reorder-controls").Should().ContainSingle();
            cut.FindAll(".weekly-day-card.reorder-open").Should().ContainSingle();
        });

        // 3. Click Thursday again -> closes menu, no handle is active
        cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-drag-handle.is-reorder-active").Should().BeEmpty();
            cut.FindAll(".weekly-day-reorder-controls").Should().BeEmpty();
            cut.FindAll(".weekly-day-card.reorder-open").Should().BeEmpty();
        });

        // 4. Native HTML5 drag with open menu:
        // Open menu on Thursday (3) (Gryta), drag Thursday (3) to Wednesday (2) (Fisk)
        cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
        });

        // Simulate HTML5 dragstart on 3, drop on 2, then dragend on 3
        cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").DragStart();
        cut.Find(".weekly-day-card[data-day-index='2']").Drop();
        cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").DragEnd();

        cut.WaitForAssertion(() =>
        {
            // Wednesday now has Gryta (dragged recipe), Thursday has Fisk
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Fisk");

            // Active reorder menu and .is-reorder-active followed Gryta to Wednesday (2)
            cut.Find(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='2']").ClassList.Should().Contain("reorder-open");
            cut.FindAll(".weekly-day-card[data-day-index='2'] .weekly-day-reorder-controls").Should().ContainSingle();

            // Thursday does NOT have active highlight or controls
            cut.Find(".weekly-day-card[data-day-index='3'] .weekly-drag-handle").ClassList.Should().NotContain("is-reorder-active");
            cut.FindAll(".weekly-day-card[data-day-index='3'] .weekly-day-reorder-controls").Should().BeEmpty();
        });

        // 5. Touch drop with open menu:
        // Gryta on Wednesday (2) has open menu. Simulate touch drag to Friday (4) (empty)
        await cut.InvokeAsync(() => cut.Instance.OnTouchDragStartDayAsync(2));
        await cut.InvokeAsync(() => cut.Instance.OnTouchDropDayAsync(2, 4));

        cut.WaitForAssertion(() =>
        {
            // Friday (4) now has Gryta, Wednesday (2) is empty
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.FindAll(".weekly-day-card[data-day-index='2'] .weekly-day-recipe-title").Should().BeEmpty();

            // Active reorder state followed Gryta to Friday (4)
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.FindAll(".weekly-day-card[data-day-index='4'] .weekly-day-reorder-controls").Should().ContainSingle();

            // Wednesday (2) is empty and has no handle
            cut.FindAll(".weekly-day-card[data-day-index='2'] .weekly-drag-handle").Should().BeEmpty();
        });

        // 6. Drag with closed menu:
        // Close menu on Friday (4)
        cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-reorder-controls").Should().BeEmpty());

        // Drag Friday (4) to Saturday (5)
        cut.Find(".weekly-day-card[data-day-index='4'] .weekly-drag-handle").DragStart();
        cut.Find(".weekly-day-card[data-day-index='5']").Drop();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='5'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            // No menu was opened by the drag
            cut.FindAll(".weekly-day-reorder-controls").Should().BeEmpty();
            cut.FindAll(".weekly-drag-handle.is-reorder-active").Should().BeEmpty();
        });

        // 7. Move to empty day with open menu:
        // Open menu on Saturday (5) (Gryta)
        cut.Find(".weekly-day-card[data-day-index='5'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() => cut.Find(".weekly-day-card[data-day-index='5'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active"));

        // Drag to Tuesday (1) (empty)
        cut.Find(".weekly-day-card[data-day-index='5'] .weekly-drag-handle").DragStart();
        cut.Find(".weekly-day-card[data-day-index='1']").Drop();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.FindAll(".weekly-day-card[data-day-index='1'] .weekly-day-reorder-controls").Should().ContainSingle();

            cut.FindAll(".weekly-day-card[data-day-index='5'] .weekly-drag-handle").Should().BeEmpty();
        });

        // 8. Cancelled drag without drop (Escape / dropped outside):
        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").DragStart();
        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").DragEnd();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.FindAll(".weekly-day-card[data-day-index='1'] .weekly-day-reorder-controls").Should().ContainSingle();
        });

        // Drop on same day: state remains on Tuesday (1)
        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").DragStart();
        cut.Find(".weekly-day-card[data-day-index='1']").Drop();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.FindAll(".weekly-day-card[data-day-index='1'] .weekly-day-reorder-controls").Should().ContainSingle();
        });

        // Cancelled touch drag: state remains on Tuesday (1)
        await cut.InvokeAsync(() => cut.Instance.OnTouchDragStartDayAsync(1));
        await cut.InvokeAsync(() => cut.Instance.OnTouchDragCancelDayAsync());

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
        });

        // 9. Piltangenter med stängd meny flyttar ingenting (C1.5):
        // Close menu on Tuesday (1)
        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-reorder-controls").Should().BeEmpty());

        // Press ArrowDown on Tuesday (1) while inactive -> Gryta must remain on Tuesday (1)
        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Gryta");
            cut.FindAll(".weekly-day-reorder-controls").Should().BeEmpty();
            cut.FindAll(".weekly-drag-handle.is-reorder-active").Should().BeEmpty();
        });

        // 10. Saving draft works cleanly while reorder menu is open
        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-reorder-controls").Should().ContainSingle());

        cut.Find("button.save-draft-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task WeeklyMenu_DuplicateRecipeOnMultipleDays_DoesNotConfuseReorderState()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var pasta = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Pasta", PrepTimeMinutes = 10, CookTimeMinutes = 10, Servings = 4 },
            [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        // Both Monday (0) and Sunday (6) have Pasta
        menu.Days[0].RecipeId = pasta.Id;
        menu.Days[0].Recipe = pasta;
        menu.Days[6].RecipeId = pasta.Id;
        menu.Days[6].Recipe = pasta;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card").Should().HaveCount(7);
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
        });

        // 1. Open menu on Monday (0)
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-drag-handle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").ClassList.Should().NotContain("is-reorder-active");
            cut.FindAll(".weekly-day-reorder-controls").Should().ContainSingle();
        });

        // 2. Move Monday (0) to Tuesday (1) via "Flytta ned"
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-move-down-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='1'] .weekly-drag-handle").ClassList.Should().Contain("is-reorder-active");
            // Sunday (6) is unaffected
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-day-recipe-title").TextContent.Trim().Should().Be("Pasta");
            cut.Find(".weekly-day-card[data-day-index='6'] .weekly-drag-handle").ClassList.Should().NotContain("is-reorder-active");
        });
    }
}

