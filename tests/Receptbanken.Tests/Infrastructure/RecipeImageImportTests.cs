using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Importers;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Infrastructure.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Receptbanken.Tests.Infrastructure;

public class RecipeImageImportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalImageStorageService _imageStorage;

    public RecipeImageImportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"receptbanken_img_import_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var options = Options.Create(new ImageStorageOptions
        {
            BaseDataPath = _tempDir,
            ImageFolderName = "images/recipes"
        });

        _imageStorage = new LocalImageStorageService(options, NullLogger<LocalImageStorageService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private static JsonLdRecipeExtractor CreateExtractor()
    {
        return new JsonLdRecipeExtractor(new SwedishIngredientParser());
    }

    private WebRecipeImporter CreateImporter(HttpMessageHandler handler, IImageStorageService? storage = null)
    {
        var parser = new SwedishIngredientParser();
        var jsonLd = new JsonLdRecipeExtractor(parser);
        var fallback = new HtmlFallbackExtractor(parser);
        var httpClient = new HttpClient(handler);
        return new WebRecipeImporter(
            httpClient,
            jsonLd,
            fallback,
            storage ?? _imageStorage,
            NullLogger<WebRecipeImporter>.Instance);
    }

    private static byte[] CreateValidPngBytes(int width = 50, int height = 50)
    {
        using var ms = new MemoryStream();
        using (var img = new Image<Rgba32>(width, height))
        {
            img.SaveAsPng(ms);
        }
        return ms.ToArray();
    }

    // ==========================================
    // 1. JSON-LD med bild-URL
    // ==========================================
    [Fact]
    public void ExtractFromJson_SimpleStringUrl_ShouldExtractRemoteImageUrl()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Kycklingpasta",
              "image": "https://assets.example.com/images/kyckling.jpg",
              "recipeIngredient": ["400g pasta", "300g kyckling"],
              "recipeInstructions": ["Koka pasta.", "Stek kyckling."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().Be("https://assets.example.com/images/kyckling.jpg");
    }

    // ==========================================
    // 2. JSON-LD med bildarray
    // ==========================================
    [Fact]
    public void ExtractFromJson_ImageArray_ShouldSelectFirstValidUrlDeterministically()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Kycklingpasta",
              "image": [
                "https://assets.example.com/images/large.jpg",
                "https://assets.example.com/images/medium.jpg",
                "https://assets.example.com/images/small.jpg"
              ],
              "recipeIngredient": ["400g pasta"],
              "recipeInstructions": ["Koka pasta."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().Be("https://assets.example.com/images/large.jpg");
    }

    [Fact]
    public void ExtractFromJson_ImageArray_WhenFirstIsInvalid_ShouldSelectFirstValidUrl()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Kycklingpasta",
              "image": [
                "not-a-valid-url",
                "ftp://assets.example.com/pic.jpg",
                "http://127.0.0.1/private.jpg",
                "https://assets.example.com/images/valid.jpg"
              ],
              "recipeIngredient": ["400g pasta"],
              "recipeInstructions": ["Koka pasta."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().Be("https://assets.example.com/images/valid.jpg");
    }

    // ==========================================
    // 3. JSON-LD med ImageObject
    // ==========================================
    [Fact]
    public void ExtractFromJson_ImageObjectWithUrl_ShouldExtractRemoteImageUrl()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Kycklingpasta",
              "image": {
                "@type": "ImageObject",
                "url": "https://assets.example.com/images/kyckling-obj.jpg"
              },
              "recipeIngredient": ["400g pasta"],
              "recipeInstructions": ["Koka pasta."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().Be("https://assets.example.com/images/kyckling-obj.jpg");
    }

    [Fact]
    public void ExtractFromJson_ImageObjectWithContentUrl_ShouldExtractRemoteImageUrl()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Kycklingpasta",
              "image": {
                "@type": "ImageObject",
                "contentUrl": "https://assets.example.com/images/kyckling-content.jpg"
              },
              "recipeIngredient": ["400g pasta"],
              "recipeInstructions": ["Koka pasta."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().Be("https://assets.example.com/images/kyckling-content.jpg");
    }

    [Fact]
    public void ExtractFromJson_ArrayOfImageObjects_ShouldSelectFirstValidUrlDeterministically()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Kycklingpasta",
              "image": [
                {
                  "@type": "ImageObject",
                  "contentUrl": "https://assets.example.com/images/first.jpg"
                },
                {
                  "@type": "ImageObject",
                  "url": "https://assets.example.com/images/second.jpg"
                }
              ],
              "recipeIngredient": ["400g pasta"],
              "recipeInstructions": ["Koka pasta."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().Be("https://assets.example.com/images/first.jpg");
    }

    // ==========================================
    // 4. JSON-LD utan bild
    // ==========================================
    [Fact]
    public void ExtractFromJson_WithoutImageProperty_ShouldReturnNullRemoteImageUrl()
    {
        const string json = """
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Kycklingpasta",
              "recipeIngredient": ["400g pasta"],
              "recipeInstructions": ["Koka pasta."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().BeNull();
    }

    // ==========================================
    // 5. Ogiltig bildadress
    // ==========================================
    [Theory]
    [InlineData("ftp://assets.example.com/pic.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("http://localhost/pic.jpg")]
    [InlineData("http://127.0.0.1/pic.jpg")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("inte-en-url")]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractFromJson_InvalidOrUnsafeImageAddress_ShouldBeIgnored(string invalidUrl)
    {
        var json = $$"""
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Testrecept",
              "image": "{{invalidUrl}}",
              "recipeIngredient": ["1 ägg"],
              "recipeInstructions": ["Stek."]
            }
            """;

        var extractor = CreateExtractor();
        var result = extractor.ExtractFromJson(json);

        result.Status.Should().Be(JsonLdExtractionStatus.Success);
        result.RemoteImageUrl.Should().BeNull();
    }

    // ==========================================
    // 6. Bildserver som svarar med fel
    // ==========================================
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DownloadAndSaveImageAsync_WhenServerReturnsError_ShouldReturnNullAndNotThrow(HttpStatusCode errorStatus)
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(errorStatus));
        var importer = CreateImporter(handler);

        var savedPath = await importer.DownloadAndSaveImageAsync("https://assets.example.com/recipe.jpg");

        savedPath.Should().BeNull();
    }

    // ==========================================
    // 7. För stor bild
    // ==========================================
    [Fact]
    public async Task DownloadAndSaveImageAsync_WhenImageExceedsMaxSize_ShouldReturnNullAndNotThrow()
    {
        // 11 MB payload of valid PNG header followed by null bytes
        var pngHeader = CreateValidPngBytes();
        var largeContent = new byte[11 * 1024 * 1024];
        Array.Copy(pngHeader, largeContent, pngHeader.Length);

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(largeContent)
        });
        var importer = CreateImporter(handler);

        var savedPath = await importer.DownloadAndSaveImageAsync("https://assets.example.com/huge.png");

        savedPath.Should().BeNull();
    }

    // ==========================================
    // 8. Felaktigt filformat
    // ==========================================
    [Fact]
    public async Task DownloadAndSaveImageAsync_WhenServerReturnsHtmlInsteadOfImage_ShouldReturnNullAndNotThrow()
    {
        var htmlContent = "<html><body>Not an image</body></html>";
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(htmlContent, Encoding.UTF8, "text/html")
        });
        var importer = CreateImporter(handler);

        var savedPath = await importer.DownloadAndSaveImageAsync("https://assets.example.com/recipe.jpg");

        savedPath.Should().BeNull();
    }

    // ==========================================
    // 9. Säker lagring
    // ==========================================
    [Fact]
    public async Task DownloadAndSaveImageAsync_WhenValidImage_ShouldSaveAsWebpWithGuidFilename()
    {
        var pngBytes = CreateValidPngBytes(60, 60);
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pngBytes)
        });
        var importer = CreateImporter(handler);

        var savedPath = await importer.DownloadAndSaveImageAsync("https://assets.example.com/image.png");

        savedPath.Should().NotBeNull();
        savedPath!.Should().StartWith("images/recipes/");
        savedPath.Should().EndWith(".webp");

        var diskPath = Path.Combine(_tempDir, savedPath!);
        File.Exists(diskPath).Should().BeTrue();

        using var loaded = await Image.LoadAsync(diskPath);
        loaded.Width.Should().Be(60);
        loaded.Height.Should().Be(60);
    }

    // ==========================================
    // 10. Bildkoppling efter sparande
    // ==========================================
    [Fact]
    public async Task RecipeService_CreateRecipeWithImagePath_ShouldPersistAndRetrieveImagePath()
    {
        using var dbFactory = new TestDbContextFactory();
        var service = new RecipeService(dbFactory, _imageStorage, NullLogger<RecipeService>.Instance);

        var recipe = new Recipe
        {
            Title = "Sparat recept med bild",
            ImagePath = "images/recipes/saved-chicken.webp",
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients = [new Ingredient { Name = "Kyckling", RawText = "Kyckling" }],
            Instructions = [new InstructionStep { InstructionText = "Stek kyckling.", StepNumber = 1 }]
        };

        var created = await service.CreateRecipeAsync(recipe);
        created.Id.Should().BeGreaterThan(0);

        var fetched = await service.GetRecipeByIdAsync(created.Id);
        fetched.Should().NotBeNull();
        fetched!.ImagePath.Should().Be("images/recipes/saved-chicken.webp");
    }

    // ==========================================
    // 11. Recept som sparas trots misslyckad bildhämtning
    // ==========================================
    [Fact]
    public async Task RecipeImport_WhenImageFails_RecipeSavesSuccessfullyWithNullImagePath()
    {
        // 1. Importer attempts URL import, image download fails (500)
        var recipeHtml = $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <title>Test | Recept</title>
              <script type="application/ld+json">
              {{IcaRecipeFixtures.RecipeJson}}
              </script>
            </head>
            <body></body>
            </html>
            """;

        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains(".jpg"))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(recipeHtml, Encoding.UTF8, "text/html")
            };
        });

        var importer = CreateImporter(handler);
        var importResult = await importer.ImportFromUrlAsync("https://www.example.com/recept");

        importResult.Success.Should().BeTrue();
        importResult.DraftRecipe.Should().NotBeNull();
        importResult.DraftRecipe!.ImagePath.Should().BeNull(); // Failed image did not crash import

        // 2. Save recipe to database
        using var dbFactory = new TestDbContextFactory();
        var service = new RecipeService(dbFactory, _imageStorage, NullLogger<RecipeService>.Instance);
        var saved = await service.CreateRecipeAsync(importResult.DraftRecipe);

        saved.Id.Should().BeGreaterThan(0);
        saved.ImagePath.Should().BeNull();

        var retrieved = await service.GetRecipeByIdAsync(saved.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Title.Should().Be("Marry me chicken");
        retrieved.ImagePath.Should().BeNull();
    }

    // ==========================================
    // 12. Befintliga recept utan bilder
    // ==========================================
    [Fact]
    public async Task ExistingRecipe_WithoutImage_CanBeReadUpdatedAndQueriedWithoutErrors()
    {
        using var dbFactory = new TestDbContextFactory();
        var service = new RecipeService(dbFactory, _imageStorage, NullLogger<RecipeService>.Instance);

        // Create recipe without image
        var initial = new Recipe
        {
            Title = "Klassisk soppa",
            ImagePath = null,
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients = [new Ingredient { Name = "Morötter", RawText = "Morötter" }],
            Instructions = [new InstructionStep { InstructionText = "Koka.", StepNumber = 1 }]
        };

        var created = await service.CreateRecipeAsync(initial);

        // Query all recipes
        var all = await service.GetRecipesAsync();
        all.Should().Contain(r => r.Id == created.Id && r.ImagePath == null);

        // Update the recipe: add an image later
        var toUpdate = await service.GetRecipeByIdAsync(created.Id);
        toUpdate.Should().NotBeNull();
        toUpdate!.ImagePath = "images/recipes/added-later.webp";
        toUpdate.Title = "Klassisk soppa uppdaterad";

        var updated = await service.UpdateRecipeAsync(toUpdate);
        updated.ImagePath.Should().Be("images/recipes/added-later.webp");

        var retrieved = await service.GetRecipeByIdAsync(created.Id);
        retrieved!.ImagePath.Should().Be("images/recipes/added-later.webp");
        retrieved.Title.Should().Be("Klassisk soppa uppdaterad");
    }
}
