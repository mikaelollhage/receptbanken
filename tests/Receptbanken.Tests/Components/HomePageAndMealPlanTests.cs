using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class HomePageAndMealPlanTests : TestContext
{
    private sealed class MutableLocalDateProvider : ILocalDateProvider
    {
        public DateOnly Today { get; set; }
        public MutableLocalDateProvider(DateOnly initialToday) => Today = initialToday;
        public DateOnly GetLocalToday() => Today;
        public void SetClientLocalDate(DateOnly clientDate) => Today = clientDate;
    }

    private async Task<(TestDbContextFactory DbFactory, WeeklyMenuService MenuService, RecipeService RecipeService, MutableLocalDateProvider DateProvider, Recipe Recipe1, Recipe Recipe2)> SetupContextAsync(DateOnly localToday)
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
        var menuService = new WeeklyMenuService(dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        var dateProvider = new MutableLocalDateProvider(localToday);

        var r1 = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Krämig lax med dill",
            PrepTimeMinutes = 15,
            CookTimeMinutes = 20,
            Servings = 4,
            ServingsUnit = "portioner",
            ImagePath = "images/lax.jpg"
        });

        var r2 = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Vegansk linsgryta",
            PrepTimeMinutes = 10,
            CookTimeMinutes = 25,
            Servings = 6,
            ServingsUnit = "portioner"
        });

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(dateProvider);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(menuService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (dbFactory, menuService, recipeService, dateProvider, r1, r2);
    }

    // =========================================================================
    // 1. Backend / Service-tester: GetHomeMealPlanAsync & GetTodaysDinnerAsync
    // =========================================================================

    [Fact]
    public async Task GetHomeMealPlanAsync_WhenConfirmedMenuTodayAndTomorrow_ReturnsBothConfirmedMeals()
    {
        var localToday = new DateOnly(2026, 11, 2); // Måndag v.45
        var (dbFactory, menuService, _, _, r1, r2) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Servings = 4;
        menu.Days[1].RecipeId = r2.Id;
        menu.Days[1].Servings = 6;
        await menuService.ConfirmMenuAsync(menu);

        var plan = await menuService.GetHomeMealPlanAsync(localToday);

        plan.Should().NotBeNull();
        plan.Today.Should().Be(localToday);
        plan.Tomorrow.Should().Be(new DateOnly(2026, 11, 3));

        // Dagens måltid
        plan.TodayMeal.HasSavedMenu.Should().BeTrue();
        plan.TodayMeal.IsConfirmed.Should().BeTrue();
        plan.TodayMeal.IsDraft.Should().BeFalse();
        plan.TodayMeal.HasRecipe.Should().BeTrue();
        plan.TodayMeal.Recipe!.Title.Should().Be("Krämig lax med dill");
        plan.TodayMeal.Servings.Should().Be(4);

        // Morgondagens måltid
        plan.TomorrowMeal.HasSavedMenu.Should().BeTrue();
        plan.TomorrowMeal.IsConfirmed.Should().BeTrue();
        plan.TomorrowMeal.HasRecipe.Should().BeTrue();
        plan.TomorrowMeal.Recipe!.Title.Should().Be("Vegansk linsgryta");
        plan.TomorrowMeal.Servings.Should().Be(6);
    }

    [Fact]
    public async Task GetHomeMealPlanAsync_WhenDraftMenuToday_ReturnsDraftStatusAndRecipe()
    {
        var localToday = new DateOnly(2026, 11, 2); // Måndag v.45
        var (dbFactory, menuService, _, _, r1, _) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        await menuService.SaveDraftAsync(menu);

        var plan = await menuService.GetHomeMealPlanAsync(localToday);

        plan.TodayMeal.HasSavedMenu.Should().BeTrue();
        plan.TodayMeal.IsDraft.Should().BeTrue();
        plan.TodayMeal.IsConfirmed.Should().BeFalse();
        plan.TodayMeal.HasRecipe.Should().BeTrue();
        plan.TodayMeal.Recipe!.Title.Should().Be("Krämig lax med dill");
    }

    [Fact]
    public async Task GetHomeMealPlanAsync_OnSunday_FetchesTomorrowFromNextWeekAccurately()
    {
        var sunday = new DateOnly(2026, 11, 8); // Söndag v.45
        var nextMonday = new DateOnly(2026, 11, 9); // Måndag v.46
        var (dbFactory, menuService, _, _, r1, r2) = await SetupContextAsync(sunday);
        using var _ = dbFactory;

        // Meny vecka 45 (Söndag har r1)
        var menuW45 = await menuService.GetOrCreateWeekMenuAsync(sunday);
        menuW45.Days[6].RecipeId = r1.Id; // Söndag index 6
        await menuService.ConfirmMenuAsync(menuW45);

        // Meny vecka 46 (Måndag har r2)
        var menuW46 = await menuService.GetOrCreateWeekMenuAsync(nextMonday);
        menuW46.Days[0].RecipeId = r2.Id; // Måndag index 0
        await menuService.ConfirmMenuAsync(menuW46);

        var plan = await menuService.GetHomeMealPlanAsync(sunday);

        plan.TodayMeal.Date.Should().Be(sunday);
        plan.TodayMeal.CalendarWeek.IsoWeek.Should().Be(45);
        plan.TodayMeal.Recipe!.Title.Should().Be("Krämig lax med dill");

        plan.TomorrowMeal.Date.Should().Be(nextMonday);
        plan.TomorrowMeal.CalendarWeek.IsoWeek.Should().Be(46);
        plan.TomorrowMeal.Recipe!.Title.Should().Be("Vegansk linsgryta");
    }

    [Fact]
    public async Task GetHomeMealPlanAsync_OverYearBoundary_CalculatesIsoWeeksAndDatesAccurately()
    {
        // 2026-12-31 är torsdag i vecka 53, 2026
        // 2027-01-01 är fredag i samma vecka 53, 2026 (ISO)
        var newYearsEve = new DateOnly(2026, 12, 31);
        var (dbFactory, menuService, _, _, r1, r2) = await SetupContextAsync(newYearsEve);
        using var _ = dbFactory;

        var menu = await menuService.GetOrCreateWeekMenuAsync(newYearsEve);
        int thuIndex = CalendarWeek.GetDayIndex(newYearsEve);
        int friIndex = CalendarWeek.GetDayIndex(new DateOnly(2027, 1, 1));
        menu.Days[thuIndex].RecipeId = r1.Id;
        menu.Days[friIndex].RecipeId = r2.Id;
        await menuService.ConfirmMenuAsync(menu);

        var plan = await menuService.GetHomeMealPlanAsync(newYearsEve);

        plan.TodayMeal.Date.Should().Be(new DateOnly(2026, 12, 31));
        plan.TodayMeal.Recipe!.Title.Should().Be("Krämig lax med dill");
        plan.TomorrowMeal.Date.Should().Be(new DateOnly(2027, 1, 1));
        plan.TomorrowMeal.Recipe!.Title.Should().Be("Vegansk linsgryta");
    }

    [Fact]
    public async Task GetHomeMealPlanAsync_WhenRecipeDeleted_ReturnsNullRecipeWithoutCrash()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, menuService, recipeService, _, r1, _) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        await menuService.ConfirmMenuAsync(menu);

        // Radera receptet ur DB (SQLite OnDelete SetNull)
        await recipeService.DeleteRecipeAsync(r1.Id);

        var plan = await menuService.GetHomeMealPlanAsync(localToday);

        plan.TodayMeal.HasSavedMenu.Should().BeTrue();
        plan.TodayMeal.HasRecipe.Should().BeFalse();
        plan.TodayMeal.Recipe.Should().BeNull();
    }

    [Fact]
    public async Task GetTodaysDinnerAsync_LegacyBehaviorPreserved_OnlyReturnsConfirmedMenu()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, menuService, _, _, r1, _) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        await menuService.SaveDraftAsync(menu);

        // Äldre metoden kräver Confirmed
        var legacy = await menuService.GetTodaysDinnerAsync(localToday);
        legacy.HasConfirmedMenu.Should().BeFalse();
        legacy.Recipe.Should().BeNull();

        // Bekräfta menyn
        await menuService.ConfirmMenuAsync(menu);
        var legacyConfirmed = await menuService.GetTodaysDinnerAsync(localToday);
        legacyConfirmed.HasConfirmedMenu.Should().BeTrue();
        legacyConfirmed.Recipe.Should().NotBeNull();
    }

    // =========================================================================
    // 2. Komponenttester för Home.razor
    // =========================================================================

    [Fact]
    public async Task Home_WhenConfirmedMenuWithRecipe_RendersHeroCardWithConfirmedBadgeAndCookingLink()
    {
        var localToday = new DateOnly(2026, 11, 2); // Måndag v.45
        var (dbFactory, menuService, _, _, r1, r2) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Servings = 4;
        menu.Days[1].RecipeId = r2.Id;
        menu.Days[1].Servings = 6;
        await menuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".page-title").TextContent.Should().Be("Dagens middag");
            cut.Find(".page-subtitle").TextContent.Should().Contain("Måndag");
            cut.Find(".page-subtitle").TextContent.Should().Contain("Vecka 45");

            // Hero-kort
            var hero = cut.Find(".todays-dinner-card");
            hero.TextContent.Should().Contain("Krämig lax med dill");
            hero.TextContent.Should().Contain("📌 Spikad meny");
            hero.TextContent.Should().Contain("4 portioner");

            // Knappar
            var cookBtn = cut.Find("a.todays-dinner-cook-btn");
            cookBtn.GetAttribute("href").Should().Be($"/recept/{r1.Id}/laga?portioner=4");
            cookBtn.ClassList.Should().Contain("btn-primary-custom");

            var viewBtn = cut.Find("a.todays-dinner-view-btn");
            viewBtn.GetAttribute("href").Should().Be($"/recept/{r1.Id}");
            viewBtn.ClassList.Should().Contain("btn-secondary-custom");

            // I morgon-sektion
            var tomorrow = cut.Find(".home-tomorrow-card");
            tomorrow.TextContent.Should().Contain("I MORGON");
            tomorrow.TextContent.Should().Contain("Vegansk linsgryta");
            tomorrow.TextContent.Should().Contain("6 portioner");
        });
    }

    [Fact]
    public async Task Home_WhenDraftMenuWithRecipe_RendersHeroCardWithDraftBadgeAndWeekLink()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, menuService, _, _, r1, _) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        await menuService.SaveDraftAsync(menu);

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            var hero = cut.Find(".todays-dinner-card");
            hero.TextContent.Should().Contain("📝 Planerat i utkast");
            hero.TextContent.Should().Contain("Planeringen är ett utkast");

            // Matlagningsknappen ska fortfarande fungera i utkast
            var cookBtn = cut.Find("a.todays-dinner-cook-btn");
            cookBtn.GetAttribute("href").Should().Be($"/recept/{r1.Id}/laga?portioner=4");
        });
    }

    [Fact]
    public async Task Home_WhenSavedMenuExistsButNoRecipeToday_RendersEmptyStateB_ChooseMenu()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, menuService, _, _, _, _) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        // Skapa och spara en tom meny utan recept som utkast
        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        await menuService.SaveDraftAsync(menu);

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".todays-dinner-card").Should().BeEmpty();
            var empty = cut.Find(".home-empty-state");
            empty.TextContent.Should().Contain("Ingen middag planerad för idag");
            cut.Find("#home-empty-choose-menu-btn").GetAttribute("href").Should().Be("/veckomeny");
            cut.Find("#home-empty-browse-recipes-btn").GetAttribute("href").Should().Be("/recept");
        });
    }

    [Fact]
    public async Task Home_WhenTomorrowHasNoMeal_HidesTomorrowCard()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, menuService, _, _, r1, _) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        // Endast måndag (idag) har recept, tisdag är tom
        var menu = await menuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = r1.Id;
        await menuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".todays-dinner-card").Should().ContainSingle();
            cut.FindAll(".home-tomorrow-card").Should().BeEmpty("Morgondagens kort ska inte visas om ingen rätt är planerad");
        });
    }

    [Fact]
    public async Task Home_Disposal_CancelsMidnightWatcherCtsWithoutThrowing()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, _, _, _, _, _) = await SetupContextAsync(localToday);
        using var _ = dbFactory;

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => cut.Find(".page-title").TextContent.Should().Be("Dagens middag"));

        var act = () => cut.Dispose();
        act.Should().NotThrow();
    }
}
