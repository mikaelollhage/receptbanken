using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
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
        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-clear-checked-btn").Should().ContainSingle());

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
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-edit-name").Should().ContainSingle());
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
        await phoneB.Instance.DisposeAsync();

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
    public async Task ShoppingMode_And_NewRecipeButtonVisibility_ShouldWorkAccordingToEtapp3A2B()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctx = CreateSessionContext(dbFactory, sharedNotifier);
        ctx.Services.AddSingleton<Receptbanken.Web.Services.AppNotificationService>();

        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        // 1. Under UX-2D.1 har den globala .app-header och "Nytt recept" i MainLayout tagits bort
        nav.NavigateTo("http://localhost/");
        var layoutOnHome = ctx.RenderComponent<Receptbanken.Web.Components.Layout.MainLayout>();
        layoutOnHome.FindAll(".app-header").Should().BeEmpty("MainLayout ska inte ha någon global .app-header");
        layoutOnHome.FindAll("a[href='/recept/ny']").Should().BeEmpty("Global 'Nytt recept'-knapp ska inte finnas i MainLayout");

        // 2. Även på /inkopslista förblir MainLayout ren utan global header
        nav.NavigateTo("http://localhost/inkopslista");
        layoutOnHome.WaitForAssertion(() =>
        {
            layoutOnHome.FindAll(".app-header").Should().BeEmpty();
            layoutOnHome.FindAll("a[href='/recept/ny']").Should().BeEmpty();
        });

        // 3. Render ShoppingPlaceholder with unchecked and checked items
        var view = ctx.RenderComponent<ShoppingPlaceholder>();
        view.WaitForAssertion(() => view.Markup.Should().Contain("Inköpslistan är tom"));

        await view.InvokeAsync(() => view.Find("#shopping-manual-input").Input("1 l mjölk"));
        await view.InvokeAsync(() => view.Find("#shopping-add-manual-btn").ClickAsync(new MouseEventArgs()));
        view.WaitForAssertion(() => view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle());

        await view.InvokeAsync(() => view.Find("#shopping-manual-input").Input("2 st avokado"));
        await view.InvokeAsync(() => view.Find("#shopping-add-manual-btn").ClickAsync(new MouseEventArgs()));
        view.WaitForAssertion(() => view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2));

        // Check off one item so both Att handla and Klart are visible
        await view.InvokeAsync(() => view.FindAll(".shopping-unchecked-list .shopping-row-check-target").ToList()[1].ClickAsync(new MouseEventArgs()));
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
        await view.InvokeAsync(() => view.Find("#shopping-enter-mode-btn").ClickAsync(new MouseEventArgs()));

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

        var count = 0;
        foreach (var itemText in new[] { "Mjölk", "Bröd", "Ägg", "Smör" })
        {
            count++;
            phoneA.Find("#shopping-manual-input").Input(itemText);
            phoneA.Find("#shopping-add-manual-btn").Click();
            phoneA.WaitForAssertion(() =>
            {
                phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(count);
            });
        }

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
        view.WaitForAssertion(() => view.FindAll(".shopping-unchecked-list .shopping-drag-handle").Should().HaveCount(3));
        view.Find(".shopping-unchecked-list .shopping-drag-handle").Click();
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
            view.Find(".shopping-item-reorder-bar .shopping-move-up-btn").HasAttribute("disabled").Should().BeTrue("första varan kan inte flyttas uppåt");
            view.Find(".shopping-item-reorder-bar .shopping-move-down-btn").HasAttribute("disabled").Should().BeFalse();
        });

        // Tapping the handle again closes the reorder bar
        view.Find(".shopping-unchecked-list .shopping-drag-handle").Click();
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
        await view.InvokeAsync(() => view.FindAll(".shopping-unchecked-list .shopping-row-check-target").First().Click());
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
        // Run dragstart + drop atomically inside renderer to avoid event handler ID invalidation during re-render
        await view.InvokeAsync(() =>
        {
            var handle = view.FindAll(".shopping-drag-handle").ToList()[1]; // Vara 2
            handle.DragStart();
            view.FindAll(".shopping-unchecked-list .shopping-item-row").ToList()[0].Drop();
        });

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara 2", "Vara 1", "Vara 4", "Vara 3");
            view.FindAll(".shopping-item-reorder-bar").Should().BeEmpty("musdragning öppnar inte flyttmenyn automatiskt vid släpp");
        });

        // Re-query handle after DOM update from drop before emitting dragend
        view.FindAll(".shopping-drag-handle").ToList()[0].DragEnd();

        // 6. The FIRST deliberate click on the drag handle after drag immediately opens the menu
        // Find + click run atomically on the renderer so a re-render cannot invalidate the handler id in between.
        await view.InvokeAsync(() => view.FindAll(".shopping-drag-handle").ToList()[0].Click());
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

        await view.InvokeAsync(() => view.FindAll(".shopping-drag-handle").ToList()[0].Click());
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-item-reorder-bar")
            .Should().ContainSingle("första klicket efter avbruten dragning ska öppna menyn direkt");
        });
    }

    [Fact]
    public async Task UnifiedReorderMode_ActiveHighlightKeyboardGatingAndDragState_ShouldFollowContract()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        phoneA.WaitForAssertion(() => phoneA.Markup.Should().Contain("Inköpslistan är tom"));
        phoneB.WaitForAssertion(() => phoneB.Markup.Should().Contain("Inköpslistan är tom"));

        var count = 0;
        foreach (var itemText in new[] { "Vara A", "Vara B", "Vara C", "Vara D" })
        {
            count++;
            await phoneA.InvokeAsync(() => phoneA.Find("#shopping-manual-input").Input(itemText));
            await phoneA.InvokeAsync(() => phoneA.Find("#shopping-add-manual-btn").ClickAsync(new MouseEventArgs()));
            phoneA.WaitForAssertion(() =>
            {
                phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(count);
            });
        }

        // 1. Initially all handles are inactive (no .is-reorder-active, aria-expanded="false")
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-drag-handle.is-reorder-active").Should().BeEmpty();
            phoneA.FindAll(".shopping-drag-handle")
                .Select(h => h.GetAttribute("aria-expanded"))
                .Should().AllBe("false");
        });

        // 2. ArrowUp / ArrowDown with closed menu must NOT move anything or open the menu
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[1].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara A", "Vara B", "Vara C", "Vara D");
            phoneA.FindAll(".shopping-item-reorder-bar").Should().BeEmpty();
            phoneA.FindAll(".shopping-drag-handle.is-reorder-active").Should().BeEmpty();
        });

        // 3. Click handle on Vara A (index 0) -> activates .is-reorder-active and aria-expanded="true"
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].Click());
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-drag-handle").ToList()[0].ClassList.Should().Contain("is-reorder-active");
            phoneA.FindAll(".shopping-drag-handle").ToList()[0].GetAttribute("aria-expanded").Should().Be("true");
            phoneA.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
        });

        // 4. Click handle on Vara B (index 1) -> activates Vara B and deactivates Vara A
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[1].Click());
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-drag-handle").ToList()[0].ClassList.Should().NotContain("is-reorder-active");
            phoneA.FindAll(".shopping-drag-handle").ToList()[0].GetAttribute("aria-expanded").Should().Be("false");
            phoneA.FindAll(".shopping-drag-handle").ToList()[1].ClassList.Should().Contain("is-reorder-active");
            phoneA.FindAll(".shopping-drag-handle").ToList()[1].GetAttribute("aria-expanded").Should().Be("true");
            phoneA.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
        });

        // 5. Click handle on Vara B again -> closes menu and removes .is-reorder-active
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[1].Click());
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-drag-handle.is-reorder-active").Should().BeEmpty();
            phoneA.FindAll(".shopping-drag-handle").ToList()[1].GetAttribute("aria-expanded").Should().Be("false");
            phoneA.FindAll(".shopping-item-reorder-bar").Should().BeEmpty();
        });

        // 6. Open menu on Vara A (index 0) and perform 3 consecutive ArrowDown moves (0 -> 1 -> 2 -> 3)
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].Click());
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].ClassList.Should().Contain("is-reorder-active"));

        // Boundary check at top before moving down: ArrowUp at index 0 does nothing and keeps active state
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara A", "Vara B", "Vara C", "Vara D");
            phoneA.FindAll(".shopping-drag-handle").ToList()[0].ClassList.Should().Contain("is-reorder-active");
        });

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara B", "Vara A", "Vara C", "Vara D");
            phoneA.FindAll(".shopping-drag-handle").ToList()[1].ClassList.Should().Contain("is-reorder-active");
        });

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[1].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara B", "Vara C", "Vara A", "Vara D");
            phoneA.FindAll(".shopping-drag-handle").ToList()[2].ClassList.Should().Contain("is-reorder-active");
        });

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[2].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara B", "Vara C", "Vara D", "Vara A");
            phoneA.FindAll(".shopping-drag-handle").ToList()[3].ClassList.Should().Contain("is-reorder-active");
        });

        // Boundary check at bottom: ArrowDown at index 3 does nothing and keeps active state
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[3].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara B", "Vara C", "Vara D", "Vara A");
            phoneA.FindAll(".shopping-drag-handle").ToList()[3].ClassList.Should().Contain("is-reorder-active");
        });

        // 7. Three consecutive ArrowUp moves bring Vara A back to index 0
        for (int idx = 3; idx >= 1; idx--)
        {
            var currentIdx = idx;
            await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[currentIdx].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" }));
            var nextIdx = idx - 1;
            phoneA.WaitForAssertion(() =>
            {
                phoneA.FindAll(".shopping-drag-handle").ToList()[nextIdx].ClassList.Should().Contain("is-reorder-active");
            });
        }

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara A", "Vara B", "Vara C", "Vara D");
        });

        // 8. Escape closes the menu; subsequent ArrowDown does NOT move Vara A
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].KeyDownAsync(new KeyboardEventArgs { Key = "Escape" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-item-reorder-bar").Should().BeEmpty();
            phoneA.FindAll(".shopping-drag-handle.is-reorder-active").Should().BeEmpty();
        });

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara A", "Vara B", "Vara C", "Vara D");
        });

        // 9. Mouse drag with ACTIVE menu preserves active state on the moved item
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].Click());
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].ClassList.Should().Contain("is-reorder-active"));

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[0].DragStart());
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").ToList()[2].DropAsync(new DragEventArgs()));
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[2].DragEndAsync(new DragEventArgs()));

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara B", "Vara C", "Vara A", "Vara D");
            phoneA.FindAll(".shopping-drag-handle").ToList()[2].ClassList.Should().Contain("is-reorder-active");
            phoneA.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
        });

        // 10. Cancelled drag and same-item drop preserve order and active state on Vara A (index 2)
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[2].DragStart());
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-drag-handle").ToList()[2].DragEndAsync(new DragEventArgs()));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-drag-handle").ToList()[2].ClassList.Should().Contain("is-reorder-active");
            phoneA.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
        });

        var rows = phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").ToList();
        var varaAId = int.Parse(rows[2].GetAttribute("data-item-id")!);
        var varaDId = int.Parse(rows[3].GetAttribute("data-item-id")!);
        var varaBId = int.Parse(rows[0].GetAttribute("data-item-id")!);

        await phoneA.Instance.OnTouchDragStartAsync(varaAId);
        await phoneA.Instance.OnTouchDragCancelAsync();
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-drag-handle").ToList()[2].ClassList.Should().Contain("is-reorder-active");
        });

        // Touch drag with ACTIVE menu preserves active state on Vara A when moved to index 3
        await phoneA.Instance.OnTouchDragStartAsync(varaAId);
        await phoneA.Instance.OnTouchDropAsync(varaAId, varaDId);
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara B", "Vara C", "Vara D", "Vara A");
            phoneA.FindAll(".shopping-drag-handle").ToList()[3].ClassList.Should().Contain("is-reorder-active");
            phoneA.FindAll(".shopping-item-reorder-bar").Should().ContainSingle();
        });

        // 11. Version conflict during drag with active menu: clears stale active state and reloads server order
        await phoneA.Instance.OnTouchDragStartAsync(varaAId);

        // Phone B reorders Vara B down while Phone A is mid-drag
        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-drag-handle").Should().HaveCount(4));
        await phoneB.InvokeAsync(() => phoneB.FindAll(".shopping-drag-handle").ToList()[0].Click());
        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-item-reorder-bar .shopping-move-down-btn").Should().ContainSingle());
        await phoneB.InvokeAsync(() => phoneB.Find(".shopping-item-reorder-bar .shopping-move-down-btn").ClickAsync(new MouseEventArgs()));
        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara C", "Vara B", "Vara D", "Vara A");
        });

        await phoneA.Instance.OnTouchDropAsync(varaAId, varaBId);
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("Vara C", "Vara B", "Vara D", "Vara A");
            phoneA.Find(".shopping-list-feedback").TextContent.Should().Contain("Ordningen ändrades");
            phoneA.FindAll(".shopping-drag-handle.is-reorder-active").Should().BeEmpty();
            phoneA.FindAll(".shopping-item-reorder-bar").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task SimplifiedShoppingList_NoCheckboxes_RowToggleAndRedCrossDelete_ShouldWorkAndSync()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        phoneA.WaitForAssertion(() => phoneA.Markup.Should().Contain("Inköpslistan är tom"));
        phoneB.WaitForAssertion(() => phoneB.Markup.Should().Contain("Inköpslistan är tom"));

        var count = 0;
        foreach (var itemText in new[] { "100 g färskost", "2 dl gräddfil", "1 knippe dill" })
        {
            count++;
            await phoneA.InvokeAsync(() => phoneA.Find("#shopping-manual-input").Input(itemText));
            await phoneA.InvokeAsync(() => phoneA.Find("#shopping-add-manual-btn").ClickAsync(new MouseEventArgs()));
            phoneA.WaitForAssertion(() =>
            {
                phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(count);
            });
        }

        // 1. No visible checkbox is rendered in Att handla
        phoneA.FindAll(".shopping-unchecked-list input[type='checkbox']").Should().BeEmpty();
        phoneA.FindAll(".shopping-unchecked-list .shopping-checkbox").Should().BeEmpty();

        // 2. Drag handle and edit button do NOT toggle item status
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-drag-handle").ToList()[0].Click());
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(3);
            phoneA.FindAll(".shopping-checked-list").Should().BeEmpty();
        });
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-drag-handle").ToList()[0].Click());

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-edit-btn").ToList()[0].Click());
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-edit-panel").Should().ContainSingle();
            phoneA.FindAll(".shopping-checked-list").Should().BeEmpty();
        });
        await phoneA.InvokeAsync(() => phoneA.Find(".shopping-cancel-edit-btn").Click());

        // 3. Clicking row toggles item to Klart, and no visible checkbox is rendered in Klart
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-row-check-target").ToList()[0].ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            phoneA.FindAll(".shopping-checked-list input[type='checkbox']").Should().BeEmpty();
            phoneA.FindAll(".shopping-checked-list .shopping-checkbox").Should().BeEmpty();
            phoneA.Find(".shopping-checked-list .shopping-delete-btn").GetAttribute("aria-label").Should().Be("Ta bort 100 g färskost");
        });

        // 4. Clicking the red cross (✕) deletes only that item completely without restoring it to Att handla,
        // shows the Undo banner, and syncs deletion to Phone B
        await phoneA.InvokeAsync(() => phoneA.Find(".shopping-checked-list .shopping-delete-btn").ClickAsync(new MouseEventArgs()));

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-checked-list").Should().BeEmpty();
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.Find(".shopping-unchecked-list").TextContent.Should().NotContain("färskost");
            phoneA.Find(".shopping-undo-feedback").TextContent.Should().Contain("\"färskost\" borttagen.");
            phoneA.Find(".shopping-undo-feedback .rb-feedback-action-btn").TextContent.Trim().Should().Be("Ångra");
        });

        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-checked-list").Should().BeEmpty();
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneB.Find(".shopping-unchecked-list").TextContent.Should().NotContain("färskost");
        });

        // 5. Clicking Ångra restores the deleted item back under Klart (IsChecked = true) with full RawText, and syncs to Phone B
        await phoneA.InvokeAsync(() => phoneA.Find(".shopping-undo-feedback .rb-feedback-action-btn").ClickAsync(new MouseEventArgs()));

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-undo-feedback").Should().BeEmpty();
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            phoneA.Find(".shopping-checked-list .shopping-checked-text").TextContent.Should().Be("100 g färskost");
        });

        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneB.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            phoneB.Find(".shopping-checked-list .shopping-checked-text").TextContent.Should().Be("100 g färskost");
        });
    }

    [Fact]
    public async Task DeleteFromChecked_UndoTargetReplacementSameNameAndConflict_ShouldFollowContract()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        var count = 0;
        foreach (var itemText in new[] { "100 g färskost", "2 dl gräddfil", "1 knippe dill" })
        {
            count++;
            await phoneA.InvokeAsync(() => phoneA.Find("#shopping-manual-input").Input(itemText));
            await phoneA.InvokeAsync(() => phoneA.Find("#shopping-add-manual-btn").ClickAsync(new MouseEventArgs()));
            phoneA.WaitForAssertion(() =>
            {
                phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(count);
            });
        }

        // 1. Check off "100 g färskost" and "2 dl gräddfil"
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-row-check-target").ToList()[0].ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle());

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-row-check-target").ToList()[0].ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.Find(".shopping-undo-feedback").TextContent.Should().Contain("\"gräddfil\" markerad som klar.");
        });

        // 2. Within 4 seconds, delete "100 g färskost" with red cross (✕) -> replaces the check-off Undo target!
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-checked-list .shopping-delete-btn").ToList()[0].ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            phoneA.Find(".shopping-undo-feedback").TextContent.Should().Contain("\"färskost\" borttagen.");
        });

        // Before clicking Ångra on Phone A, Phone B adds a new "250 g färskost" to Att handla
        await phoneB.InvokeAsync(() => phoneB.Find("#shopping-manual-input").Input("250 g färskost"));
        await phoneB.InvokeAsync(() => phoneB.Find("#shopping-add-manual-btn").ClickAsync(new MouseEventArgs()));

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
        });

        // 3. Clicking Ångra on Phone A restores "100 g färskost" under Klart (NOT unchecking gräddfil!)
        // and preserves Phone B's "250 g färskost" in Att handla
        await phoneA.InvokeAsync(() => phoneA.Find(".shopping-undo-feedback .rb-feedback-action-btn").ClickAsync(new MouseEventArgs()));

        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneA.FindAll(".shopping-checked-list .shopping-checked-text")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("100 g färskost", "2 dl gräddfil");
        });

        phoneB.WaitForAssertion(() =>
        {
            phoneB.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(2);
            phoneB.FindAll(".shopping-checked-list .shopping-checked-text")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("100 g färskost", "2 dl gräddfil");
        });

        // 4. Conversely: delete "100 g färskost" under Klart, then immediately check off "1 knippe dill" under Att handla ->
        // clicking Ångra undoes the check-off of "dill" (latest action), leaving "100 g färskost" deleted!
        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-checked-list .shopping-delete-btn").ToList()[0].ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() => phoneA.Find(".shopping-undo-feedback").TextContent.Should().Contain("\"färskost\" borttagen."));

        await phoneA.InvokeAsync(() => phoneA.FindAll(".shopping-unchecked-list .shopping-row-check-target").ToList()[0].ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() => phoneA.Find(".shopping-undo-feedback").TextContent.Should().Contain("\"dill\" markerad som klar."));

        await phoneA.InvokeAsync(() => phoneA.Find(".shopping-undo-feedback .rb-feedback-action-btn").ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() =>
        {
            phoneA.FindAll(".shopping-unchecked-list .shopping-item-name")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("dill", "färskost");
            phoneA.FindAll(".shopping-checked-list .shopping-checked-text")
                .Select(x => x.TextContent.Trim())
                .Should().Equal("2 dl gräddfil");
        });
    }

    [Fact]
    public async Task ShoppingPlaceholder_I3_25_ShouldRenderAndEditCommentsAndIntervalsInUncheckedAndCheckedLists()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctx = CreateSessionContext(dbFactory, sharedNotifier);
        var recipeService = ctx.Services.GetRequiredService<RecipeService>();
        var shoppingService = ctx.Services.GetRequiredService<ShoppingListService>();

        var recipe = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Blåbärsdessert",
            Servings = 4,
            Ingredients =
            [
                new Ingredient { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "blåbär", RawText = "2 dl blåbär" },
                new Ingredient { OrderIndex = 2, Name = "blåbär till garnering", RawText = "blåbär till garnering" },
                new Ingredient { OrderIndex = 3, Quantity = 3m, Unit = "dl", Name = "grädde", RawText = "3 dl grädde" },
                new Ingredient { OrderIndex = 4, Name = "vispad grädde till garnering", RawText = "vispad grädde till garnering" },
                new Ingredient { OrderIndex = 5, Quantity = 1m, QuantityMax = 2m, Unit = "tsk", Name = "spiskummin", RawText = "1–2 tsk spiskummin" }
            ]
        });

        await shoppingService.AddRecipeIngredientsAsync(recipe.Id, targetServings: 4);

        var view = ctx.RenderComponent<ShoppingPlaceholder>();

        // 1. Verify rendering in "Att handla": discreet comment lines and interval quantity
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-row").Should().HaveCount(3);
            var comments = view.FindAll(".shopping-unchecked-list .shopping-item-comment")
                .Select(x => x.TextContent.Trim())
                .ToList();
            comments.Should().Equal(
                "Extra blåbär till garnering, mängd ej angiven",
                "Extra vispad grädde till garnering, mängd ej angiven");

            var texts = view.FindAll(".shopping-unchecked-list .shopping-item-text")
                .Select(x => x.TextContent.Trim())
                .ToList();
            texts.Should().Equal("2 dl blåbär", "3 dl grädde", "1–2 tsk spiskummin");
        });

        // 2. Editing an item without changing the comment preserves the existing comment
        await view.InvokeAsync(() => view.FindAll(".shopping-unchecked-list .shopping-edit-btn").First().Click());
        view.WaitForAssertion(() =>
        {
            view.Find(".shopping-edit-comment").GetAttribute("value")
                .Should().Be("Extra blåbär till garnering, mängd ej angiven");
        });

        await view.InvokeAsync(() => view.Find(".shopping-edit-qty").Change("4"));
        await view.InvokeAsync(() => view.Find(".shopping-save-edit-btn").Click());

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-text").First().TextContent.Trim()
                .Should().Be("4 dl blåbär");
            view.FindAll(".shopping-unchecked-list .shopping-item-comment").First().TextContent.Trim()
                .Should().Be("Extra blåbär till garnering, mängd ej angiven");
        });

        // 3. Editing and updating the comment in the edit panel updates it cleanly
        await view.InvokeAsync(() => view.FindAll(".shopping-unchecked-list .shopping-edit-btn").First().Click());
        await view.InvokeAsync(() => view.Find(".shopping-edit-comment").Change("Extra till garnering"));
        await view.InvokeAsync(() => view.Find(".shopping-save-edit-btn").Click());

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-unchecked-list .shopping-item-comment").First().TextContent.Trim()
                .Should().Be("Extra till garnering");
        });

        // 4. Checking off the item moves it to "Klart" and renders both struck-through text and discreet comment
        await view.InvokeAsync(() => view.FindAll(".shopping-unchecked-list .shopping-row-check-target").First().ClickAsync(new MouseEventArgs()));

        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            view.Find(".shopping-checked-list .shopping-checked-text").TextContent.Trim().Should().Be("4 dl blåbär");
            view.Find(".shopping-checked-list .shopping-checked-comment").TextContent.Trim().Should().Be("Extra till garnering");
            view.FindAll(".shopping-checked-list .shopping-delete-btn").Should().ContainSingle();
        });

        // 5. Deleting from "Klart" and clicking "Ångra" restores the item with its comment intact
        await view.InvokeAsync(() => view.Find(".shopping-checked-list .shopping-delete-btn").ClickAsync(new MouseEventArgs()));
        view.WaitForAssertion(() => view.FindAll(".shopping-checked-list").Should().BeEmpty());

        await view.InvokeAsync(() => view.Find(".shopping-undo-feedback .rb-feedback-action-btn").ClickAsync(new MouseEventArgs()));
        view.WaitForAssertion(() =>
        {
            view.FindAll(".shopping-checked-list .shopping-item-row").Should().ContainSingle();
            view.Find(".shopping-checked-list .shopping-checked-comment").TextContent.Trim().Should().Be("Extra till garnering");
        });
    }

    [Fact]
    public async Task DisposeAsync_WhenJsDisconnectedInShoppingMode_UnsubscribesAndDisposesDotNetRefWithoutThrowing()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctxA = CreateSessionContext(dbFactory, sharedNotifier);
        using var ctxB = CreateSessionContext(dbFactory, sharedNotifier);

        var phoneA = ctxA.RenderComponent<ShoppingPlaceholder>();
        var phoneB = ctxB.RenderComponent<ShoppingPlaceholder>();

        phoneB.WaitForAssertion(() =>
            ctxB.JSInterop.Invocations["receptbanken.registerShoppingSyncListeners"].Should().ContainSingle());
        var dotNetRef = (DotNetObjectReference<ShoppingPlaceholder>)ctxB.JSInterop
            .Invocations["receptbanken.registerShoppingSyncListeners"]
            .Single()
            .Arguments[0]!;

        // Enter shopping mode so DisposeAsync also attempts releaseWakeLock
        await phoneB.InvokeAsync(() => phoneB.Find("#shopping-enter-mode-btn").ClickAsync(new MouseEventArgs()));
        phoneB.WaitForAssertion(() => phoneB.FindAll(".shopping-mode-exit-btn").Should().ContainSingle());

        ctxB.JSInterop.SetupVoid("receptbanken.releaseWakeLock")
            .SetException(new JSDisconnectedException("Circuit disconnected"));
        ctxB.JSInterop.SetupVoid("receptbanken.unregisterShoppingSyncListeners")
            .SetException(new JSDisconnectedException("Circuit disconnected"));

        var act = async () => await phoneB.Instance.DisposeAsync();
        await act.Should().NotThrowAsync();

        // Both JS cleanup calls were attempted despite releaseWakeLock failing
        ctxB.JSInterop.Invocations["receptbanken.releaseWakeLock"].Should().ContainSingle();
        ctxB.JSInterop.Invocations["receptbanken.unregisterShoppingSyncListeners"].Should().ContainSingle();

        // DotNetObjectReference was disposed in finally
        var accessDisposedRef = () => _ = dotNetRef.Value;
        accessDisposedRef.Should().Throw<ObjectDisposedException>();

        // ShoppingListService.OnChanged was unsubscribed synchronously even though JS cleanup failed
        await phoneA.InvokeAsync(() => phoneA.Find("#shopping-manual-input").Input("1 kg mjöl"));
        await phoneA.InvokeAsync(() => phoneA.Find("#shopping-add-manual-btn").ClickAsync(new MouseEventArgs()));
        phoneA.WaitForAssertion(() => phoneA.FindAll(".shopping-unchecked-list .shopping-item-row").Should().ContainSingle());
    }

    [Fact]
    public async Task DisposeAsync_NormalCleanup_CompletesWithoutThrowing()
    {
        using var dbFactory = new TestDbContextFactory();
        var sharedNotifier = new ShoppingListNotifier();

        using var ctx = CreateSessionContext(dbFactory, sharedNotifier);
        var cut = ctx.RenderComponent<ShoppingPlaceholder>();

        cut.WaitForAssertion(() =>
            ctx.JSInterop.Invocations["receptbanken.registerShoppingSyncListeners"].Should().ContainSingle());
        var dotNetRef = (DotNetObjectReference<ShoppingPlaceholder>)ctx.JSInterop
            .Invocations["receptbanken.registerShoppingSyncListeners"]
            .Single()
            .Arguments[0]!;

        var act = async () => await cut.Instance.DisposeAsync();
        await act.Should().NotThrowAsync();

        ctx.JSInterop.Invocations["receptbanken.unregisterShoppingSyncListeners"].Should().ContainSingle();
        var accessDisposedRef = () => _ = dotNetRef.Value;
        accessDisposedRef.Should().Throw<ObjectDisposedException>();
    }
}
