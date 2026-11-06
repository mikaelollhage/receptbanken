using System.Net;
using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Layout;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class ImportPlaceholderComponentTests : TestContext
{
    private (IRenderedComponent<ImportPlaceholder> Cut, RecipeService RecipeService) RenderImportPage(HttpMessageHandler? httpHandler = null)
    {
        var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var parser = new SwedishIngredientParser();
        var importer = new WebRecipeImporter(
            new HttpClient(httpHandler ?? new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new JsonLdRecipeExtractor(parser),
            new HtmlFallbackExtractor(parser),
            imageStorage,
            NullLogger<WebRecipeImporter>.Instance);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<IRecipeImporter>(importer);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (RenderComponent<ImportPlaceholder>(), recipeService);
    }

    // ---------- Part 2: URL button reactivity ----------

    [Fact]
    public void FetchRecipeButton_ShouldReactImmediatelyToInputEvent_WithoutRequiringBlurOrEnter()
    {
        var (cut, _) = RenderImportPage();

        // 1. Initial empty field => button disabled
        var fetchBtn = () => cut.Find("#fetch-recipe-btn");
        var urlInput = () => cut.Find("#import-url-input");
        fetchBtn().HasAttribute("disabled").Should().BeTrue();

        // 2. Whitespace-only input => button remains disabled
        urlInput().Input("    ");
        fetchBtn().HasAttribute("disabled").Should().BeTrue();

        // 3. Pasted URL via 'input' event (Ctrl+V / context menu paste) => enabled immediately without 'change'/blur
        urlInput().Input(IcaRecipeFixtures.PageUrl);
        fetchBtn().HasAttribute("disabled").Should().BeFalse();

        // 4. Clearing the field => disabled immediately
        urlInput().Input("");
        fetchBtn().HasAttribute("disabled").Should().BeTrue();

        // 5. Any non-whitespace text enables the button immediately (validation happens on submit)
        urlInput().Input("delvis-inskriven-text");
        fetchBtn().HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task FetchRecipeButton_ShouldDisableWhileImporting_AndRestoreStateAfterwards()
    {
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new TestHttpMessageHandler((_, _) => tcs.Task);
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input(IcaRecipeFixtures.PageUrl);
        cut.Find("#fetch-recipe-btn").HasAttribute("disabled").Should().BeFalse();

        // Trigger click; import stays in-flight until tcs completes
        var clickTask = cut.InvokeAsync(() => cut.Find("#fetch-recipe-btn").Click());
        cut.Find("#fetch-recipe-btn").HasAttribute("disabled").Should().BeTrue();

        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        await clickTask;

        cut.WaitForAssertion(() =>
        {
            cut.Find("#fetch-recipe-btn").HasAttribute("disabled").Should().BeFalse();
        });
    }

    // ---------- Part 1 & 3: JSON-LD section UI & auto-expansion ----------

    [Fact]
    public void JsonLdSection_ShouldBeCollapsedByDefault_AndAllowManualToggle()
    {
        var (cut, _) = RenderImportPage();

        cut.FindAll("#jsonld-panel").Should().BeEmpty();
        cut.Find("#jsonld-toggle").GetAttribute("aria-expanded").Should().Be("false");

        // Open manually without attempting URL import first
        cut.Find("#jsonld-toggle").Click();
        cut.FindAll("#jsonld-panel").Should().ContainSingle();
        cut.Find("#jsonld-toggle").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("#parse-jsonld-btn").HasAttribute("disabled").Should().BeTrue();

        // Pasting into JSON-LD textarea enables "Tolka receptdata" immediately on 'input'
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.RecipeJson);
        cut.Find("#parse-jsonld-btn").HasAttribute("disabled").Should().BeFalse();

        // Close manually
        cut.Find("#jsonld-toggle").Click();
        cut.FindAll("#jsonld-panel").Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void UrlImportHttpFailures_ShouldExpandJsonLdSectionOnlyWhenAppropriate(HttpStatusCode statusCode, bool expectedExpanded)
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(IcaRecipeFixtures.Akamai403Body, Encoding.UTF8, "text/html")
        });
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input(IcaRecipeFixtures.PageUrl);
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".import-error-box").Should().ContainSingle();
            (cut.FindAll("#jsonld-panel").Count > 0).Should().Be(expectedExpanded);
        });
    }

    [Fact]
    public void UrlImport_WhenHtmlIsBlockPageOrHasNoRecipe_ShouldAutoExpandJsonLdSection()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><head><title>Vanlig sida</title></head><body>Inget recept</body></html>", Encoding.UTF8, "text/html")
        });
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input("https://www.example.se/sida");
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#jsonld-panel").Should().ContainSingle();
        });
    }

    [Fact]
    public void UrlImport_WhenUrlIsInvalid_ShouldKeepJsonLdSectionCollapsed()
    {
        var (cut, _) = RenderImportPage();

        cut.Find("#import-url-input").Input("inte-en-giltig-url");
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".import-error-box").Should().ContainSingle();
            cut.FindAll("#jsonld-panel").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task PastingJsonLdAfterBlockedUrl_ShouldOpenExistingPreview_PreserveSourceUrl_AndNotSaveAutomatically()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(IcaRecipeFixtures.Akamai403Body, Encoding.UTF8, "text/html")
        });
        var (cut, recipeService) = RenderImportPage(handler);

        // 1. User attempts URL import -> fails with 403 -> JSON-LD section opens automatically
        cut.Find("#import-url-input").Input(IcaRecipeFixtures.PageUrl);
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#jsonld-panel").Should().ContainSingle();
        });

        // 2. User pastes ICA JSON-LD array from browser console and clicks "Tolka receptdata"
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        cut.Find("#parse-jsonld-btn").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#parse-jsonld-btn").Click();

        // 3. Existing preview renders with all 18 ingredients, 8 steps, and preserved source URL
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Förhandsgranskning — Väntar på ditt godkännande");
            cut.Markup.Should().Contain("Marry me chicken");
            cut.Markup.Should().Contain($"Tolkade ingredienser ({IcaRecipeFixtures.IngredientCount})");
            cut.Markup.Should().Contain($"Tolkade instruktionssteg ({IcaRecipeFixtures.StepCount})");
            cut.FindAll(".ingredient-editor-row").Should().HaveCount(IcaRecipeFixtures.IngredientCount);
            cut.FindAll(".recipe-meta-fields").Should().ContainSingle();
            cut.FindAll(".recipe-meta-row").Should().HaveCount(4);
            cut.FindAll(".recipe-meta-suffix").Select(e => e.TextContent.Trim()).Should().Equal("min", "min");
        });

        // 4. Verify nothing was saved automatically to the database
        (await recipeService.GetRecipesAsync()).Should().BeEmpty();
    }

    // ---------- Del A: JavaScript snippet copy button tests ----------

    [Fact]
    public void CopyButton_InitialState_ShouldShowKopiera_AndHaveNoErrors()
    {
        var (cut, _) = RenderImportPage();

        // Open JSON-LD section to access help
        cut.Find("#jsonld-toggle").Click();

        var copyBtn = cut.Find("#copy-jsonld-snippet-btn");
        copyBtn.TextContent.Should().Contain("Kopiera");
        copyBtn.TextContent.Should().NotContain("Kopierat");
        cut.FindAll(".jsonld-copy-error").Should().BeEmpty();

        // Code in pre block matches constant
        cut.Find("#jsonld-help-code code").TextContent.Trim()
            .Should().Be(ImportPlaceholder.JsonLdConsoleSnippet.Trim());
    }

    [Fact]
    public void CopyButton_WhenClickedAndSuccessful_ShouldCopyExactSnippet_ShowKopierat_AndRevertAfterTwoSeconds()
    {
        var (cut, _) = RenderImportPage();
        cut.Find("#jsonld-toggle").Click();

        JSInterop.Setup<bool>("receptbanken.copyToClipboard", ImportPlaceholder.JsonLdConsoleSnippet, "jsonld-help-code")
            .SetResult(true);

        var copyBtn = cut.Find("#copy-jsonld-snippet-btn");
        copyBtn.Click();

        // Verified invocation with exact snippet
        var invocation = JSInterop.VerifyInvoke("receptbanken.copyToClipboard");
        invocation.Arguments[0].Should().Be(ImportPlaceholder.JsonLdConsoleSnippet);
        invocation.Arguments[1].Should().Be("jsonld-help-code");

        // Immediately shows copied state
        copyBtn = cut.Find("#copy-jsonld-snippet-btn");
        copyBtn.TextContent.Should().Contain("Kopierat");
        copyBtn.ClassList.Should().Contain("copied");
        cut.FindAll(".jsonld-copy-error").Should().BeEmpty();

        // Reverts back after ~2 seconds
        cut.WaitForAssertion(() =>
        {
            var btn = cut.Find("#copy-jsonld-snippet-btn");
            btn.TextContent.Should().Contain("Kopiera");
            btn.TextContent.Should().NotContain("Kopierat");
            btn.ClassList.Should().NotContain("copied");
        }, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CopyButton_WhenCopyFails_ShouldNotShowKopierat_AndDisplayHelpfulErrorMessage()
    {
        var (cut, _) = RenderImportPage();
        cut.Find("#jsonld-toggle").Click();

        JSInterop.Setup<bool>("receptbanken.copyToClipboard", ImportPlaceholder.JsonLdConsoleSnippet, "jsonld-help-code")
            .SetResult(false);

        var copyBtn = cut.Find("#copy-jsonld-snippet-btn");
        copyBtn.Click();

        // Should NOT display "Kopierat"
        copyBtn = cut.Find("#copy-jsonld-snippet-btn");
        copyBtn.TextContent.Should().Contain("Kopiera");
        copyBtn.TextContent.Should().NotContain("Kopierat");
        copyBtn.ClassList.Should().NotContain("copied");

        // Should display helpful error message explaining manual fallback
        var error = cut.Find(".jsonld-copy-error");
        error.TextContent.Should().Contain("Kunde inte kopiera automatiskt");
        error.TextContent.Should().Contain("Ctrl+C");
    }

    [Fact]
    public void CopyButton_WhenJsThrows_ShouldHandleGracefullyAndDisplayErrorMessage()
    {
        var (cut, _) = RenderImportPage();
        cut.Find("#jsonld-toggle").Click();

        JSInterop.Setup<bool>("receptbanken.copyToClipboard", ImportPlaceholder.JsonLdConsoleSnippet, "jsonld-help-code")
            .SetException(new InvalidOperationException("Clipboard permission denied"));

        var copyBtn = cut.Find("#copy-jsonld-snippet-btn");
        copyBtn.Click();

        copyBtn = cut.Find("#copy-jsonld-snippet-btn");
        copyBtn.TextContent.Should().NotContain("Kopierat");

        var error = cut.Find(".jsonld-copy-error");
        error.TextContent.Should().Contain("Kunde inte kopiera automatiskt");
    }

    // ---------- Del B: Improved error workflow & alternatives tests ----------

    [Fact]
    public void UrlImport_WhenBlocked_ShowsNonTechnicalError_RecommendsJsonLd_AndProvidesManualOption()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(IcaRecipeFixtures.Akamai403Body, Encoding.UTF8, "text/html")
        });
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input(IcaRecipeFixtures.PageUrl);
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".import-error-box").Should().NotBeNull();
            cut.Find(".import-error-title").TextContent.Trim().Should().Be("Det gick inte att hämta receptet automatiskt");
            cut.Find(".import-error-desc").TextContent.Trim().Should().Be("Webbplatsen blockerar automatisk hämtning.");
            cut.Find(".import-error-hint").TextContent.Should().Contain("Du kan fortfarande importera det genom att kopiera recepttexten från webbsidan och klistra in den här.");

            // Primary recommended action: Paste recipe text button
            var textImportBtn = cut.Find("#btn-open-textimport");
            textImportBtn.TextContent.Should().Contain("Klistra in recept");

            // Secondary options: JSON-LD and Manual input
            cut.Find(".import-alt-label").TextContent.Trim().Should().Be("Alternativt:");
            var jsonLdBtn = cut.Find("#btn-open-jsonld");
            jsonLdBtn.TextContent.Should().Contain("Importera receptdata (JSON-LD)");
            var manualBtn = cut.Find("#btn-manual-input");
            manualBtn.TextContent.Should().Contain("Fyll i receptet manuellt");
        });
    }

    [Fact]
    public void UrlImport_WhenBlocked_ClickingJsonLdButton_FocusesJsonLdInput()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(IcaRecipeFixtures.Akamai403Body, Encoding.UTF8, "text/html")
        });
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input(IcaRecipeFixtures.PageUrl);
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#btn-open-jsonld").Click();
            JSInterop.VerifyInvoke("receptbanken.focusElement");
            cut.FindAll("#jsonld-panel").Should().ContainSingle();
        });
    }

    [Fact]
    public void UrlImport_WhenBlocked_ClickingManualButton_CreatesManualDraftWithPreservedSourceUrl()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(IcaRecipeFixtures.Akamai403Body, Encoding.UTF8, "text/html")
        });
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input(IcaRecipeFixtures.PageUrl);
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() => cut.FindAll(".import-error-box").Count.Should().Be(1));

        // Click "Fyll i receptet manuellt"
        cut.Find("#btn-manual-input").Click();

        // Error box disappears, draft preview appears
        cut.FindAll(".import-error-box").Should().BeEmpty();
        cut.Markup.Should().Contain("Förhandsgranskning — Väntar på ditt godkännande");

        // Source URL is preserved
        cut.Markup.Should().Contain(IcaRecipeFixtures.PageUrl);
    }

    [Fact]
    public void UrlImport_WhenNotFound_DoesNotRecommendJsonLd_OffersManualInput()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var (cut, _) = RenderImportPage(handler);

        cut.Find("#import-url-input").Input("https://www.example.com/saknas");
        cut.Find("#fetch-recipe-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".import-error-box").Should().NotBeNull();
            cut.Find(".import-error-title").TextContent.Trim().Should().Be("Det gick inte att hämta receptet automatiskt");
            cut.Find(".import-error-desc").TextContent.Trim().Should().Be("Receptsidan kunde inte hittas. Kontrollera att webbadressen är korrekt.");

            // Should NOT offer JSON-LD button when 404
            cut.FindAll("#btn-open-jsonld").Should().BeEmpty();

            // Offers manual input
            cut.Find("#btn-manual-input").TextContent.Should().Contain("Fyll i receptet manuellt");
        });
    }

    // ---------- Image import & preview component tests ----------

    [Fact]
    public void ParseJsonLd_WithValidImage_ShouldDownloadImage_AndDisplayInPreviewHero()
    {
        var validPng = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG magic
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52  // IHDR chunk
        };

        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains(".jpg") || req.RequestUri.ToString().Contains(".png"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(validPng)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (cut, _) = RenderImportPage(handler);

        // Open JSON-LD section and parse
        cut.Find("#jsonld-toggle").Click();
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        cut.Find("#parse-jsonld-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Förhandsgranskning — Väntar på ditt godkännande");
            // Hero image is rendered because image was downloaded
            var heroImg = cut.Find(".recipe-detail-hero img");
            heroImg.GetAttribute("src").Should().StartWith("/images/recipes/");

            // Receptbild section is also rendered
            cut.FindAll(".image-upload-grid").Should().ContainSingle();
            cut.FindAll(".image-preview-box").Should().ContainSingle();
        });
    }

    [Fact]
    public void ParseJsonLd_WhenImageDownloadFails_ShouldDisplayPlaceholder_AndAllowSavingRecipe()
    {
        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains(".jpg"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (cut, recipeService) = RenderImportPage(handler);

        cut.Find("#jsonld-toggle").Click();
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        cut.Find("#parse-jsonld-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Förhandsgranskning — Väntar på ditt godkännande");
            // Placeholder icon is shown because image was not downloaded
            cut.FindAll(".recipe-card-placeholder").Should().NotBeEmpty();
            cut.Find(".recipe-card-placeholder").TextContent.Should().Contain("🍲");
            // Informative notice is displayed contextually in the Receptbild section
            cut.FindAll(".import-image-notice").Should().ContainSingle();
            cut.Find(".import-image-notice").TextContent.Should().Contain("Receptet innehöll en bildadress, men bilden kunde inte hämtas");

            // No duplicate warning is sent to the global AppNotificationService banner
            var notificationService = Services.GetRequiredService<AppNotificationService>();
            notificationService.HasNotification.Should().BeFalse();
        });
    }

    [Fact]
    public void RemoveImageButton_WhenClicked_ShouldRemoveImageAndShowPlaceholder()
    {
        var validPng = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
        };

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(validPng)
        });

        var (cut, _) = RenderImportPage(handler);

        cut.Find("#jsonld-toggle").Click();
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        cut.Find("#parse-jsonld-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".image-preview-box button").Should().ContainSingle();
        });

        // Click "Ta bort bild"
        cut.Find(".image-preview-box button").Click();

        cut.WaitForAssertion(() =>
        {
            // Now hero shows placeholder
            cut.Find(".recipe-card-placeholder").TextContent.Should().Contain("🍲");
            cut.FindAll(".image-preview-box").Should().BeEmpty();
        });
    }

    [Fact]
    public void ApproveAndSave_ShouldNavigateWithSavedQueryParameter()
    {
        var (cut, _) = RenderImportPage();
        var nav = Services.GetRequiredService<NavigationManager>();

        cut.Find("#jsonld-toggle").Click();
        cut.Find("#jsonld-input").Input(IcaRecipeFixtures.ConsoleArrayJson);
        cut.Find("#parse-jsonld-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".btn-primary-custom").Should().NotBeEmpty();
        });

        // Click "Godkänn & Spara recept"
        var saveBtns = cut.FindAll("button").Where(b => b.TextContent.Contains("Godkänn & Spara")).ToList();
        saveBtns.Should().NotBeEmpty();
        saveBtns.First().Click();

        cut.WaitForAssertion(() =>
        {
            nav.Uri.Should().Contain("?saved=true");
        });
    }

    [Fact]
    public async Task RecipeDetail_WhenSavedQueryParamIsTrue_ShouldDisplayContextualSuccessBanner_WithoutGlobalTopBanner()
    {
        var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var servingCalc = new ServingCalculator();
        var shoppingListService = new ShoppingListService(dbFactory, new ShoppingListAggregator(servingCalc), new SwedishIngredientParser());
        var notifService = new AppNotificationService();

        var recipe = await recipeService.CreateRecipeAsync(new Receptbanken.Core.Models.Recipe
        {
            Title = "Sparad kyckling",
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients = [new Receptbanken.Core.Models.Ingredient { Name = "Kyckling", RawText = "Kyckling" }],
            Instructions = [new Receptbanken.Core.Models.InstructionStep { InstructionText = "Stek.", StepNumber = 1 }]
        });

        Services.AddFluentUIComponents();
        Services.AddSingleton(recipeService);
        Services.AddSingleton(shoppingListService);
        Services.AddSingleton(servingCalc);
        Services.AddSingleton(notifService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/recept/{recipe.Id}?saved=true");

        var cut = RenderComponent<RecipeDetail>(parameters => parameters
            .Add(p => p.Id, recipe.Id));

        cut.WaitForAssertion(() =>
        {
            // Global top banner is NOT used (single contextual confirmation on RecipeDetail)
            notifService.HasNotification.Should().BeFalse();

            var banner = cut.Find(".recipe-detail-feedback.rb-feedback-success");
            banner.GetAttribute("role").Should().Be("status");
            banner.TextContent.Should().Contain("Receptet har sparats!");
            banner.TextContent.Should().Contain("Sparad kyckling");
        });

        // Dismiss the contextual banner manually
        cut.Find(".recipe-detail-feedback .rb-feedback-close").Click();
        cut.FindAll(".recipe-detail-feedback").Should().BeEmpty();
    }

    [Fact]
    public void MainLayout_WhenNotificationActive_ShouldRenderBannerAboveHeader_AndDismissOnClick()
    {
        Services.AddFluentUIComponents();
        var notifService = new AppNotificationService();
        Services.AddSingleton(notifService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<MainLayout>();

        // Initially no banner
        cut.FindAll(".app-top-banner").Should().BeEmpty();

        // 1. Show warning
        cut.InvokeAsync(() => notifService.ShowWarning("Receptbilden kunde inte importeras", "Kunde inte hämtas från webbplatsen."));
        cut.WaitForAssertion(() =>
        {
            var banner = cut.Find(".app-top-banner");
            banner.ClassList.Should().Contain("app-top-banner-warning");
            banner.TextContent.Should().Contain("Receptbilden kunde inte importeras");
            banner.TextContent.Should().Contain("Kunde inte hämtas från webbplatsen.");
        });

        // Click close
        cut.Find(".app-top-banner-close").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".app-top-banner").Should().BeEmpty();
            notifService.HasNotification.Should().BeFalse();
        });

        // 2. Show success
        cut.InvokeAsync(() => notifService.ShowSuccess("Receptet har sparats!", "Receptet är sparat och redo."));
        cut.WaitForAssertion(() =>
        {
            var banner = cut.Find(".app-top-banner");
            banner.ClassList.Should().Contain("app-top-banner-success");
            banner.TextContent.Should().Contain("Receptet har sparats!");
            banner.TextContent.Should().Contain("Receptet är sparat och redo.");
        });
    }
}

