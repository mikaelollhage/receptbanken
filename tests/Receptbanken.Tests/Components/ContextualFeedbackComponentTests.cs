using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Importers;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Common;
using Receptbanken.Web.Components.Layout;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class ContextualFeedbackComponentTests : TestContext
{
    /// <summary>
    /// Deterministic controllable <see cref="TimeProvider"/> for testing 4-second auto-dismiss timers
    /// without real wall-clock delays.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = new();

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
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

            public ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
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

            public void Dispose()
            {
                Disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FixedLocalDateProvider : ILocalDateProvider
    {
        private DateOnly _today;
        public FixedLocalDateProvider(DateOnly today) => _today = today;
        public DateOnly GetLocalToday() => _today;
        public void SetClientLocalDate(DateOnly clientDate) => _today = clientDate;
    }

    private sealed class FakeBrowserFile : IBrowserFile
    {
        private readonly byte[] _bytes;
        public FakeBrowserFile(string name, string contentType, byte[] bytes)
        {
            Name = name;
            ContentType = contentType;
            _bytes = bytes;
            Size = bytes.Length;
        }

        public string Name { get; }
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size { get; }
        public string ContentType { get; }

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize)
                throw new IOException("Filen överskrider maxstorlek.");
            return new MemoryStream(_bytes);
        }
    }

    private sealed class FakeBackupService : IBackupService
    {
        public byte[] ArchiveToReturn { get; set; } = [0x50, 0x4B, 0x03, 0x04];
        public BackupRestoreResult RestoreResultToReturn { get; set; } = new()
        {
            Success = true,
            RestoredRecipes = 5,
            RestoredCategories = 3,
            RestoredImages = 2
        };

        public Task<byte[]> CreateBackupArchiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ArchiveToReturn);

        public Task<BackupRestoreResult> RestoreFromArchiveAsync(Stream zipStream, bool replaceExisting = true, CancellationToken cancellationToken = default)
            => Task.FromResult(RestoreResultToReturn);
    }

    // =========================================================================
    // DEL H6: Livscykel och auto-dismiss (4 sekunder)
    // =========================================================================

    [Fact]
    public void ContextualFeedback_Success_ShouldAutoDismissAfter4Seconds()
    {
        var timeProvider = new ManualTimeProvider();
        Services.AddSingleton<TimeProvider>(timeProvider);

        var state = new FeedbackState();
        state.ShowSuccess("Receptet har sparats!");

        var cut = RenderComponent<ContextualFeedback>(p => p.Add(x => x.State, state));

        cut.Find(".rb-feedback-success").GetAttribute("role").Should().Be("status");
        cut.Find(".rb-feedback-message").TextContent.Should().Contain("Receptet har sparats!");

        // After 3.9 seconds, still visible
        timeProvider.Advance(TimeSpan.FromMilliseconds(3900));
        cut.FindAll(".rb-feedback-success").Should().ContainSingle();

        // At 4.0 seconds, automatically dismissed and state cleared
        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        cut.WaitForAssertion(() =>
        {
            state.HasMessage.Should().BeFalse();
            cut.FindAll(".rb-feedback").Should().BeEmpty();
        });
    }

    [Fact]
    public void ContextualFeedback_WhenNewSuccessReplacesPrevious_OldTimerMustNotClearNewMessage()
    {
        var timeProvider = new ManualTimeProvider();
        Services.AddSingleton<TimeProvider>(timeProvider);

        var state = new FeedbackState();
        state.ShowSuccess("Första bekräftelsen");

        var cut = RenderComponent<ContextualFeedback>(p => p.Add(x => x.State, state));
        cut.Find(".rb-feedback-message").TextContent.Should().Contain("Första bekräftelsen");

        // Advance 2.5 seconds (first timer has 1.5s left)
        timeProvider.Advance(TimeSpan.FromMilliseconds(2500));

        // Trigger second confirmation
        state.ShowSuccess("Andra bekräftelsen");
        cut.SetParametersAndRender(p => p.Add(x => x.State, state));
        cut.Find(".rb-feedback-message").TextContent.Should().Contain("Andra bekräftelsen");

        // Advance 2.0 seconds (4.5s total since first, but only 2.0s since second)
        timeProvider.Advance(TimeSpan.FromMilliseconds(2000));
        state.HasMessage.Should().BeTrue();
        cut.Find(".rb-feedback-message").TextContent.Should().Contain("Andra bekräftelsen");

        // Advance another 2.1 seconds (4.1s since second confirmation) -> now dismissed
        timeProvider.Advance(TimeSpan.FromMilliseconds(2100));
        cut.WaitForAssertion(() =>
        {
            state.HasMessage.Should().BeFalse();
            cut.FindAll(".rb-feedback").Should().BeEmpty();
        });
    }

    [Fact]
    public void ContextualFeedback_WarningAndError_ShouldNeverAutoDismiss_ButCanBeClosedManually()
    {
        var timeProvider = new ManualTimeProvider();
        Services.AddSingleton<TimeProvider>(timeProvider);

        var state = new FeedbackState();
        state.ShowWarning("En varning som kräver uppmärksamhet");

        var cut = RenderComponent<ContextualFeedback>(p => p.Add(x => x.State, state));
        cut.Find(".rb-feedback-warning").GetAttribute("role").Should().Be("status");

        // Advance 10 seconds -> warning remains
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        state.HasMessage.Should().BeTrue();
        cut.FindAll(".rb-feedback-warning").Should().ContainSingle();

        // Switch to error
        state.ShowError("Ett blockerande fel uppstod");
        cut.SetParametersAndRender(p => p.Add(x => x.State, state));
        cut.Find(".rb-feedback-error").GetAttribute("role").Should().Be("alert");

        // Advance 10 seconds -> error remains
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        state.HasMessage.Should().BeTrue();
        cut.FindAll(".rb-feedback-error").Should().ContainSingle();

        // Manual close button dismisses it
        cut.Find(".rb-feedback-close").Click();
        state.HasMessage.Should().BeFalse();
        cut.FindAll(".rb-feedback").Should().BeEmpty();
    }

    [Fact]
    public void MainLayout_WhenNavigating_ShouldClearAnyResidualGlobalNotification()
    {
        Services.AddFluentUIComponents();
        var notifService = new AppNotificationService();
        Services.AddSingleton(notifService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<MainLayout>();
        cut.InvokeAsync(() => notifService.ShowWarning("Varning", "Testmeddelande"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".app-top-banner").Should().ContainSingle();
        });

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/veckomeny");

        cut.WaitForAssertion(() =>
        {
            notifService.HasNotification.Should().BeFalse();
            cut.FindAll(".app-top-banner").Should().BeEmpty();
        });
    }

    // =========================================================================
    // DEL H1: Veckomenyn (enkel bekräftelse, kontextuell placering, dagkort)
    // =========================================================================

    [Fact]
    public async Task WeeklyMenuPage_ConfirmAndSave_ShouldShowSingleContextualConfirmation_NearClickedButton_AndNoGlobalBanner()
    {
        var localToday = new DateOnly(2026, 11, 2);
        using var dbFactory = new TestDbContextFactory();
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
        var notifService = new AppNotificationService();

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;
        var festId = cats.First(c => c.Name == "Fest & Helg").Id;

        for (int i = 1; i <= 5; i++)
            await recipeService.CreateRecipeAsync(new Recipe { Title = $"Vardag {i}", Servings = 4 }, [vardagId]);
        for (int i = 1; i <= 2; i++)
            await recipeService.CreateRecipeAsync(new Recipe { Title = $"Helg {i}", Servings = 4 }, [festId]);

        Services.AddFluentUIComponents();
        Services.AddSingleton(notifService);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(new FixedLocalDateProvider(localToday));
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(pantryMatcher);
        Services.AddSingleton(generator);
        Services.AddSingleton(weeklyMenuService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        // Generate a full week menu first so all 7 days have recipes
        cut.Find("button.generate-week-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-recipe-title").Should().HaveCount(7));

        // 1. Click top "Spika veckomenyn"
        cut.FindAll("button.confirm-week-btn").First().Click();

        cut.WaitForAssertion(() =>
        {
            // Exactly ONE confirmation banner on the page (top), and NO global AppNotificationService banner
            cut.FindAll(".weekly-feedback-banner").Should().ContainSingle();
            cut.Find(".weekly-feedback-banner").ClassList.Should().NotContain("weekly-feedback-bottom");
            cut.Find(".weekly-feedback-banner").TextContent.Should().Contain("är nu spikad!");
            notifService.HasNotification.Should().BeFalse();
        });

        // 2. Reroll Monday -> updates Monday card directly WITHOUT creating a top confirmation banner
        cut.FindAll("button.weekly-day-reroll-btn").First().Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-feedback-banner").Should().BeEmpty();
            notifService.HasNotification.Should().BeFalse();
        });

        // 3. Click bottom "Spara ändringar" -> shows confirmation at the bottom save bar
        cut.Find("button.confirm-week-bottom-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-feedback-banner").Should().ContainSingle();
            var bottomBanner = cut.Find(".weekly-feedback-banner.weekly-feedback-bottom");
            bottomBanner.TextContent.Should().Contain("Ändringarna i veckomenyn för vecka 45 har sparats!");
            notifService.HasNotification.Should().BeFalse();
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_RerollWithoutAlternative_ShouldShowDayCardMessageOnly_NotTopBanner()
    {
        var localToday = new DateOnly(2026, 11, 2);
        using var dbFactory = new TestDbContextFactory();
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

        var cats = await categoryService.GetCategoriesAsync();
        var vardagId = cats.First(c => c.Name == "Vardagsmat").Id;

        // Only 1 recipe exists -> rerolling Monday has no alternative recipe
        await recipeService.CreateRecipeAsync(new Recipe { Title = "Enda rätten", Servings = 4 }, [vardagId]);

        Services.AddFluentUIComponents();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(new FixedLocalDateProvider(localToday));
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(pantryMatcher);
        Services.AddSingleton(generator);
        Services.AddSingleton(weeklyMenuService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        // Generate the week menu first (Monday gets "Enda rätten", other days have no recipes)
        cut.Find("button.generate-week-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Should().Contain("Enda rätten");
        });

        // Close the initial generation summary warning so we can verify reroll doesn't create a top banner
        cut.Find(".weekly-feedback-banner .weekly-feedback-close").Click();
        cut.FindAll(".weekly-feedback-banner").Should().BeEmpty();

        // Reroll Monday (day 0)
        cut.Find(".weekly-day-card[data-day-index='0'] button.weekly-day-reroll-btn").Click();

        cut.WaitForAssertion(() =>
        {
            // Shown on Monday's card with role="status", and NO top feedback banner
            var dayStatus = cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-status-message");
            dayStatus.GetAttribute("role").Should().Be("status");
            dayStatus.TextContent.Should().Contain("inga andra lediga recept som matchar");
            cut.FindAll(".weekly-feedback-banner").Should().BeEmpty();
        });
    }

    // =========================================================================
    // DEL H2 & H3: Receptimport och Receptredigering (validering & felhantering)
    // =========================================================================

    [Fact]
    public void ImportPlaceholder_JsonLdError_ShouldClearAutomaticallyWhenInputChanges()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var parser = new SwedishIngredientParser();
        var importer = new WebRecipeImporter(
            new HttpClient(),
            new JsonLdRecipeExtractor(parser),
            new HtmlFallbackExtractor(parser),
            imageStorage,
            NullLogger<WebRecipeImporter>.Instance);

        Services.AddFluentUIComponents();
        Services.AddSingleton<IRecipeImporter>(importer);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<ImportPlaceholder>();

        cut.Find("#jsonld-toggle").Click();
        cut.Find("#jsonld-input").Input("{ invalid json ");
        cut.Find("#parse-jsonld-btn").Click();

        cut.FindAll(".jsonld-error").Should().ContainSingle();

        // Changing the JSON-LD input clears the error automatically
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        cut.FindAll(".jsonld-error").Should().BeEmpty();
    }

    [Fact]
    public void RecipeEdit_WhenTitleEmptyOrServingsZero_ShouldShowContextualValidationErrors_AndClearWhenFixed()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);

        Services.AddFluentUIComponents();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<RecipeEdit>();
        cut.WaitForAssertion(() => cut.FindAll("#edit-title").Should().ContainSingle());

        // Set servings to 0 (empty) while title is also empty, and click bottom Save button
        cut.Find("#edit-servings").Input("");
        var saveButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Spara recept")).ToList();
        saveButtons.Last().Click();

        cut.WaitForAssertion(() =>
        {
            // Both inline field errors are shown
            var fieldErrors = cut.FindAll(".field-validation-error");
            fieldErrors.Should().HaveCount(2);
            fieldErrors.ElementAt(0).TextContent.Should().Contain("Ange en titel för receptet.");
            fieldErrors.ElementAt(1).TextContent.Should().Contain("Antal portioner måste vara minst 1.");

            // Contextual error banner is shown at the bottom save bar
            var bottomBanner = cut.Find(".recipe-save-feedback-bottom.rb-feedback-error");
            bottomBanner.GetAttribute("role").Should().Be("alert");
            bottomBanner.TextContent.Should().Contain("Ange en titel för receptet.");
            bottomBanner.TextContent.Should().Contain("Antal portioner måste vara minst 1.");
        });

        // Fix title and servings -> validation errors clear automatically
        cut.Find("#edit-title").Input("Pannkakor");
        cut.Find("#edit-servings").Input("4");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".field-validation-error").Should().BeEmpty();
            cut.FindAll(".recipe-save-feedback").Should().BeEmpty();
        });
    }

    // =========================================================================
    // DEL H4: Receptdetalj (inköpslista, duplicering, radering)
    // =========================================================================

    [Fact]
    public async Task RecipeDetail_ShoppingListDuplicateAndDelete_ShouldShowSingleContextualFeedback()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var servingCalc = new ServingCalculator();
        var shoppingListService = new ShoppingListService(dbFactory, new ShoppingListAggregator(servingCalc), new SwedishIngredientParser());

        var recipe = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Lasagne",
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients =
            [
                new Ingredient { Name = "Köttfärs", Quantity = 500, Unit = "g", RawText = "500 g Köttfärs" },
                new Ingredient { Name = "Lasagneplattor", Quantity = 12, Unit = "st", RawText = "12 st Lasagneplattor" }
            ],
            Instructions = [new InstructionStep { StepNumber = 1, InstructionText = "Grädda i ugn." }]
        });

        Services.AddFluentUIComponents();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(shoppingListService);
        Services.AddSingleton(servingCalc);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<RecipeDetail>(p => p.Add(x => x.Id, recipe.Id));

        // 1. Click "Lägg till i inköpslista" -> contextual confirmation directly below button
        var addShoppingBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Lägg till i inköpslista"));
        addShoppingBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var feedback = cut.Find(".recipe-shopping-feedback.rb-feedback-success");
            feedback.GetAttribute("role").Should().Be("status");
            feedback.TextContent.Should().Contain("2 ingredienser lades till i inköpslistan.");
        });

        // 2. Click "Duplicera" -> navigates to ?duplicated=true
        var nav = Services.GetRequiredService<NavigationManager>();
        var duplicateBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Duplicera"));
        duplicateBtn.Click();

        cut.WaitForAssertion(() =>
        {
            nav.Uri.Should().Contain("?duplicated=true");
        });

        // 3. Delete recipe -> navigates to /recept?deleted=true and RecipeListPage renders contextual confirmation
        var deleteBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Radera"));
        deleteBtn.Click();
        cut.Find(".modal-dialog-custom button.btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
        {
            nav.Uri.Should().EndWith("/recept?deleted=true");
        });

        var listCut = RenderComponent<RecipeListPage>();
        listCut.WaitForAssertion(() =>
        {
            var banner = listCut.Find(".home-feedback.rb-feedback-success");
            banner.TextContent.Should().Contain("Receptet har raderats.");
        });
    }

    // =========================================================================
    // DEL H5: Kategorier, inköpslista och säkerhetskopiering
    // =========================================================================

    [Fact]
    public async Task Categories_DuplicateName_ShouldShowContextualErrorInForm_AndClearOnInput()
    {
        using var dbFactory = new TestDbContextFactory();
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        await categoryService.CreateCategoryAsync(new Category { Name = "Soppa", ColorHex = "#e07a5f" });

        Services.AddFluentUIComponents();
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<Categories>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Soppa");
            var editBtn = cut.Find("button.btn-secondary-custom.btn-sm");
            editBtn.TextContent.Should().Contain("Redigera");
            var deleteBtn = cut.Find("button.btn-danger-custom.btn-sm");
            deleteBtn.TextContent.Should().Contain("Radera");
        });

        // Enter duplicate name "soppa"
        var textField = cut.Find("fluent-text-field");
        textField.Input("soppa");

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Lägg till kategori"));
        saveBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find(".category-form-feedback.rb-feedback-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain("finns redan");
        });

        // Typing a new name clears the error automatically
        textField.Input("Fisk & Skaldjur");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".category-form-feedback").Should().BeEmpty();
        });

        // Saving the valid category shows a contextual confirmation in the form
        saveBtn.Click();
        cut.WaitForAssertion(() =>
        {
            var success = cut.Find(".category-form-feedback.rb-feedback-success");
            success.TextContent.Should().Contain("Kategorin 'Fisk & Skaldjur' har skapats.");
        });
    }

    [Fact]
    public void BackupPlaceholder_RestoreAndExport_ShouldShowSingleContextualFeedbackPerCard()
    {
        var fakeBackup = new FakeBackupService();
        Services.AddFluentUIComponents();
        Services.AddSingleton<IBackupService>(fakeBackup);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<BackupPlaceholder>();

        // 1. Export backup -> shows contextual confirmation in export card
        var exportBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Ladda ner säkerhetskopia"));
        exportBtn.Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".backup-export-feedback.rb-feedback-success").Should().ContainSingle();
            cut.Find(".backup-export-feedback").TextContent.Should().Contain("Säkerhetskopian har skapats och laddats ner.");
        });

        // 2. Select backup file and restore -> shows exactly ONE contextual confirmation in restore card
        var inputFile = cut.FindComponent<InputFile>();
        var fakeZip = new FakeBrowserFile("backup.zip", "application/zip", [0x50, 0x4B, 0x03, 0x04]);
        cut.InvokeAsync(() => inputFile.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([fakeZip])));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Starta återställning"));
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Starta återställning")).Click();
        cut.Find(".modal-dialog-custom button.btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".backup-restore-feedback.rb-feedback-success").Should().ContainSingle();
            cut.Find(".backup-restore-feedback").TextContent.Should().Contain("Återställningen lyckades! 5 recept, 3 kategorier och 2 bilder återställdes.");
        });
    }

    [Fact]
    public async Task RecipeEdit_ServingsValidation_ShouldDefaultTo1_RejectZeroAndNegativeWithoutAutoClamping_AndNotSaveInvalidRecipe()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);

        Services.AddFluentUIComponents();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<RecipeEdit>();
        cut.WaitForAssertion(() => cut.FindAll("#edit-servings").Should().ContainSingle());

        // 1. Verify default value is 1 for new recipe and no min="1" attribute is present on fluent-number-field
        var servingsField = cut.Find("#edit-servings");
        servingsField.GetAttribute("min").Should().BeNull("min='1' i web-komponenten tvingade tidigare 0 till 1 i webbläsaren");
        servingsField.GetAttribute("value").Should().Be("1");

        cut.Find("#edit-title").Input("Testgryta");

        // 2. Enter 0 portions -> must retain "0" in the field, not clamp to 1, and fail validation on save
        cut.Find("#edit-servings").Change("0");
        cut.Find("#edit-servings").GetAttribute("value").Should().Be("0");

        cut.FindAll("button").First(b => b.TextContent.Contains("Spara recept")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".field-validation-error").TextContent.Should().Be("Antal portioner måste vara minst 1.");
            cut.Find(".recipe-save-feedback.rb-feedback-error").TextContent.Should().Contain("Antal portioner måste vara minst 1.");
        });
        (await recipeService.GetRecipesAsync()).Should().BeEmpty();

        // 3. Enter negative portions (-3) -> must retain "-3" and fail validation on save
        cut.Find("#edit-servings").Change("-3");
        cut.Find("#edit-servings").GetAttribute("value").Should().Be("-3");

        cut.FindAll("button").First(b => b.TextContent.Contains("Spara recept")).Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".field-validation-error").TextContent.Should().Be("Antal portioner måste vara minst 1.");
        });
        (await recipeService.GetRecipesAsync()).Should().BeEmpty();

        // 4. Correct to 1 portion -> validation error disappears automatically and recipe can be saved
        cut.Find("#edit-servings").Change("1");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".field-validation-error").Should().BeEmpty();
            cut.FindAll(".recipe-save-feedback").Should().BeEmpty();
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Spara recept")).Click();
        var saved = await recipeService.GetRecipesAsync();
        saved.Should().ContainSingle(r => r.Title == "Testgryta" && r.Servings == 1);
    }

    [Fact]
    public async Task RecipeEdit_EditingExistingRecipe_AndImportPreview_ShouldRejectInvalidServings()
    {
        using var dbFactory = new TestDbContextFactory();
        await using (var db = dbFactory.CreateDbContext())
        {
            await DbInitializer.InitializeAsync(db, NullLogger.Instance);
        }

        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var parser = new SwedishIngredientParser();
        var importer = new WebRecipeImporter(
            new HttpClient(),
            new JsonLdRecipeExtractor(parser),
            new HtmlFallbackExtractor(parser),
            imageStorage,
            NullLogger<WebRecipeImporter>.Instance);

        var existing = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Befintlig Soppa",
            Servings = 4,
            ServingsUnit = "portioner",
            Instructions = [new InstructionStep { StepNumber = 1, InstructionText = "Koka." }]
        });

        Services.AddFluentUIComponents();
        Services.AddSingleton<IRecipeImporter>(importer);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        // 1. Editing existing recipe: setting 0 servings must fail validation and keep DB value at 4
        var editCut = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, existing.Id));
        editCut.WaitForAssertion(() => editCut.Find("#edit-servings").GetAttribute("value").Should().Be("4"));

        editCut.Find("#edit-servings").Change("0");
        editCut.FindAll("button").First(b => b.TextContent.Contains("Spara ändringar")).Click();

        editCut.WaitForAssertion(() =>
        {
            editCut.Find(".field-validation-error").TextContent.Should().Be("Antal portioner måste vara minst 1.");
        });
        (await recipeService.GetRecipeByIdAsync(existing.Id))!.Servings.Should().Be(4);

        // 2. Import preview: setting 0 servings must fail validation and prevent saving
        var importCut = RenderComponent<ImportPlaceholder>();
        importCut.Find("#jsonld-toggle").Click();
        importCut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        importCut.Find("#parse-jsonld-btn").Click();

        importCut.WaitForAssertion(() => importCut.FindAll("#import-servings").Should().ContainSingle());
        importCut.Find("#import-servings").Change("0");
        importCut.FindAll("button").First(b => b.TextContent.Contains("Godkänn & Spara recept")).Click();

        importCut.WaitForAssertion(() =>
        {
            importCut.Find(".field-validation-error").TextContent.Should().Be("Antal portioner måste vara minst 1.");
            importCut.Find(".import-save-feedback.rb-feedback-error").TextContent.Should().Contain("Antal portioner måste vara minst 1.");
        });

        // 3. Direct service-level guard
        var actCreate = () => recipeService.CreateRecipeAsync(new Recipe { Title = "Ogiltig", Servings = 0 });
        await actCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("Antal portioner måste vara minst 1.");

        existing.Servings = -1;
        var actUpdate = () => recipeService.UpdateRecipeAsync(existing);
        await actUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("Antal portioner måste vara minst 1.");
    }

    [Fact]
    public async Task WeeklyMenuPage_NavigationAndTodayHighlight_ShouldKeepStableButtonPositions_AndHighlightOnlyToday()
    {
        // Onsdag 4 november 2026 (Vecka 45, index 2 = onsdag)
        var localToday = new DateOnly(2026, 11, 4);
        using var dbFactory = new TestDbContextFactory();
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

        Services.AddFluentUIComponents();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(new FixedLocalDateProvider(localToday));
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(pantryMatcher);
        Services.AddSingleton(generator);
        Services.AddSingleton(weeklyMenuService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Vecka 45, 2026"));

        // H4: On current week (Week 45), ONLY Wednesday (day-index 2) has .is-today and "IDAG" badge
        var todayCards = cut.FindAll(".weekly-day-card.is-today");
        todayCards.Should().ContainSingle();
        todayCards.Single().GetAttribute("data-day-index").Should().Be("2");
        todayCards.Single().QuerySelector(".weekly-day-today-badge")!.TextContent.Trim().Should().Be("IDAG");
        cut.FindAll(".weekly-day-today-badge").Should().ContainSingle();

        // H3: On current week, .weekly-nav-primary-row has exactly [week-prev-btn, week-next-btn], and no .weekly-nav-today-row
        var primaryButtons = cut.FindAll(".weekly-nav-primary-row > button");
        primaryButtons.Should().HaveCount(2);
        primaryButtons.ElementAt(0).ClassList.Should().Contain("week-prev-btn");
        primaryButtons.ElementAt(1).ClassList.Should().Contain("week-next-btn");
        cut.FindAll(".weekly-nav-today-row").Should().BeEmpty();

        // Click "Nästa vecka" 5 times in a row (Week 45 -> 46 -> 47 -> 48 -> 49 -> 50)
        for (int expectedWeek = 46; expectedWeek <= 50; expectedWeek++)
        {
            cut.Find(".weekly-nav-primary-row button.week-next-btn").Click();
            var weekNum = expectedWeek;
            cut.WaitForAssertion(() =>
            {
                cut.Markup.Should().Contain($"Vecka {weekNum}, 2026");

                // Primary row STILL has [week-prev-btn, week-next-btn] in the exact same order
                var navBtns = cut.FindAll(".weekly-nav-primary-row > button");
                navBtns.Should().HaveCount(2);
                navBtns.ElementAt(0).ClassList.Should().Contain("week-prev-btn");
                navBtns.ElementAt(1).ClassList.Should().Contain("week-next-btn");

                // "Denna vecka" is rendered in its separate row (.weekly-nav-today-row)
                cut.FindAll(".weekly-nav-today-row button.week-today-btn").Should().ContainSingle();

                // H4: No day card is marked as today when viewing another week
                cut.FindAll(".weekly-day-card.is-today").Should().BeEmpty();
                cut.FindAll(".weekly-day-today-badge").Should().BeEmpty();
            });
        }

        // Click "Föregående vecka" -> goes back from Week 50 to Week 49
        cut.Find(".weekly-nav-primary-row button.week-prev-btn").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Vecka 49, 2026"));

        // Click "Denna vecka" -> returns to Week 45 and restores today's highlight on Wednesday
        cut.Find(".weekly-nav-today-row button.week-today-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Vecka 45, 2026");
            cut.FindAll(".weekly-nav-today-row").Should().BeEmpty();
            cut.FindAll(".weekly-day-card.is-today").Should().ContainSingle();
        });
    }

    [Fact]
    public void BackupPlaceholder_ShouldRenderSinglePrimaryDownloadButton_WithoutDuplicateDirectLink()
    {
        var fakeBackup = new FakeBackupService();
        Services.AddFluentUIComponents();
        Services.AddSingleton<IBackupService>(fakeBackup);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<BackupPlaceholder>();

        cut.FindAll("button").Where(b => b.TextContent.Contains("Ladda ner säkerhetskopia (.zip)")).Should().ContainSingle();
        cut.FindAll("a[href='/api/backup/export']").Should().BeEmpty();
        cut.Markup.Should().NotContain("Direktlänk (.zip)");
    }
}

