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
using Xunit;
using ShoppingPreviewComponent = Receptbanken.Web.Components.WeeklyMenu.WeeklyMenuShoppingPreview;

namespace Receptbanken.Tests.Components;

public sealed class WeeklyMenuBarrierAndClearComponentTests : TestContext
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
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(weeklyMenuService);
        Services.AddSingleton<ILocalDateProvider>(localDateProvider);
        Services.AddSingleton(aggregator);
        Services.AddSingleton(parser);
        Services.AddSingleton(notifier);
        Services.AddSingleton(shoppingService);
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (dbFactory, recipeService, categoryService, weeklyMenuService);
    }

    [Fact]
    public async Task WeeklyMenuPage_WhenMenuIsDraft_DisablesCreateShoppingListButtons_AndShowsHint()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, _, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var recipe = await recipeService.CreateRecipeAsync(new Recipe { Title = "Köttbullar", Servings = 4 }, []);
        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = recipe.Id;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            var topBtn = cut.Find("button.create-shopping-list-btn");
            topBtn.HasAttribute("disabled").Should().BeTrue("Skapa inköpslista ska vara inaktiverad när menyn är utkast");

            var bottomBtn = cut.Find("button.create-shopping-list-bottom-btn");
            bottomBtn.HasAttribute("disabled").Should().BeTrue("Nedre knappen ska också vara inaktiverad");

            var hints = cut.FindAll(".weekly-action-hint");
            hints.Should().Contain(h => h.TextContent.Contains("Spika veckomenyn för att kunna skapa en inköpslista."));
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_WhenMenuIsConfirmed_EnablesCreateShoppingListButtons()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, _, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var recipe = await recipeService.CreateRecipeAsync(new Recipe { Title = "Köttbullar", Servings = 4 }, []);
        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = recipe.Id;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            var topBtn = cut.Find("button.create-shopping-list-btn");
            topBtn.HasAttribute("disabled").Should().BeFalse("Skapa inköpslista ska vara aktiv när menyn är spikad");

            var bottomBtn = cut.Find("button.create-shopping-list-bottom-btn");
            bottomBtn.HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_WhenMenuHasNoRecipes_DisablesConfirmButtons_AndShowsHint()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, _, _, _) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var cut = RenderComponent<WeeklyMenuPage>();

        cut.WaitForAssertion(() =>
        {
            var topConfirmBtn = cut.Find("button.confirm-week-btn");
            topConfirmBtn.HasAttribute("disabled").Should().BeTrue("En tom vecka ska inte kunna spikas");

            var bottomConfirmBtn = cut.Find("button.confirm-week-bottom-btn");
            bottomConfirmBtn.HasAttribute("disabled").Should().BeTrue();

            var hints = cut.FindAll(".weekly-action-hint");
            hints.Should().Contain(h => h.TextContent.Contains("Välj minst en maträtt för att spika veckomenyn."));
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_ClearMenu_OpensConfirmDialog_AndOnCancel_LeavesMenuUntouched()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, _, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var recipe = await recipeService.CreateRecipeAsync(new Recipe { Title = "Fisksoppa", Servings = 4 }, []);
        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = recipe.Id;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        // Klicka på Rensa veckomeny
        cut.WaitForAssertion(() => cut.Find("button.clear-menu-btn").Should().NotBeNull());
        cut.Find("button.clear-menu-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find(".modal-dialog-custom");
            dialog.TextContent.Should().Contain("Rensa veckomeny för vecka");
            dialog.TextContent.Should().Contain("Alla planerade rätter och portionsantal");
        });

        // Klicka på Avbryt
        var cancelBtn = cut.Find(".modal-dialog-custom button.btn-secondary-custom");
        cancelBtn.TextContent.Should().Contain("Avbryt");
        cancelBtn.Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".modal-dialog-custom").Should().BeEmpty();
            // Menyn är oförändrad och spikad
            cut.Find("button.confirm-week-btn").TextContent.Should().Contain("Spara ändringar i spikad meny");
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_ClearMenu_WhenConfirmed_ClearsAllDays_AndSetsStatusToDraft()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, _, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var recipe = await recipeService.CreateRecipeAsync(new Recipe { Title = "Fisksoppa", Servings = 4 }, []);
        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[0].RecipeId = recipe.Id;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();

        // Klicka på Rensa veckomeny
        cut.WaitForAssertion(() => cut.Find("button.clear-menu-btn").Should().NotBeNull());
        cut.Find("button.clear-menu-btn").Click();

        // Klicka på Rensa veckomeny (bekräfta i dialogen)
        cut.WaitForAssertion(() => cut.Find(".modal-dialog-custom button.btn-danger-custom").Should().NotBeNull());
        cut.Find(".modal-dialog-custom button.btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
        {
            // Bekräftelsemeddelande visas
            cut.Find(".weekly-feedback-banner").TextContent.Should().Contain("har rensats och är nu ett tomt utkast");

            // Spikningsknappen visar nu "Spika veckomenyn" och är inaktiverad eftersom menyn nu är tom
            var confirmBtn = cut.Find("button.confirm-week-btn");
            confirmBtn.TextContent.Should().Contain("Spika veckomenyn");
            confirmBtn.HasAttribute("disabled").Should().BeTrue();

            // Skapa inköpslista är inaktiverad
            cut.Find("button.create-shopping-list-btn").HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Fact]
    public async Task WeeklyMenuShoppingPreview_WhenWeekIsDraft_RendersDraftBanner_AndDisablesConfirmButton()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, _, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var recipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Tacos",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "färs", RawText = "500 g färs" }]
            },
            []);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[4].RecipeId = recipe.Id;
        await weeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<ShoppingPreviewComponent>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.InitialWeekDate, localToday)
            .Add(p => p.AllowWeekNavigation, true));

        cut.WaitForAssertion(() =>
        {
            // Draft banner visas
            var draftBanner = cut.Find(".weekly-preview-draft-banner");
            draftBanner.TextContent.Should().Contain("Veckomenyn är ett utkast");
            draftBanner.TextContent.Should().Contain("Spika den under Veckomeny innan du lägger till ingredienser");

            // Bekräfta-knappen är inaktiverad
            var confirmBtn = cut.Find("button.weekly-preview-confirm-btn");
            confirmBtn.HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Fact]
    public async Task WeeklyMenuShoppingPreview_WhenWeekIsConfirmed_EnablesConfirmButton()
    {
        var localToday = new DateOnly(2026, 11, 2);
        var (dbFactory, recipeService, _, weeklyMenuService) = await SetupServicesAsync(localToday);
        using var _ = dbFactory;

        var recipe = await recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Tacos",
                Servings = 4,
                Ingredients = [new Ingredient { OrderIndex = 1, Quantity = 500m, Unit = "g", Name = "färs", RawText = "500 g färs" }]
            },
            []);

        var menu = await weeklyMenuService.GetOrCreateWeekMenuAsync(localToday);
        menu.Days[4].RecipeId = recipe.Id;
        await weeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<ShoppingPreviewComponent>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.InitialWeekDate, localToday)
            .Add(p => p.AllowWeekNavigation, true));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-preview-draft-banner").Should().BeEmpty();
            var confirmBtn = cut.Find("button.weekly-preview-confirm-btn");
            confirmBtn.HasAttribute("disabled").Should().BeFalse();
            confirmBtn.TextContent.Should().Contain("Lägg till i inköpslistan");
        });
    }
}
