using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Backup;
using Receptbanken.Infrastructure.Importers;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Infrastructure.Storage;
using Receptbanken.Tests.Infrastructure;

namespace Receptbanken.Tests.Integration;

public class WebAppIntegrationTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly TestDbContextFactory _dbFactory;

    public WebAppIntegrationTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), $"rb_e2e_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDataPath, "images", "recipes"));
        _dbFactory = new TestDbContextFactory();
    }

    [Fact]
    public async Task EndToEndWorkflow_ImportApproveSaveScaleShoppingListAndBackup_ShouldSucceed()
    {
        // 1. Setup full service pipeline
        var parser = new SwedishIngredientParser();
        var jsonLd = new JsonLdRecipeExtractor(parser);
        var fallback = new HtmlFallbackExtractor(parser);
        var importer = new WebRecipeImporter(
            new HttpClient(),
            jsonLd,
            fallback,
            new MockImageStorageService(),
            NullLogger<WebRecipeImporter>.Instance);

        var categoryService = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
        var recipeService = new RecipeService(_dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var shoppingService = new ShoppingListService(_dbFactory, aggregator, parser);

        // 2. Simulate Website Import -> Preview Draft (must NOT save to DB before user approval)
        const string icaHtml = """
            <!DOCTYPE html>
            <html><head>
            <script type="application/ld+json">
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Krämig kantarellpasta",
              "description": "Höstens godaste pasta.",
              "prepTime": "PT15M",
              "cookTime": "PT15M",
              "recipeYield": "4 portioner",
              "recipeIngredient": ["400 g pasta", "300 g kantareller", "2,5 dl vispgrädde"],
              "recipeInstructions": [
                { "@type": "HowToStep", "text": "Stek kantarellerna i smör." },
                { "@type": "HowToStep", "text": "Häll på grädden och vänd ner nykokt pasta." }
              ]
            }
            </script>
            </head><body></body></html>
            """;

        var importResult = importer.ParseFromHtml(icaHtml, "https://www.ica.se/recept/kantarellpasta/");
        importResult.Success.Should().BeTrue();

        // Verify database is still empty before explicit user approval
        (await recipeService.GetRecipesAsync()).Should().BeEmpty();

        // 3. User edits preview draft and explicitly approves & saves
        var draft = importResult.DraftRecipe!;
        draft.Title = "Krämig kantarellpasta (Familjerecept)";
        draft.IsFavorite = true;
        var pastaCategory = await categoryService.CreateCategoryAsync(new Category { Name = "Pasta", ColorHex = "#f2a65a" });

        var savedRecipe = await recipeService.CreateRecipeAsync(draft, [pastaCategory.Id]);
        savedRecipe.Id.Should().BeGreaterThan(0);

        // 4. Add scaled ingredients (6 servings instead of 4 => 1.5x) to Shopping List
        await shoppingService.AddRecipeIngredientsAsync(savedRecipe.Id, targetServings: 6);
        var shoppingItems = await shoppingService.GetItemsAsync();
        shoppingItems.Should().HaveCount(3);
        shoppingItems.Single(x => x.Name == "pasta").Quantity.Should().Be(600m);
        shoppingItems.Single(x => x.Name == "kantareller").Quantity.Should().Be(450m);
        shoppingItems.Single(x => x.Name == "vispgrädde").Quantity.Should().Be(3.75m);

        // 5. Export full backup and verify archive integrity
        var backupService = new ZipBackupService(
            _dbFactory,
            Options.Create(new ImageStorageOptions { BaseDataPath = _tempDataPath, ImageFolderName = "images/recipes" }),
            NullLogger<ZipBackupService>.Instance);

        var backupZipBytes = await backupService.CreateBackupArchiveAsync();
        backupZipBytes.Should().NotBeEmpty();

        // 6. Restore into a fresh database and verify
        using var targetDbFactory = new TestDbContextFactory();
        var targetBackupService = new ZipBackupService(
            targetDbFactory,
            Options.Create(new ImageStorageOptions { BaseDataPath = _tempDataPath, ImageFolderName = "images/recipes" }),
            NullLogger<ZipBackupService>.Instance);

        using var archiveStream = new MemoryStream(backupZipBytes);
        var restoreResult = await targetBackupService.RestoreFromArchiveAsync(archiveStream, replaceExisting: true);
        restoreResult.Success.Should().BeTrue();
        restoreResult.RestoredRecipes.Should().Be(1);
    }

    [Fact]
    public async Task HealthEndpoint_And_HealthCheckProbe_ShouldVerifySqliteAndPersistDirectories()
    {
        var hostDataPath = Path.Combine(_tempDataPath, "host_data");
        var previousEnv = Environment.GetEnvironmentVariable("Storage__DataPath");
        Environment.SetEnvironmentVariable("Storage__DataPath", hostDataPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().Contain("\"status\":\"Healthy\"");
            payload.Should().Contain("\"database\":\"Connected\"");

            // Verify persistent subdirectories and SQLite database file were initialized via Storage__DataPath
            File.Exists(Path.Combine(hostDataPath, "receptbanken.db")).Should().BeTrue();
            Directory.Exists(Path.Combine(hostDataPath, "images", "recipes")).Should().BeTrue();
            Directory.Exists(Path.Combine(hostDataPath, "keys")).Should().BeTrue();

            // Verify built-in CLI probe helper returns 0 when healthy and 1 when unreachable
            using var handler = factory.Server.CreateHandler();
            var healthyExitCode = await Program.RunHealthCheckProbeAsync(
                new Uri(factory.Server.BaseAddress, "/health").ToString(),
                handler);
            healthyExitCode.Should().Be(0);

            var unhealthyExitCode = await Program.RunHealthCheckProbeAsync("http://127.0.0.1:1/health");
            unhealthyExitCode.Should().Be(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Storage__DataPath", previousEnv);
        }
    }

    [Fact]
    public async Task ProductionEnvironment_ShouldServeBlazorWebJsAndStaticAssets_AndRenderHomePage()
    {
        var prodDataPath = Path.Combine(_tempDataPath, "prod_data");
        var previousEnv = Environment.GetEnvironmentVariable("Storage__DataPath");
        Environment.SetEnvironmentVariable("Storage__DataPath", prodDataPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                });

            using var client = factory.CreateClient();

            // 1. Verify GET /_framework/blazor.web.js returns HTTP 200 OK with JavaScript content in Production
            using var blazorJsResponse = await client.GetAsync("/_framework/blazor.web.js");
            blazorJsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            blazorJsResponse.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");

            var blazorJsContent = await blazorJsResponse.Content.ReadAsStringAsync();
            blazorJsContent.Should().NotBeNullOrWhiteSpace();
            blazorJsContent.Should().NotStartWith("<!DOCTYPE", "blazor.web.js must return actual JavaScript, not an HTML fallback");
            blazorJsContent.Should().Contain("Blazor");

            // 2. Verify CSS and Fluent UI static web assets are served in Production
            using var appCssResponse = await client.GetAsync("/app.css");
            appCssResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var appCss = await appCssResponse.Content.ReadAsStringAsync();
            appCss.Should().Contain("--fill-color");

            using var bundleCssResponse = await client.GetAsync("/Receptbanken.Web.styles.css");
            bundleCssResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. Verify GET / renders the home page in Production with Blazor Interactive Server script reference
            using var homeResponse = await client.GetAsync("/");
            homeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var homeHtml = await homeResponse.Content.ReadAsStringAsync();
            homeHtml.Should().Contain("_framework/blazor.web.js");
            homeHtml.Should().Contain("Receptbanken");

            // 4. Verify GET /importera renders the import page
            using var importResponse = await client.GetAsync("/importera");
            importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var importHtml = await importResponse.Content.ReadAsStringAsync();
            importHtml.Should().Contain("Importera recept");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Storage__DataPath", previousEnv);
        }
    }

    [Fact]
    public async Task DeploymentArchive_ShouldIncludeAllSourceFilesIncludingDataNamespace_AndExcludeSecretsAndBuildOutputs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Receptbanken.sln")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("solution root must be discoverable from test base directory");
        var repoRoot = dir!.FullName;

        // 1. Verify .dockerignore does not contain a wildcard **/data pattern that could collide with Data namespaces
        var dockerIgnoreLines = (await File.ReadAllLinesAsync(Path.Combine(repoRoot, ".dockerignore")))
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrEmpty(x) && !x.StartsWith('#'))
            .ToList();
        dockerIgnoreLines.Should().NotContain("**/data");

        // 2. Generate distribution archive via deploy.ps1 -DryRun -SkipTests and inspect contents with TarReader
        var archivePath = Path.Combine(_tempDataPath, "verify-dist.tar.gz");
        var deployScriptPath = Path.Combine(repoRoot, "scripts", "deploy.ps1");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{deployScriptPath}\" -ServerHost home-server -ServerUser mikaelollhage -DryRun -SkipTests -ArchiveOutputPath \"{archivePath}\"",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.Should().Be(0, $"deploy.ps1 -DryRun failed. Stdout: {stdout}\nStderr: {stderr}");
        File.Exists(archivePath).Should().BeTrue();

        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        await using (var fs = File.OpenRead(archivePath))
        await using (var gzip = new GZipStream(fs, CompressionMode.Decompress))
        await using (var tar = new TarReader(gzip))
        {
            while (await tar.GetNextEntryAsync() is { } entry)
            {
                var normalized = entry.Name.Replace('\\', '/');
                if (normalized.StartsWith("./", StringComparison.Ordinal))
                {
                    normalized = normalized[2..];
                }
                normalized = normalized.TrimEnd('/');
                if (!string.IsNullOrEmpty(normalized))
                {
                    entryNames.Add(normalized);
                }
            }
        }

        // 3. Explicitly verify src/Receptbanken.Infrastructure/Data/* files are present (case-sensitive Linux paths)
        entryNames.Should().Contain("src/Receptbanken.Infrastructure/Data/AppDbContext.cs");
        entryNames.Should().Contain("src/Receptbanken.Infrastructure/Data/DbInitializer.cs");
        entryNames.Should().Contain("src/Receptbanken.Infrastructure/Data/DesignTimeDbContextFactory.cs");
        entryNames.Should().Contain("src/Receptbanken.Infrastructure/Backup/ZipBackupService.cs");

        // 4. Verify every single .cs, .razor, and .csproj file under src/ (outside bin/obj) is included
        var srcDir = Path.Combine(repoRoot, "src");
        var allCodeFiles = Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".razor", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var rel = Path.GetRelativePath(repoRoot, f).Replace('\\', '/');
                return !rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f => Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))
            .ToList();

        allCodeFiles.Should().NotBeEmpty();
        foreach (var expectedFile in allCodeFiles)
        {
            entryNames.Should().Contain(expectedFile, $"source file '{expectedFile}' must not be excluded from the Docker build archive");
        }

        // 5. Verify no bin, obj, .db, or .env files are included
        entryNames.Should().NotContain(e => e.Contains("/bin/", StringComparison.OrdinalIgnoreCase));
        entryNames.Should().NotContain(e => e.Contains("/obj/", StringComparison.OrdinalIgnoreCase));
        entryNames.Should().NotContain(e => e.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        entryNames.Should().NotContain(e => e.EndsWith(".env", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
        try
        {
            if (Directory.Exists(_tempDataPath))
                Directory.Delete(_tempDataPath, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup errors
        }
    }
}
