using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
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
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();
        var notifier = new ShoppingListNotifier();
        var shoppingService = new ShoppingListService(dbFactory, aggregator, parser, notifier);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(localDateProvider);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(pantryMatcher);
        Services.AddSingleton(generator);
        Services.AddSingleton(weeklyMenuService);
        Services.AddSingleton(calc);
        Services.AddSingleton(aggregator);
        Services.AddSingleton(parser);
        Services.AddSingleton(notifier);
        Services.AddSingleton(shoppingService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (dbFactory, recipeService, categoryService, weeklyMenuService);
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

    [Fact]
    public async Task K5_29_PortionAdjuster_ShownOnlyOnDaysWithRecipe_UpdatesServings_MarksUnsaved_AndFollowsRecipeOnSwap()
    {
        var localToday = new DateOnly(2026, 11, 2); // Vecka 45
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var pasta = await recipeService.CreateRecipeAsync(
            new Recipe { Title = "Pasta Bolognese", PrepTimeMinutes = 15, CookTimeMinutes = 20, Servings = 4 },
            [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        // Endast måndag (index 0) har recept, övriga dagar är tomma
        menu.Days[0].RecipeId = pasta.Id;
        menu.Days[0].Recipe = pasta;
        menu.Days[0].Servings = 4;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            // Måndag visar portionsjusterare med 4 portioner
            cut.FindAll(".weekly-day-card[data-day-index='0'] .weekly-day-servings-control .servings-adjuster").Should().ContainSingle();
            cut.Find(".weekly-day-card[data-day-index='0'] .servings-count").TextContent.Trim().Should().Be("4 portioner");

            // Tisdag (tom dag) har ingen portionsjusterare
            cut.FindAll(".weekly-day-card[data-day-index='1'] .weekly-day-servings-control").Should().BeEmpty();
        });

        // Öka måndagens portioner från 4 -> 6 via '+'-knappen (andra .servings-btn)
        cut.FindAll(".weekly-day-card[data-day-index='0'] .servings-btn").ElementAt(1).Click();
        cut.FindAll(".weekly-day-card[data-day-index='0'] .servings-btn").ElementAt(1).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .servings-count").TextContent.Trim().Should().Be("6 portioner");
            cut.Find(".weekly-status-badge.status-unsaved").TextContent.Should().Contain("Osparade ändringar");
        });

        // Flytta måndagens recept till tisdag via drag-and-drop -> portionsantalet (6) följer med till tisdag
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-drag-handle").DragStart();
        cut.Find(".weekly-day-card[data-day-index='1']").Drop();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card[data-day-index='0'] .weekly-day-servings-control").Should().BeEmpty();
            cut.Find(".weekly-day-card[data-day-index='1'] .servings-count").TextContent.Trim().Should().Be("6 portioner");
        });
    }

    [Fact]
    public async Task K5_30_And_32_WeeklyMenuPage_CreateShoppingList_HandlesUnsavedChanges_Previews_AndAddsSelectedItems()
    {
        var localToday = new DateOnly(2026, 11, 2); // Vecka 45
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var recipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Kycklinggryta",
                PrepTimeMinutes = 15,
                CookTimeMinutes = 25,
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "grädde", RawText = "2 dl grädde" },
                    new Ingredient { OrderIndex = 2, Quantity = 400m, Unit = "g", Name = "kycklingfilé", RawText = "400 g kycklingfilé" }
                ]
            },
            [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Recipe = recipe;
        menu.Days[0].Servings = 4;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        // 1. Ändra måndagens portioner från 4 -> 6 utan att spara manuellt (osparade ändringar = true)
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card[data-day-index='0'] .servings-btn").Should().HaveCount(2));
        cut.FindAll(".weekly-day-card[data-day-index='0'] .servings-btn").ElementAt(1).Click();
        cut.FindAll(".weekly-day-card[data-day-index='0'] .servings-btn").ElementAt(1).Click();

        // 2. Klicka på "🛒 Skapa inköpslista" -> dialog "Spara ändringar i veckomenyn?" visas (K1 #8)
        cut.Find("button.create-shopping-list-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var modal = cut.Find(".modal-dialog-custom");
            modal.TextContent.Should().Contain("Spara ändringar i veckomenyn?");
            modal.TextContent.Should().Contain("Spara och fortsätt");
        });

        // 3. Bekräfta "Spara och fortsätt" -> bekräftelseknappen använder nu semantiskt korrekt primär styling (btn-primary-custom)
        var confirmBtn = cut.FindAll(".modal-dialog-custom button").First(b => b.TextContent.Contains("Spara och fortsätt"));
        confirmBtn.ClassList.Should().Contain("btn-primary-custom");
        confirmBtn.ClassList.Should().NotContain("btn-danger-custom");
        confirmBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var previewDialog = cut.Find(".weekly-shopping-preview-dialog");
            previewDialog.TextContent.Should().Contain("Vecka 45, 2026");
            // Planerade middagar är hopfällda som standard
            var mealsToggle = cut.Find("button.weekly-preview-meals-toggle");
            mealsToggle.GetAttribute("aria-expanded").Should().Be("false");
            mealsToggle.TextContent.Should().Contain("1 planerad middag");
            cut.FindAll("#weekly-preview-meals-panel").Should().BeEmpty();

            // 2 dl skalat till 6 portioner = 3 dl grädde; 400 g skalat till 6 portioner = 600 g kycklingfilé
            previewDialog.TextContent.Should().Contain("3");
            previewDialog.TextContent.Should().Contain("dl");
            previewDialog.TextContent.Should().Contain("grädde");
            previewDialog.TextContent.Should().Contain("600");
            previewDialog.TextContent.Should().Contain("kycklingfilé");
            previewDialog.TextContent.Should().Contain("2 varor att lägga till");
            cut.Find(".weekly-preview-selection-hint").TextContent.Should().Be("Avmarkera varor du redan har hemma eller inte behöver köpa.");
        });

        // Expandera planerade middagar och kontrollera att rätt och portioner visas
        cut.Find("button.weekly-preview-meals-toggle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find("button.weekly-preview-meals-toggle").GetAttribute("aria-expanded").Should().Be("true");
            var mealsPanel = cut.Find("#weekly-preview-meals-panel");
            mealsPanel.TextContent.Should().Contain("Kycklinggryta");
            mealsPanel.TextContent.Should().Contain("(6 portioner)");
        });

        // 4. Klicka på "🛒 Lägg till i inköpslistan (2)"
        cut.Find("button.weekly-preview-confirm-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-shopping-preview-dialog").Should().BeEmpty();
            cut.Find(".rb-feedback-success").TextContent.Should().Contain("2").And.Contain("inköpslistan");
        });

        var shoppingService = Services.GetRequiredService<ShoppingListService>();
        var items = await shoppingService.GetItemsAsync();
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "grädde" && i.Quantity == 3m && i.Unit == "dl");
        items.Should().Contain(i => i.Name == "kycklingfilé" && i.Quantity == 600m && i.Unit == "g");
    }

    [Fact]
    public async Task K5_31_33_And_34_ShoppingListPage_AddFromWeeklyMenu_SupportsWeekNavigation_AndUpdatesListDirectly()
    {
        var localToday = new DateOnly(2026, 11, 2); // Vecka 45
        var nextWeekDate = localToday.AddDays(7);   // Vecka 46
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var w45Recipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Vecka 45 Gryta",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "mjölk", RawText = "2 dl mjölk" }]
            },
            [vardagId]);

        var w46Recipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Vecka 46 Soppa",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "morötter", RawText = "500 g morötter" }]
            },
            [vardagId]);

        var menu45 = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu45.Days[0].RecipeId = w45Recipe.Id;
        await weeklyMenuService.SaveDraftAsync(menu45);

        var menu46 = await weeklyMenuService.GetOrCreateWeekMenuAsync(nextWeekDate);
        menu46.Days[1].RecipeId = w46Recipe.Id;
        await weeklyMenuService.ConfirmMenuAsync(menu46);

        var cut = RenderComponent<ShoppingPlaceholder>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#shopping-weekly-menu-btn").TextContent.Should().Contain("Lägg till från veckomeny");
        });

        // 1. Öppna förhandsgranskningen från /inkopslista -> visar innevarande vecka (Vecka 45)
        cut.Find("#shopping-weekly-menu-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find(".weekly-shopping-preview-dialog");
            dialog.TextContent.Should().Contain("Vecka 45, 2026");
            dialog.TextContent.Should().Contain("1 planerad middag");
            dialog.TextContent.Should().Contain("mjölk");
        });

        // 2. Byt till nästa vecka (Vecka 46) via veckonavigeringen i förhandsgranskningen (K5 #34)
        cut.Find("button.weekly-preview-next-week-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find(".weekly-shopping-preview-dialog");
            dialog.TextContent.Should().Contain("Vecka 46, 2026");
            dialog.TextContent.Should().Contain("Spikad");
            dialog.TextContent.Should().Contain("morötter");
        });

        // Expandera planerade middagar för Vecka 46
        cut.Find("button.weekly-preview-meals-toggle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find("#weekly-preview-meals-panel").TextContent.Should().Contain("Vecka 46 Soppa");
        });

        // 3. Bekräfta tillägg från Vecka 46 -> uppdaterar inköpslistan direkt (K5 #33)
        cut.Find("button.weekly-preview-confirm-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-shopping-preview-dialog").Should().BeEmpty();
            cut.Find(".shopping-unchecked-list").TextContent.Should().Contain("500 g morötter");
        });
    }

    [Fact]
    public async Task Hierarchy_CollapsibleMeals_HiddenAlreadyAdded_InfoButton_Warnings_AndReselectRemovedItems()
    {
        var localToday = new DateOnly(2026, 11, 2); // Vecka 45
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var shoppingService = Services.GetRequiredService<ShoppingListService>();
        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var mondayRecipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Kycklingpanna",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 400m, Unit = "g", Name = "kycklingfilé", RawText = "400 g kycklingfilé" },
                    new Ingredient { OrderIndex = 2, Quantity = 2m, Unit = "dl", Name = "grädde", RawText = "2 dl grädde" },
                    new Ingredient { OrderIndex = 3, Quantity = null, Unit = null, Name = "grädde", Preparation = "vispad", GroupHeader = "Till garnering", RawText = "vispad grädde" }
                ]
            },
            [vardagId]);

        var tuesdayRecipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Tomatsoppa",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "tomater", RawText = "500 g tomater" }
                ]
            },
            [vardagId]);

        // Lägg in "2 st tomater" i Att handla så att "500 g tomater" ger en inkompatibel enhetsvarning!
        await shoppingService.AddManualItemAsync("2 st tomater");

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = mondayRecipe.Id;
        menu.Days[0].Servings = 4;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        // Steg 1: Lägg till måndagens varor (kycklingfilé + grädde)
        var initialPreview = await shoppingService.BuildWeeklyMenuPreviewAsync(localToday);
        await shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            localToday,
            initialPreview.PreviewStamp,
            initialPreview.Items.Select(i => i.PreviewItemKey).ToList());

        // Steg 2: Alla varor är nu hanterade -> verifiera meddelandet "Alla varor från veckomenyn är redan hanterade."
        // och att redan tillagda varor ligger i den hopfällda sektionen "Visa redan tillagda varor (2)"
        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.Find("button.create-shopping-list-btn").Should().NotBeNull());
        cut.Find("button.create-shopping-list-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-preview-all-handled").TextContent.Should().Contain("Alla varor från veckomenyn är redan hanterade.");
            cut.FindAll(".weekly-preview-pending-list").Should().BeEmpty();
            cut.FindAll(".weekly-preview-selection-hint").Should().BeEmpty();
            var alreadyToggle = cut.Find("button.weekly-preview-already-added-toggle");
            alreadyToggle.GetAttribute("aria-expanded").Should().Be("false");
            alreadyToggle.TextContent.Should().Contain("Visa redan tillagda varor (2)");
            cut.FindAll("#weekly-preview-already-added-panel").Should().BeEmpty();
        });

        // Stäng dialogen
        cut.Find("button.weekly-preview-cancel-btn").Click();

        // Steg 3: Radera "grädde" ur inköpslistan, öka måndagens portioner från 4 -> 6 (ger 200 g komplettering av kycklingfilé + 1 dl grädde),
        // och lägg till tisdagens recept ("500 g tomater" som krockar i enhet med "2 st tomater").
        // Dessutom: lägg till ett tredje recept eller låt oss testa en redan tillagd men borttagen vara genom att radera kycklingfilé på en måltid utan portionsökning!
        // Låt oss behålla måndag på 4 portioner för grädde? Nej, om måndag ökar till 6 portioner får kycklingfilé en portionskomplettering (200 g).
        // För att samtidigt ha en redan fullt tillagd men borttagen vara, lägger vi först till onsdag (Risotto, 4 port -> 3 dl arborioris) och raderar sedan arborioris ur inköpslistan!
        var wednesdayRecipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Svamprisotto",
                Servings = 4,
                Ingredients =
                [
                    new Ingredient { OrderIndex = 1, Quantity = 3m, Unit = "dl", Name = "arborioris", RawText = "3 dl arborioris" }
                ]
            },
            [vardagId]);

        menu.Days[2].RecipeId = wednesdayRecipe.Id;
        menu.Days[2].Servings = 4;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        var wedPreview = await shoppingService.BuildWeeklyMenuPreviewAsync(localToday);
        await shoppingService.ConfirmWeeklyMenuAdditionsAsync(
            localToday,
            wedPreview.PreviewStamp,
            wedPreview.Items.Where(i => i.IsSelectedByDefault).Select(i => i.PreviewItemKey).ToList());

        // Radera arborioris ur inköpslistan (så att den är "Redan tillagd" men "Borttagen ur inköpslistan")
        var currentShoppingItems = await shoppingService.GetItemsAsync();
        var riceItem = currentShoppingItems.Single(i => i.Name == "arborioris");
        await shoppingService.DeleteItemAsync(riceItem.Id);

        // Ändra nu måndag till 6 portioner (komplettering för måndag) och lägg till tisdag (tomater med enhetsvarning)
        menu.Days[0].Servings = 6;
        menu.Days[1].RecipeId = tuesdayRecipe.Id;
        menu.Days[1].Servings = 4;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        // Ladda om sidan och öppna förhandsgranskningen
        var cut2 = RenderComponent<WeeklyMenuPage>();
        cut2.WaitForAssertion(() => cut2.Find("button.create-shopping-list-btn").Should().NotBeNull());
        cut2.Find("button.create-shopping-list-btn").Click();

        cut2.WaitForAssertion(() =>
        {
            // 1. Planerade middagar är hopfällda från start ("3 planerade middagar")
            var mealsBtn = cut2.Find("button.weekly-preview-meals-toggle");
            mealsBtn.GetAttribute("aria-expanded").Should().Be("false");
            mealsBtn.TextContent.Should().Contain("3 planerade middagar");

            // 2. Nya/kompletterande varor (kycklingfilé 200 g, grädde 1 dl med garneringskommentar, tomater 500 g med enhetsvarning)
            // visas i .weekly-preview-pending-list medan redan tillagda (arborioris) är dolda i hopfälld sektion
            var pendingRows = cut2.FindAll(".weekly-preview-pending-list .weekly-preview-item-row");
            pendingRows.Should().HaveCount(3);

            // 5. Garneringskommentar är synlig direkt på gräddraden
            var creamRow = cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='grädde']");
            creamRow.QuerySelector(".weekly-preview-item-comment")!.TextContent.Should().Contain("till garnering");

            // 6. Portionskomplettering är tydligt markerad på kycklingfilé ("Komplettering för måndag")
            var chickenRow = cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='kycklingfilé']");
            chickenRow.QuerySelector(".weekly-preview-item-short-status")!.TextContent.Trim().Should().Be("Komplettering för måndag");
            // Detaljerad spårning är dold innan man klickar på ⓘ
            chickenRow.QuerySelectorAll(".weekly-preview-item-details").Should().BeEmpty();

            // 7. Inkompatibla enheter har synlig varning direkt på tomatraden
            var tomatoRow = cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='tomater']");
            tomatoRow.QuerySelector(".weekly-preview-inline-warning")!.TextContent.Should().Contain("Enhetsvarning");
        });

        // 4. Klicka på informationsknappen (ⓘ) på kycklingfilé och tomater -> visar spårningsdetaljer och enhetsvarning
        cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='kycklingfilé'] button.weekly-preview-info-btn").Click();
        cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='tomater'] button.weekly-preview-info-btn").Click();

        cut2.WaitForAssertion(() =>
        {
            var chickenDetails = cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='kycklingfilé'] .weekly-preview-item-details");
            chickenDetails.TextContent.Should().Contain("Veckodagar:").And.Contain("4 av 6 port redan tillagda").And.Contain("Finns i Att handla: 400 g kycklingfilé");

            var tomatoDetails = cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='tomater'] .weekly-preview-item-details");
            tomatoDetails.TextContent.Should().Contain("annan enhet");
        });

        // Informationsknappen använder en Fluent-ikon (inte tecknet ⓘ) och behåller sina ARIA-attribut/tillståndsklass
        var openedInfoBtn = cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='kycklingfilé'] button.weekly-preview-info-btn");
        openedInfoBtn.QuerySelector("svg").Should().NotBeNull();
        openedInfoBtn.TextContent.Should().NotContain("ⓘ");
        openedInfoBtn.GetAttribute("aria-expanded").Should().Be("true");
        openedInfoBtn.GetAttribute("aria-controls").Should().NotBeNullOrWhiteSpace();
        openedInfoBtn.GetAttribute("aria-label").Should().Contain("kycklingfilé");
        openedInfoBtn.ClassList.Should().Contain("is-open");
        cut2.Find(".weekly-preview-pending-list .weekly-preview-item-row[data-ingredient-name='tomater'] button.weekly-preview-info-btn")
            .ClassList.Should().Contain("has-warning");

        // Planerade middagar: en rad per middag med fast kolumnstruktur, hopfälld som standard
        cut2.FindAll("#weekly-preview-meals-panel").Should().BeEmpty();
        cut2.Find("button.weekly-preview-meals-toggle").Click();
        cut2.WaitForAssertion(() =>
        {
            var mealRows = cut2.FindAll("#weekly-preview-meals-panel > li.weekly-preview-meal-row").ToList();
            mealRows.Should().HaveCount(3);
            foreach (var mealRow in mealRows)
            {
                mealRow.QuerySelectorAll(".weekly-preview-meal-day").Should().ContainSingle();
                mealRow.QuerySelectorAll(".weekly-preview-meal-title").Should().ContainSingle();
                mealRow.QuerySelectorAll(".weekly-preview-meal-servings").Should().ContainSingle();
                mealRow.QuerySelectorAll(".weekly-preview-meal-status").Should().ContainSingle();
            }
            mealRows.Select(r => r.GetAttribute("data-meal-day")).Should().Equal("0", "1", "2");
            mealRows[0].QuerySelector(".weekly-preview-meal-day")!.TextContent.Should().Be("Måndag");
            // Onsdagens recept är fullt tillagt (✓-ikon), måndagen har bara komplettering kvar
            mealRows[2].QuerySelector(".weekly-preview-meal-status svg").Should().NotBeNull();
            mealRows[0].QuerySelector(".weekly-preview-meal-status svg").Should().BeNull();
        });

        // Standardurval: alla nya förslag markerade, redan tillagda ej. "Markera endast nya" ändrar då inget (inaktiv).
        cut2.Find(".weekly-preview-selection-hint").TextContent.Should().Be("Avmarkera varor du redan har hemma eller inte behöver köpa.");
        bool IsChecked(string name) => cut2.Find($".weekly-preview-item-row[data-ingredient-name='{name}'] input.weekly-preview-checkbox").HasAttribute("checked");
        var selectNewBtn = () => cut2.Find("button.weekly-preview-select-new-btn");
        var deselectAllBtn = () => cut2.Find("button.weekly-preview-deselect-all-btn");
        IsChecked("kycklingfilé").Should().BeTrue();
        IsChecked("grädde").Should().BeTrue();
        IsChecked("tomater").Should().BeTrue();
        selectNewBtn().TextContent.Trim().Should().Be("Markera endast nya (3)");
        selectNewBtn().HasAttribute("disabled").Should().BeTrue();
        deselectAllBtn().HasAttribute("disabled").Should().BeFalse();

        // Manuellt ändrat urval: avmarkera en ny vara -> massåtgärden blir aktiv och återställer endast urvalet
        cut2.Find(".weekly-preview-item-row[data-ingredient-name='grädde'] input.weekly-preview-checkbox").Change(false);
        cut2.WaitForAssertion(() =>
        {
            cut2.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("2 varor att lägga till");
            selectNewBtn().HasAttribute("disabled").Should().BeFalse();
        });
        selectNewBtn().Click();
        cut2.WaitForAssertion(() =>
        {
            cut2.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("3 varor att lägga till");
            IsChecked("grädde").Should().BeTrue();
            selectNewBtn().HasAttribute("disabled").Should().BeTrue();
        });

        // 9. Testa "Avmarkera alla" och "Markera endast nya"
        cut2.Find("button.weekly-preview-deselect-all-btn").Click();
        cut2.WaitForAssertion(() =>
        {
            cut2.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("0 varor att lägga till");
            cut2.Find("button.weekly-preview-confirm-btn").HasAttribute("disabled").Should().BeTrue();
            deselectAllBtn().HasAttribute("disabled").Should().BeTrue();
            selectNewBtn().HasAttribute("disabled").Should().BeFalse();
        });

        cut2.Find("button.weekly-preview-select-new-btn").Click();
        cut2.WaitForAssertion(() =>
        {
            cut2.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("3 varor att lägga till");
            cut2.Find("button.weekly-preview-confirm-btn").TextContent.Should().Contain("(3)");
            selectNewBtn().HasAttribute("disabled").Should().BeTrue();
            deselectAllBtn().HasAttribute("disabled").Should().BeFalse();
        });

        // 3 & 8 & 10. Expandera "Visa redan tillagda varor (1)", verifiera att borttagna "arborioris" inte är förvald
        // men kan bockas i manuellt och uppdaterar bekräftelseknappens antal till (4)!
        cut2.Find("button.weekly-preview-already-added-toggle").Click();

        cut2.WaitForAssertion(() =>
        {
            var riceRow = cut2.Find("#weekly-preview-already-added-panel .weekly-preview-item-row[data-ingredient-name='arborioris']");
            riceRow.TextContent.Should().Contain("Borttagen ur inköpslistan");
            riceRow.QuerySelector("input.weekly-preview-checkbox")!.HasAttribute("checked").Should().BeFalse();
        });

        // Bocka i arborioris manuellt -> "Markera endast nya" blir aktiv och kan rensa bort den igen
        cut2.Find("#weekly-preview-already-added-panel .weekly-preview-item-row[data-ingredient-name='arborioris'] input.weekly-preview-checkbox").Change(true);

        cut2.WaitForAssertion(() =>
        {
            cut2.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("4 varor att lägga till");
            cut2.Find("button.weekly-preview-confirm-btn").TextContent.Should().Contain("(4)");
            selectNewBtn().HasAttribute("disabled").Should().BeFalse();
        });

        selectNewBtn().Click();
        cut2.WaitForAssertion(() =>
        {
            cut2.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("3 varor att lägga till");
            cut2.Find("#weekly-preview-already-added-panel .weekly-preview-item-row[data-ingredient-name='arborioris'] input.weekly-preview-checkbox")
                .HasAttribute("checked").Should().BeFalse();
            selectNewBtn().HasAttribute("disabled").Should().BeTrue();
        });

        // Bocka i arborioris igen inför slutkontroll
        cut2.Find("#weekly-preview-already-added-panel .weekly-preview-item-row[data-ingredient-name='arborioris'] input.weekly-preview-checkbox").Change(true);

        cut2.WaitForAssertion(() =>
        {
            cut2.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("4 varor att lägga till");
            cut2.Find("button.weekly-preview-confirm-btn").TextContent.Should().Contain("(4)");
        });
    }

    // ---------- UX-1C: receptväljarmodal i veckomenyns dagkort ----------

    private const string ChangeRecipeButton = ".weekly-day-change-recipe-toggle";

    private static string DayCard(int dayIndex) => $".weekly-day-card[data-day-index='{dayIndex}']";

    private static string DayTitle(IRenderedComponent<WeeklyMenuPage> cut, int dayIndex) =>
        cut.Find($"{DayCard(dayIndex)} .weekly-day-recipe-title").TextContent.Trim();

    /// <summary>Måndag = Pasta (6 port.), tisdag = Soppa (2 port.), övriga dagar tomma. Paj finns som tredje recept.</summary>
    private async Task<(IRenderedComponent<WeeklyMenuPage> Cut, WeeklyMenuService Service, DateOnly Today, Recipe Paj)> RenderPageWithPickerDataAsync()
    {
        var localToday = new DateOnly(2026, 11, 2); // Vecka 45
        var (dbFactory, recipeService, categoryService, weeklyMenuService) = await SetupServicesAsync(localToday);
        Services.AddSingleton(dbFactory); // hålls vid liv under testet

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        var pasta = await recipeService.CreateRecipeAsync(new Recipe { Title = "Pasta", Servings = 4 }, [vardagId]);
        var soppa = await recipeService.CreateRecipeAsync(new Recipe { Title = "Soppa", Servings = 2 }, [vardagId]);
        var paj = await recipeService.CreateRecipeAsync(new Recipe { Title = "Paj", Servings = 6 }, [vardagId]);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = pasta.Id;
        menu.Days[0].Recipe = pasta;
        menu.Days[0].Servings = 6;
        menu.Days[1].RecipeId = soppa.Id;
        menu.Days[1].Recipe = soppa;
        menu.Days[1].Servings = 2;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));
        return (cut, weeklyMenuService, localToday, paj);
    }

    private static void OpenPicker(IRenderedComponent<WeeklyMenuPage> cut, int dayIndex) =>
        cut.Find($"{DayCard(dayIndex)} {ChangeRecipeButton}").Click();

    private static void PickCard(IRenderedComponent<WeeklyMenuPage> cut, string title) =>
        cut.FindAll(".recipe-picker-card")
            .First(c => c.QuerySelector(".recipe-picker-card-title")!.TextContent.Trim() == title)
            .Click();

    [Fact]
    public async Task RecipePicker_ButtonsOpenModal_WithoutOldDropdown_AndCancelChangesNothing()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();

        cut.FindAll("select.weekly-day-recipe-select").Should().BeEmpty();
        cut.FindAll(".recipe-picker").Should().BeEmpty();
        cut.Find($"{DayCard(0)} {ChangeRecipeButton}").TextContent.Trim().Should().Be("Byt recept…");
        cut.Find($"{DayCard(2)} {ChangeRecipeButton}").TextContent.Trim().Should().Be("Välj recept…");

        OpenPicker(cut, 0);

        cut.FindAll(".recipe-picker").Should().ContainSingle();
        cut.Find(".recipe-picker-title").TextContent.Should().Contain("Måndag");
        cut.FindAll(".recipe-picker-card-title").Select(e => e.TextContent.Trim()).Should().Contain(new[] { "Pasta", "Soppa", "Paj" });

        cut.Find(".recipe-picker-cancel").Click();

        cut.FindAll(".recipe-picker").Should().BeEmpty();
        DayTitle(cut, 0).Should().Be("Pasta");
        DayTitle(cut, 1).Should().Be("Soppa");
        cut.Find($"{DayCard(0)} .servings-count").TextContent.Trim().Should().Be("6 portioner");
        cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty();
    }

    [Fact]
    public async Task RecipePicker_Escape_ClosesWithoutChange()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();
        OpenPicker(cut, 1);

        cut.Find(".recipe-picker").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll(".recipe-picker").Should().BeEmpty();
        DayTitle(cut, 1).Should().Be("Soppa");
        cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty();
    }

    [Fact]
    public async Task RecipePicker_SelectingRecipe_UpdatesOnlyThatDay_AndMarksMenuUnsaved()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();

        OpenPicker(cut, 1); // tisdag
        PickCard(cut, "Paj");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".recipe-picker").Should().BeEmpty();
            DayTitle(cut, 1).Should().Be("Paj");
            // Befintligt beteende: nytt recept får receptets egna portionsantal
            cut.Find($"{DayCard(1)} .servings-count").TextContent.Trim().Should().Be("6 portioner");
            cut.Find(".weekly-status-badge.status-unsaved").TextContent.Should().Contain("Osparade ändringar");
        });

        // Övriga dagar är orörda
        DayTitle(cut, 0).Should().Be("Pasta");
        cut.Find($"{DayCard(0)} .servings-count").TextContent.Trim().Should().Be("6 portioner");
        for (var i = 2; i < 7; i++)
        {
            cut.FindAll($"{DayCard(i)} .weekly-day-recipe-title").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task RecipePicker_SelectingRecipeOnEmptyDay_FillsThatDayOnly()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();

        OpenPicker(cut, 4); // fredag, tom
        cut.FindAll(".recipe-picker-clear").Should().BeEmpty("tom dag har inget val att tömma");
        PickCard(cut, "Soppa");

        cut.WaitForAssertion(() =>
        {
            DayTitle(cut, 4).Should().Be("Soppa");
            cut.Find($"{DayCard(4)} .servings-count").TextContent.Trim().Should().Be("2 portioner");
            cut.Find($"{DayCard(4)} {ChangeRecipeButton}").TextContent.Trim().Should().Be("Byt recept…");
        });
        DayTitle(cut, 0).Should().Be("Pasta");
        DayTitle(cut, 1).Should().Be("Soppa");
        cut.FindAll($"{DayCard(3)} .weekly-day-recipe-title").Should().BeEmpty();
        cut.FindAll($"{DayCard(5)} .weekly-day-recipe-title").Should().BeEmpty();
    }

    [Fact]
    public async Task RecipePicker_ClearDay_EmptiesOnlyThatDay()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();

        OpenPicker(cut, 0);
        cut.Find(".recipe-picker-clear").TextContent.Trim().Should().Be("Ingen middag (töm dag)");
        cut.Find(".recipe-picker-clear").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".recipe-picker").Should().BeEmpty();
            cut.FindAll($"{DayCard(0)} .weekly-day-recipe-title").Should().BeEmpty();
            cut.FindAll($"{DayCard(0)} .weekly-day-servings-control").Should().BeEmpty();
            cut.Find($"{DayCard(0)} {ChangeRecipeButton}").TextContent.Trim().Should().Be("Välj recept…");
            cut.Find(".weekly-status-badge.status-unsaved").TextContent.Should().Contain("Osparade ändringar");
        });
        DayTitle(cut, 1).Should().Be("Soppa");
    }

    [Fact]
    public async Task RecipePicker_SelectingCurrentRecipe_KeepsServingsAndDoesNotMarkUnsaved()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();

        OpenPicker(cut, 0);
        cut.FindAll(".recipe-picker-card")
            .Single(c => c.GetAttribute("aria-current") == "true")
            .QuerySelector(".recipe-picker-card-title")!.TextContent.Trim().Should().Be("Pasta");
        PickCard(cut, "Pasta");

        cut.FindAll(".recipe-picker").Should().BeEmpty();
        cut.Find($"{DayCard(0)} .servings-count").TextContent.Trim().Should().Be("6 portioner");
        cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty();
    }

    [Fact]
    public async Task RecipePicker_OnlyOneModalIsOpenAtATime()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();

        OpenPicker(cut, 3);

        cut.FindAll(".recipe-picker").Should().ContainSingle();
        cut.FindAll("fluent-dialog").Should().HaveCount(7);
        cut.FindAll("fluent-dialog").Count(d => !d.HasAttribute("hidden")).Should().Be(1);
        cut.Find($"{DayCard(3)} .recipe-picker").Should().NotBeNull();
    }

    [Fact]
    public async Task RecipePicker_ChangeCanBeSavedAndRestored()
    {
        var (cut, weeklyMenuService, today, _) = await RenderPageWithPickerDataAsync();

        OpenPicker(cut, 1);
        PickCard(cut, "Paj");
        cut.WaitForAssertion(() => cut.Find(".weekly-status-badge.status-unsaved").Should().NotBeNull());

        // Återställ sparad version -> bekräftelsedialog visas, bekräfta återställning
        cut.Find("button.discard-changes-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".modal-dialog-custom").Should().ContainSingle());
        var confirmRevertBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Ja, återställ"));
        confirmRevertBtn.Click();
        cut.WaitForAssertion(() =>
        {
            DayTitle(cut, 1).Should().Be("Soppa");
            cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty();
        });

        // Ändra igen och spara utkast -> ändringen finns i databasen och inget är osparat
        OpenPicker(cut, 1);
        PickCard(cut, "Paj");
        cut.WaitForAssertion(() => DayTitle(cut, 1).Should().Be("Paj"));
        cut.Find("button.save-draft-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty());

        var saved = await weeklyMenuService.GetOrCreateWeekMenuAsync(today);
        saved.Days.Single(d => d.DayIndex == 1).Recipe!.Title.Should().Be("Paj");
        saved.Days.Single(d => d.DayIndex == 0).Recipe!.Title.Should().Be("Pasta");
        saved.Days.Single(d => d.DayIndex == 0).Servings.Should().Be(6);
    }

    [Fact]
    public async Task DiscardChanges_WhenCancelled_ShouldKeepUnsavedChangesAndNotRevert()
    {
        var (cut, _, _, _) = await RenderPageWithPickerDataAsync();

        OpenPicker(cut, 1);
        PickCard(cut, "Paj");
        cut.WaitForAssertion(() => cut.Find(".weekly-status-badge.status-unsaved").Should().NotBeNull());

        // Klicka på "Återställ sparad version"
        cut.Find("button.discard-changes-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find(".modal-dialog-custom");
            dialog.Should().NotBeNull();
            dialog.TextContent.Should().Contain("Återställ sparad version?");
            dialog.TextContent.Should().Contain("Dina osparade ändringar försvinner");
        });

        // Bekräftelseknappen ska vara destruktiv (Danger)
        var confirmBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Ja, återställ"));
        confirmBtn.ClassList.Should().Contain("btn-danger-custom");

        // Klicka på Avbryt
        var cancelBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Avbryt"));
        cancelBtn.Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".modal-dialog-custom").Should().BeEmpty("dialogen ska stängas vid avbryt");
            // Ändringen ska finnas kvar och fortfarande vara markerad som osparad
            DayTitle(cut, 1).Should().Be("Paj");
            cut.FindAll(".weekly-status-badge.status-unsaved").Should().ContainSingle();
        });
    }

    // --- Isolerade tester av dagkortet: callbackkontrakt, status och fokus ---

    private static WeeklyMenuDay MakeDay(Recipe? recipe, int dayIndex = 2, int? servings = null, string? status = null) => new()
    {
        Id = dayIndex + 1,
        DayIndex = dayIndex,
        Date = new DateOnly(2026, 11, 2).AddDays(dayIndex),
        RecipeId = recipe?.Id,
        Recipe = recipe,
        Servings = servings,
        StatusMessage = status
    };

    private IRenderedComponent<WeeklyMenuDayCard> RenderDayCard(
        WeeklyMenuDay day,
        IReadOnlyList<Recipe> recipes,
        List<(int DayIndex, int? RecipeId)> changes)
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
        return RenderComponent<WeeklyMenuDayCard>(p => p
            .Add(c => c.Day, day)
            .Add(c => c.AllRecipes, recipes)
            .Add(c => c.AllCategories, Array.Empty<Category>())
            .Add(c => c.OnManualRecipeChanged,
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<(int DayIndex, int? RecipeId)>(this, a => changes.Add(a))));
    }

    [Fact]
    public void DayCard_PickingAnotherRecipe_RaisesCallbackForThatDayOnly_AndMovesFocusToButton()
    {
        var pasta = new Recipe { Id = 1, Title = "Pasta", Servings = 4 };
        var soppa = new Recipe { Id = 2, Title = "Soppa", Servings = 2 };
        var changes = new List<(int DayIndex, int? RecipeId)>();
        var cut = RenderDayCard(MakeDay(pasta, dayIndex: 3, servings: 6, status: "Obs"), new[] { pasta, soppa }, changes);

        cut.Find(ChangeRecipeButton).Click();
        cut.FindAll(".recipe-picker-card").First(c => c.TextContent.Contains("Soppa")).Click();

        changes.Should().Equal((3, (int?)2));
        cut.FindAll(".recipe-picker").Should().BeEmpty();
        JSInterop.Invocations["Blazor._internal.domWrapper.focus"].Should().NotBeEmpty();
    }

    [Fact]
    public void DayCard_Cancel_KeepsServingsAndStatus_AndRaisesNoCallback()
    {
        var pasta = new Recipe { Id = 1, Title = "Pasta", Servings = 4 };
        var changes = new List<(int DayIndex, int? RecipeId)>();
        var day = MakeDay(pasta, servings: 6, status: "Inget recept hittades");
        var cut = RenderDayCard(day, new[] { pasta }, changes);

        cut.Find(ChangeRecipeButton).Click();
        cut.Find(".recipe-picker-cancel").Click();

        changes.Should().BeEmpty();
        day.Servings.Should().Be(6);
        day.RecipeId.Should().Be(1);
        cut.Find(".weekly-day-status-message").TextContent.Should().Contain("Inget recept hittades");
        cut.Find(".servings-count").TextContent.Trim().Should().Be("6 portioner");
    }

    [Fact]
    public void DayCard_ClearAndSameRecipe_RaiseExpectedCallbacks()
    {
        var pasta = new Recipe { Id = 1, Title = "Pasta", Servings = 4 };
        var changes = new List<(int DayIndex, int? RecipeId)>();
        var cut = RenderDayCard(MakeDay(pasta, dayIndex: 5, servings: 4), new[] { pasta }, changes);

        cut.Find(ChangeRecipeButton).Click();
        cut.Find(".recipe-picker-card").Click(); // samma recept -> ingen ändring
        changes.Should().BeEmpty();

        cut.Find(ChangeRecipeButton).Click();
        cut.Find(".recipe-picker-clear").Click();
        changes.Should().Equal((5, (int?)null));
    }

    // =========================================================================
    // UX-2D.2: Knapphierarki och cleanup
    // =========================================================================

    [Fact]
    public async Task WeeklyMenuPage_ButtonHierarchy_GenerateShouldBeSecondary_ConfirmShouldBePrimary()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, _, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var recipe = await recipeService.CreateRecipeAsync(new Recipe { Title = "Testmiddag", Servings = 4 }, []);
        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = recipe.Id;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll("button.generate-week-btn").Should().ContainSingle());

        // 1. 'Generera veckomeny' ska vara mörkgrå sekundärknapp (UX-2D.2 Del C)
        var generateBtn = cut.Find("button.generate-week-btn");
        generateBtn.ClassList.Should().Contain("btn-secondary-custom", "Generera veckomeny är ett verktyg och ska vara sekundärt");
        generateBtn.ClassList.Should().NotContain("btn-primary-custom", "Generera veckomeny ska inte konkurrera med primär sparhandling");

        // 2. 'Spika veckomenyn' ska vara korallfärgad primärknapp
        var confirmBtn = cut.Find("button.confirm-week-btn");
        confirmBtn.ClassList.Should().Contain("btn-primary-custom", "Spika veckomenyn är den primära slutförandehandlingen");
        confirmBtn.TextContent.Should().Contain("Spika veckomenyn");

        // 3. Spika menyn och verifiera att 'Spara ändringar i spikad meny' förblir korallfärgad primärknapp
        confirmBtn.Click();
        cut.WaitForAssertion(() =>
        {
            var saveConfirmedBtn = cut.Find("button.confirm-week-btn");
            saveConfirmedBtn.ClassList.Should().Contain("btn-primary-custom", "Spara ändringar i spikad meny ska förbli primärknapp");
            saveConfirmedBtn.TextContent.Should().Contain("Spara ändringar i spikad meny");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeeklyMenuPage_DisposeAsync_DisposesDotNetRefWithoutThrowing(bool jsDisconnected)
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, _, _, _) = await SetupServicesAsync(localToday);
        using var dbScope = dbFactory;

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() =>
            JSInterop.Invocations["receptbanken.registerWeeklyMenuTouchDrag"].Should().ContainSingle());

        var dotNetRef = (DotNetObjectReference<WeeklyMenuPage>)JSInterop
            .Invocations["receptbanken.registerWeeklyMenuTouchDrag"]
            .Single()
            .Arguments[0]!;

        if (jsDisconnected)
        {
            JSInterop.SetupVoid("receptbanken.unregisterWeeklyMenuTouchDrag")
                .SetException(new JSDisconnectedException("Circuit disconnected"));
        }

        var act = async () => await cut.Instance.DisposeAsync();
        await act.Should().NotThrowAsync();

        JSInterop.Invocations["receptbanken.unregisterWeeklyMenuTouchDrag"].Should().ContainSingle();
        var accessDisposedRef = () => _ = dotNetRef.Value;
        accessDisposedRef.Should().Throw<ObjectDisposedException>();
    }
}
