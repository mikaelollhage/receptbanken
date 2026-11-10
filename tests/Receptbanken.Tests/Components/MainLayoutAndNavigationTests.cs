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
using Receptbanken.Web.Components.Layout;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class MainLayoutAndNavigationTests : TestContext
{
    private sealed class FixedLocalDateProvider : ILocalDateProvider
    {
        private readonly DateOnly _today;
        public FixedLocalDateProvider(DateOnly today) => _today = today;
        public DateOnly GetLocalToday() => _today;
        public void SetClientLocalDate(DateOnly clientDate) { }
    }

    private (TestDbContextFactory DbFactory, RecipeService RecipeService) SetupTestContext(bool seedRecipes = true)
    {
        var dbFactory = new TestDbContextFactory();
        if (seedRecipes)
        {
            using var db = dbFactory.CreateDbContext();
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        var weeklyMenuService = new WeeklyMenuService(dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        var localDateProvider = new FixedLocalDateProvider(new DateOnly(2026, 11, 2));
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

        return (dbFactory, recipeService);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost/inkopslista")]
    [InlineData("http://localhost/veckomeny")]
    [InlineData("http://localhost/recept")]
    public void MainLayout_ShouldNotRenderAppHeaderOrGlobalNewRecipeButton_OnAnyRoute(string url)
    {
        SetupTestContext(seedRecipes: false);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(url);

        var cut = RenderComponent<MainLayout>();

        cut.FindAll(".app-header").Should().BeEmpty(
            "UX-2D.1: den globala .app-header-sektionen har tagits bort från applikationens layout.");
        cut.FindAll("a[href='/recept/ny']").Should().BeEmpty(
            "UX-2D.1: receptskapande ska inte ligga som en global knapp i MainLayout.");
        cut.FindAll(".app-content").Should().ContainSingle(
            "Applikationens huvudinnehållscontainer (.app-content) ska renderas ren utan överflödig header.");
    }

    [Fact]
    public void Home_WhenRecipesExist_ShouldRenderLocalNewRecipeButton_InPageHeader()
    {
        var (dbFactory, _) = SetupTestContext(seedRecipes: true);
        using var _ = dbFactory;

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            var pageHeader = cut.Find(".page-header");
            var newRecipeBtn = pageHeader.QuerySelector("#home-create-recipe-btn");
            newRecipeBtn.Should().NotBeNull("Receptöversikten ska ha en lokal 'Nytt recept'-knapp i sidhuvudet");
            newRecipeBtn!.GetAttribute("href").Should().Be("/recept/ny");
            newRecipeBtn.ClassList.Should().Contain("btn-primary-custom");
            newRecipeBtn.TextContent.Should().Contain("Nytt recept");

            var importBtn = pageHeader.QuerySelector("a[href='/importera']");
            importBtn.Should().NotBeNull("Receptöversikten ska också erbjuda 'Importera från länk'");
            importBtn!.ClassList.Should().Contain("btn-secondary-custom");
        });
    }

    [Fact]
    public void Home_WhenEmptyState_ShouldRenderBothHeaderNewRecipeButton_AndEmptyStateActionButtons()
    {
        var (dbFactory, _) = SetupTestContext(seedRecipes: false);
        using var _ = dbFactory;

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            // Sidhuvudets lokala knapp ska finnas även när listan är tom
            var pageHeader = cut.Find(".page-header");
            var headerBtn = pageHeader.QuerySelector("#home-create-recipe-btn");
            headerBtn.Should().NotBeNull();
            headerBtn!.GetAttribute("href").Should().Be("/recept/ny");
            headerBtn.ClassList.Should().Contain("btn-primary-custom");

            // Empty state-ytan ska också erbjuda knappar
            var emptyState = cut.Find(".empty-state");
            emptyState.Should().NotBeNull();
            var emptyStateCreateBtn = emptyState.QuerySelector("a[href='/recept/ny']");
            emptyStateCreateBtn.Should().NotBeNull();
            emptyStateCreateBtn!.TextContent.Should().Contain("Skapa recept");
            emptyStateCreateBtn.ClassList.Should().Contain("btn-primary-custom");

            var emptyStateImportBtn = emptyState.QuerySelector("a[href='/importera']");
            emptyStateImportBtn.Should().NotBeNull();
            emptyStateImportBtn!.TextContent.Should().Contain("Importera från länk");
            emptyStateImportBtn.ClassList.Should().Contain("btn-secondary-custom");
        });
    }
}
