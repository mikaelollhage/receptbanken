using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;

namespace Receptbanken.Tests.Components;

public class ShoppingListRealtimeComponentTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = new();

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            lock (_timers)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            _now = _now.Add(delta);
            List<ManualTimer> ready;
            lock (_timers)
            {
                ready = _timers.Where(t => !t.Disposed && t.DueAt <= _now).ToList();
            }

            foreach (var timer in ready)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _provider;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public DateTimeOffset DueAt { get; private set; }
            public bool Disposed { get; private set; }

            public ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime)
            {
                _provider = provider;
                _callback = callback;
                _state = state;
                DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : provider._now.Add(dueTime);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : _provider._now.Add(dueTime);
                return true;
            }

            public void Fire()
            {
                if (Disposed) return;
                DueAt = DateTimeOffset.MaxValue;
                _callback(_state);
            }

            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private static TestContext CreateSessionContext(
        TestDbContextFactory dbFactory,
        ShoppingListNotifier sharedNotifier,
        TimeProvider? timeProvider = null)
    {
        var ctx = new TestContext();
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        // Each session gets its own Scoped ShoppingListService instance sharing the Singleton notifier and DB
        var shoppingService = new ShoppingListService(dbFactory, aggregator, parser, sharedNotifier);

        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton(calc);
        ctx.Services.AddSingleton(sharedNotifier);
        ctx.Services.AddSingleton(recipeService);
        ctx.Services.AddSingleton(shoppingService);
        if (timeProvider != null)
        {
            ctx.Services.AddSingleton(timeProvider);
        }

        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [Fact]
    public void TwoSessions_AddCheckUndoRestoreEditDeleteAndClear_ShouldSynchronizeInRealTime()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        phoneA.WaitForAssertion(() => phoneA.Markup.Should().Contain("Inköpslistan är tom"));
        phoneB.WaitForAssertion(() => phoneB.Markup.Should().Contain("Inköpslistan är tom"));

        // 1. Phone A adds "1,5 l mjölk" and "2 förp bacon"
        phoneA.Find("#shopping-manual-input").Input("1,5 l mjölk");
        phoneA.Find("#shopping-add-manual-btn").Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(1));

        phoneA.Find("#shopping-manual-input").Input("2 förp bacon");
        phoneA.Find("#shopping-add-manual-btn").Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2));

        // Both phones automatically show both items in "Att handla (2)", and Phone B shows no noisy banner
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
        });
        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneB.Find(".shopping-unchecked-list").TextContent.Should().Contain("1,5 l mjölk");
            phoneB.Find(".shopping-unchecked-list").TextContent.Should().Contain("2 förp bacon");
            phoneB.FindAll(".rb-feedback").Should().BeEmpty("andra telefonen ska uppdateras diskret utan grön bekräftelsebanner");
        });

        // 2. Phone B checks off "1,5 l mjölk" by tapping the row check area
        phoneB.FindAll(".shopping-unchecked-list .shopping-row-check-target").First().Click();

        // Both phones move "1,5 l mjölk" to "Klart (1)" and leave "2 förp bacon" in "Att handla (1)"
        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle();
            phoneB.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            phoneB.Find(".shopping-checked-list").TextContent.Should().Contain("1,5 l mjölk");

            // Phone B shows the contextual Undo banner with "Ångra" button
            var undoBanner = phoneB.Find(".shopping-undo-feedback.rb-feedback-success");
            undoBanner.TextContent.Should().Contain("\"mjölk\" markerad som klar.");
            phoneB.Find(".shopping-undo-feedback .rb-feedback-action-btn").TextContent.Trim().Should().Be("Ångra");
        });

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle();
            phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            phoneA.Find(".shopping-checked-list").TextContent.Should().Contain("1,5 l mjölk");
            // Phone A must NOT show Phone B's Undo banner
            phoneA.FindAll(".shopping-undo-feedback").Should().BeEmpty();
        });

        // 3. Phone B taps "Ångra" -> "1,5 l mjölk" returns to its original first position in "Att handla (2)" on both phones
        phoneB.Find(".shopping-undo-feedback .rb-feedback-action-btn").Click();

        phoneA.WaitForAssertion(() =>
        {
            var uncheckedTexts = phoneA.FindAll(".shopping-unchecked-list .shopping-item-text")
                .Select(x => x.TextContent.Trim())
                .ToList();
            uncheckedTexts.Should().Equal("1,5 l mjölk", "2 förp bacon");
            phoneA.FindAll(".shopping-checked-list").Should().BeEmpty();
        });
        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-undo-feedback").Should().BeEmpty();
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
        });

        // 4. Phone A checks off "1,5 l mjölk", then Phone B restores it by tapping the item directly in "Klart"
        phoneA.FindAll(".shopping-unchecked-list .shopping-row-check-target").First().Click();
        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle());

        phoneB.Find(".shopping-checked-list .shopping-row-check-target").Click();

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.FindAll(".shopping-checked-list").Should().BeEmpty();
            // Phone A's Undo banner automatically clears because the item is no longer checked
            phoneA.FindAll(".shopping-undo-feedback").Should().BeEmpty();
        });

        // 5. Phone A edits "2 förp bacon" -> "3 förp kalkonbacon"
        phoneA.FindAll(".shopping-unchecked-list .shopping-edit-btn").Last().Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-edit-qty").Should().ContainSingle());
        phoneA.Find(".shopping-edit-qty").Change("3");
        phoneA.Find(".shopping-edit-unit").Change("förp");
        phoneA.Find(".shopping-edit-name").Change("kalkonbacon");
        phoneA.Find(".shopping-save-edit-btn").Click();

        phoneB.WaitForAssertion(() =>
        {
            phoneB.Find(".shopping-unchecked-list").TextContent.Should().Contain("3 förp kalkonbacon");
        });

        // 6. Phone B checks off "3 förp kalkonbacon" and clicks "Rensa klara (1)"
        phoneB.FindAll(".shopping-unchecked-list .shopping-row-check-target").Last().Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle());

        phoneB.Find(".shopping-clear-checked-btn").Click();

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-checked-list").Should().BeEmpty();
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle();
        });

        // 7. Phone A clicks "Töm hela listan" and confirms in ConfirmDialog -> both phones show empty state
        phoneA.Find(".shopping-clear-all-btn").Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".modal-dialog-custom button.btn-danger-custom").Should().ContainSingle());
        phoneA.Find(".modal-dialog-custom button.btn-danger-custom").Click();

        phoneB.WaitForAssertion(() =>
        {
            phoneB.Markup.Should().Contain("Inköpslistan är tom");
        });
    }

    [Fact]
    public void TwoSessions_WhenPhoneAIsEditingItem_AndPhoneBChecksOffAnotherItem_PhoneAEditStateIsPreserved()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        phoneA.Find("#shopping-manual-input").Input("1 l fil");
        phoneA.Find("#shopping-add-manual-btn").Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle());

        phoneA.Find("#shopping-manual-input").Input("500 g smör");
        phoneA.Find("#shopping-add-manual-btn").Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2));

        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2));

        // Phone A opens edit mode on "1 l fil" and changes name input (without saving yet)
        phoneA.FindAll(".shopping-unchecked-list .shopping-edit-btn").First().Click();
        phoneA.Find(".shopping-edit-name").Change("naturell yoghurt");

        // Meanwhile, Phone B checks off "500 g smör"
        phoneB.FindAll(".shopping-unchecked-list .shopping-row-check-target").Last().Click();

        // Phone A receives the real-time update (smör moves to Klart), while Phone A's edit panel for "1 l fil" remains open!
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            phoneA.FindAll(".shopping-edit-panel").Should().ContainSingle();
            phoneA.Find(".shopping-edit-name").GetAttribute("value").Should().Be("naturell yoghurt");
        });
    }

    [Fact]
    public async Task UndoBanner_ShouldAutoDismissAfter4Seconds_AndRecipeAdditionFromDetailShouldSyncToOpenList()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();
        var timeProvider = new ManualTimeProvider();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier, timeProvider);
        var shoppingService = ctxA.Services.GetRequiredService<ShoppingListService>();
        var recipeService = ctxA.Services.GetRequiredService<RecipeService>();

        var recipe = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Pannkakor",
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients =
            [
                new Ingredient { OrderIndex = 1, Quantity = 6m, Unit = "dl", Name = "mjölk", RawText = "6 dl mjölk" },
                new Ingredient { OrderIndex = 2, Quantity = 3m, Unit = "st", Name = "ägg", RawText = "3 st ägg" }
            ]
        });

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        phoneA.WaitForAssertion(() => phoneA.Markup.Should().Contain("Inköpslistan är tom"));

        // Adding recipe ingredients via ShoppingListService (as RecipeDetail does) automatically updates the open shopping list
        await shoppingService.AddRecipeIngredientsAsync(recipe.Id, 4);

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.Find(".shopping-unchecked-list").TextContent.Should().Contain("6 dl mjölk");
        });

        // Check off "6 dl mjölk" -> Undo banner appears
        phoneA.FindAll(".shopping-unchecked-list .shopping-row-check-target").First().Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-undo-feedback").Should().ContainSingle());

        // Advance 3.9 seconds -> still visible
        timeProvider.Advance(TimeSpan.FromMilliseconds(3900));
        phoneA.FindAll(".shopping-undo-feedback").Should().ContainSingle();

        // Advance to 4.1 seconds -> auto-dismissed
        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-undo-feedback").Should().BeEmpty());
    }

    [Fact]
    public async Task ReconnectAndDispose_OnClientResumedFetchesMissedUpdates_AndDisposeUnsubscribesCleanly()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        // Dispose Phone B and verify subsequent changes on Phone A do not throw
        phoneB.Instance.Dispose();

        phoneA.Find("#shopping-manual-input").Input("1 kg potatis");
        phoneA.Find("#shopping-add-manual-btn").Click();
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle());

        // Simulate a database change that bypassed the in-memory notifier (e.g. while phone A was disconnected)
        var unnotifiedService = new ShoppingListService(
            dbFactory,
            new ShoppingListAggregator(new ServingCalculator()),
            new SwedishIngredientParser(),
            new ShoppingListNotifier());

        await unnotifiedService.AddManualItemAsync("2 st vitlöksklyftor");

        // Before OnClientResumedAsync, phoneA still has 1 item
        phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle();

        // Phone A wakes up / reconnects -> OnClientResumedAsync reloads from SQLite
        await phoneA.Instance.OnClientResumedAsync();

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.Find(".shopping-unchecked-list").TextContent.Should().Contain("2 st vitlöksklyftor");
        });
    }

    [Fact]
    public void ShoppingMode_And_NewRecipeButtonVisibility_ShouldWorkAccordingToEtapp3A2B()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctx = CreateSessionContext(dbFactory, sharedNotifier);
        ctx.Services.AddSingleton<Receptbanken.Web.Services.AppNotificationService>();

        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        // 1. On the home page ("/"), MainLayout renders the global "Nytt recept" header
        nav.NavigateTo("http://localhost/");
        var layoutOnHome = ctx.RenderComponent<Receptbanken.Web.Components.Layout.MainLayout>();
        layoutOnHome.FindAll(".app-header").Should().ContainSingle();
        layoutOnHome.Find(".app-header").TextContent.Should().Contain("Nytt recept");

        // 2. When navigating to "/inkopslista", MainLayout hides ".app-header" and "Nytt recept" even in normal mode
        nav.NavigateTo("http://localhost/inkopslista");
        layoutOnHome.WaitForAssertion(() =>
        {
            layoutOnHome.FindAll(".app-header").Should().BeEmpty("Nytt recept ska döljas på inköpslistesidan även i normalläge");
        });

        // 3. Render ShoppingPlaceholder with unchecked and checked items
        var view = ctx.RenderComponent<ShoppingPlaceholder>();
        view.WaitForAssertion(() => view.Markup.Should().Contain("Inköpslistan är tom"));

        view.Find("#shopping-manual-input").Input("1 l mjölk");
        view.Find("#shopping-add-manual-btn").Click();
        view.WaitForAssertion(() => view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle());

        view.Find("#shopping-manual-input").Input("2 st avokado");
        view.Find("#shopping-add-manual-btn").Click();
        view.WaitForAssertion(() => view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2));

        // Check off one item so both Att handla and Klart are visible
        view.FindAll(".shopping-unchecked-list .shopping-row-check-target").ToList().Last().Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle();
            view.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
        });

        // In normal mode: "Handla", "Töm hela listan", "Lägg till från recept", edit button (✏️) and delete button (✕) are visible
        view.FindAll("#shopping-enter-mode-btn").Should().ContainSingle();
        view.FindAll(".shopping-clear-all-btn").Should().ContainSingle();
        view.FindAll("#shopping-recipe-toggle-btn").Should().ContainSingle();
        view.FindAll(".shopping-edit-btn").Should().ContainSingle();
        view.FindAll(".shopping-delete-btn").Should().ContainSingle();
        view.FindAll(".shopping-drag-handle").Should().ContainSingle();

        // 4. Activate "Handla" (Shopping Mode)
        view.Find("#shopping-enter-mode-btn").Click();

        view.WaitForAssertion(() =>
        {
            view.Find(".shopping-page-container").ClassList.Should().Contain("is-shopping-mode");
            view.FindAll("#shopping-exit-mode-btn").Should().ContainSingle();
            view.Find(".shopping-mode-topbar").TextContent.Should().Contain("1 kvar att handla");

            // Secondary/destructive and edit controls are hidden in shopping mode
            view.FindAll(".shopping-clear-all-btn").Should().BeEmpty();
            view.FindAll("#shopping-recipe-toggle-btn").Should().BeEmpty();
            view.FindAll(".shopping-edit-btn").Should().BeEmpty("redigeringsknappar ska döljas i handlingsläge");
            view.FindAll(".shopping-delete-btn").Should().BeEmpty("raderingsknappar ska döljas i handlingsläge");
            view.FindAll(".shopping-clear-checked-btn").Should().BeEmpty();

            // Core shopping actions remain available in shopping mode (quick-add, check-off, drag handle, Klart section)
            view.FindAll("#shopping-manual-input").Should().ContainSingle();
            view.FindAll(".shopping-drag-handle").Should().ContainSingle();
            view.FindAll(".shopping-checked-list .shopping-row-check-target").Should().ContainSingle();
        });

        // 5. Exit Shopping Mode -> normal controls return
        view.Find("#shopping-exit-mode-btn").Click();

        view.WaitForAssertion(() =>
        {
            view.Find(".shopping-page-container").ClassList.Should().NotContain("is-shopping-mode");
            view.FindAll("#shopping-enter-mode-btn").Should().ContainSingle();
            view.FindAll(".shopping-edit-btn").Should().ContainSingle();
            view.FindAll(".shopping-delete-btn").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task TwoSessions_ReorderViaDragAndAccessibleButtons_ShouldSyncInRealTimeAndHandleVersionConflict()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        phoneA.WaitForAssertion(() => phoneA.Markup.Should().Contain("Inköpslistan är tom"));
        phoneB.WaitForAssertion(() => phoneB.Markup.Should().Contain("Inköpslistan är tom"));

        foreach (var itemText in new[] { "Mjölk", "Bröd", "Ägg", "Smör" })
        {
            phoneA.Find("#shopping-manual-input").Input(itemText);
            phoneA.Find("#shopping-add-manual-btn").Click();
        }

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(4);
        });
        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(4);
        });

        // 1. Phone A drags "Smör" (position 4) onto "Bröd" (position 2) via touch drag callbacks
        var rowsA = phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").ToList();
        var breadId = int.Parse(rowsA[1].GetAttribute("data-item-id")!);
        var butterId = int.Parse(rowsA[3].GetAttribute("data-item-id")!);

        await phoneA.Instance.OnTouchDragStartAsync(butterId);
        await phoneA.Instance.OnTouchDropAsync(butterId, breadId);

        // Both phones automatically display: Mjölk, Smör, Bröd, Ägg (with no green confirmation banner)
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Mjölk", "Smör", "Bröd", "Ägg");
            phoneA.FindAll(".shopping-list-feedback.rb-feedback").Should().BeEmpty();
        });

        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Mjölk", "Smör", "Bröd", "Ägg");
        });

        // 2. In Shopping Mode on Phone B, use the accessible "Flytta upp" button via the drag handle menu to move "Ägg" up
        phoneB.Find("#shopping-enter-mode-btn").Click();
        phoneB.WaitForAssertion(() => phoneB.Find(".shopping-page-container").ClassList.Should().Contain("is-shopping-mode"));

        phoneB.FindAll(".shopping-drag-handle").Last().Click();
        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-item-reorder-bar .shopping-move-up-btn").Should().ContainSingle());
        phoneB.Find(".shopping-item-reorder-bar .shopping-move-up-btn").Click();

        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Mjölk", "Smör", "Ägg", "Bröd");
        });
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Mjölk", "Smör", "Ägg", "Bröd");
        });

        // 3. Simulate a concurrent reorder conflict:
        // Phone A starts dragging "Bröd", and before Phone A drops it, Phone B moves "Mjölk" down
        var currentRowsA = phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").ToList();
        var firstItemId = int.Parse(currentRowsA[0].GetAttribute("data-item-id")!);
        var lastItemId = int.Parse(currentRowsA[3].GetAttribute("data-item-id")!);

        await phoneA.Instance.OnTouchDragStartAsync(lastItemId);

        // Phone B moves "Mjölk" down while Phone A is mid-drag
        phoneB.FindAll(".shopping-drag-handle").First().Click();
        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-item-reorder-bar .shopping-move-down-btn").Should().ContainSingle());
        phoneB.Find(".shopping-item-reorder-bar .shopping-move-down-btn").Click();

        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Smör", "Mjölk", "Ägg", "Bröd");
        });

        // Now Phone A finishes its stale drag -> rejected with conflict warning and Phone A reloads Phone B's order
        await phoneA.Instance.OnTouchDropAsync(lastItemId, firstItemId);

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Smör", "Mjölk", "Ägg", "Bröd");
            phoneA.Find(".shopping-list-feedback").TextContent.Should().Contain("Ordningen ändrades");
        });
    }

    [Fact]
    public async Task TouchReorderGestures_ShortTapTogglesReorderMenu_AndCancelledDragCleansUpWithoutReordering()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctx = CreateSessionContext(dbFactory, sharedNotifier);
        var view = ctx.RenderComponent<ShoppingPlaceholder>();

        var count = 0;
        foreach (var itemText in new[] { "Kaffe", "Te", "Kakao" })
        {
            count++;
            view.Find("#shopping-manual-input").Input(itemText);
            view.Find("#shopping-add-manual-btn").Click();
            view.WaitForAssertion(() =>
            {
                view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(count);
            });
        }

        var rows = view.FindAll(".shopping-unchecked-list .shopping-item-row").ToList();
        var kaffeId = int.Parse(rows[0].GetAttribute("data-item-id")!);
        var teId = int.Parse(rows[1].GetAttribute("data-item-id")!);
        var kakaoId = int.Parse(rows[2].GetAttribute("data-item-id")!);

        // 1. Normal short tap / click on drag handle toggles the accessible "Flytta upp / Flytta ned" bar
        view.FindAll(".shopping-drag-handle").First().Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
            view.Find(".shopping-item-reorder-bar .shopping-move-up-btn").HasAttribute("disabled").Should().BeTrue("första varan kan inte flyttas uppåt");
            view.Find(".shopping-item-reorder-bar .shopping-move-down-btn").HasAttribute("disabled").Should().BeFalse();
        });

        // Tapping the handle again closes the reorder bar
        view.FindAll(".shopping-drag-handle").First().Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar").Should().BeEmpty();
        });

        // 2. Early swipe or cancelled drag: OnTouchDragCancelAsync cleans up without changing item order
        await view.Instance.OnTouchDragStartAsync(kaffeId);
        await view.Instance.OnTouchDragCancelAsync();

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Kaffe", "Te", "Kakao");
        });

        // 3. Desktop HTML5 drag-and-drop: dragstart on "Kakao" and drop on "Kaffe"
        view.FindAll(".shopping-drag-handle").Last().DragStart();
        view.FindAll(".shopping-unchecked-list .shopping-item-row").First().Drop();

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Kakao", "Kaffe", "Te");
        });

        // 4. Checking off an item by tapping the check target works completely independently of the drag handle
        view.FindAll(".shopping-unchecked-list .shopping-row-check-target").First().Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            view.Find(".shopping-checked-list").TextContent.Should().Contain("Kakao");
            view.FindAll(".shopping-undo-feedback").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task MoveButtons_StayOpenAcrossConsecutiveMoves_AndDragSuppressesAccidentalClick()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctx = CreateSessionContext(dbFactory, sharedNotifier);
        var view = ctx.RenderComponent<ShoppingPlaceholder>();

        var count = 0;
        foreach (var itemText in new[] { "Vara 1", "Vara 2", "Vara 3", "Vara 4" })
        {
            count++;
            view.Find("#shopping-manual-input").Input(itemText);
            view.Find("#shopping-add-manual-btn").Click();
            view.WaitForAssertion(() =>
            {
                view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(count);
            });
        }

        view.WaitForAssertion(() => view.FindAll(".shopping-drag-handle").Should().HaveCount(4));

        // 1. Open reorder menu on Vara 4 (last item)
        view.FindAll(".shopping-drag-handle").ToList()[3].Click();

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
            view.Find(".shopping-item-reorder-bar .shopping-move-up-btn").HasAttribute("disabled").Should().BeFalse();
            view.Find(".shopping-item-reorder-bar .shopping-move-down-btn").HasAttribute("disabled").Should().BeTrue();
        });

        // 2. Click Move Up once: Vara 4 moves to index 2. Menu STAYS OPEN!
        view.Find(".shopping-item-reorder-bar .shopping-move-up-btn").Click();

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara 1", "Vara 2", "Vara 4", "Vara 3");
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle("menyn ska förbli öppen efter flyttning");
            view.Find(".shopping-item-reorder-bar .shopping-move-up-btn").HasAttribute("disabled").Should().BeFalse();
            view.Find(".shopping-item-reorder-bar .shopping-move-down-btn").HasAttribute("disabled").Should().BeFalse();
        });

        // 3. Click Move Up again immediately without reopening menu: Vara 4 moves to index 1. Menu STAYS OPEN!
        view.Find(".shopping-item-reorder-bar .shopping-move-up-btn").Click();

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara 1", "Vara 4", "Vara 2", "Vara 3");
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle("menyn ska förbli öppen vid flera steg i följd");
        });

        // 4. Click Move Down: Vara 4 moves back down to index 2. Menu STAYS OPEN!
        view.Find(".shopping-item-reorder-bar .shopping-move-down-btn").Click();

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara 1", "Vara 2", "Vara 4", "Vara 3");
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
        });

        // 5. Dragging an item committed via mouse HTML5 drag does not open menu automatically
        var dragHandle2 = view.FindAll(".shopping-drag-handle").ToList()[1]; // Vara 2
        dragHandle2.DragStart();
        view.FindAll(".shopping-unchecked-list .shopping-item-row").ToList()[0].Drop();

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara 2", "Vara 1", "Vara 4", "Vara 3");
            view.FindAll(".shopping-item-reorder-bar").Should().BeEmpty("musdragning öppnar inte flyttmenyn automatiskt vid släpp");
        });

        dragHandle2.DragEnd();

        // 6. The FIRST deliberate click on the drag handle after drag immediately opens the menu
        view.FindAll(".shopping-drag-handle").ToList()[0].Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle("första avsiktliga klicket efter dragning ska öppna menyn direkt");
        });

        // Close menu with a second click
        view.FindAll(".shopping-drag-handle").ToList()[0].Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar").Should().BeEmpty("klick på öppet handtag ska stänga menyn");
        });

        // 7. Cancelled mouse drag (dragstart -> dragend without drop) followed by click opens on first click
        var handleToCancel = view.FindAll(".shopping-drag-handle").ToList()[0];
        handleToCancel.DragStart();
        handleToCancel.DragEnd();
        view.FindAll(".shopping-item-reorder-bar").Should().BeEmpty("avbruten dragning öppnar inte menyn");

        view.FindAll(".shopping-drag-handle").ToList()[0].Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle("första klicket efter avbruten dragning ska öppna menyn direkt");
        });
    }
}
