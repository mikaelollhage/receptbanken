using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Backup;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Importers;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Infrastructure.Storage;
using Receptbanken.Web.Components;
using Receptbanken.Web.Services;

if (args.Contains("--healthcheck", StringComparer.OrdinalIgnoreCase))
{
    var exitCode = await RunHealthCheckProbeAsync(Environment.GetEnvironmentVariable("HEALTHCHECK_URL"));
    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

// Ensure static web assets (e.g. _framework/blazor.web.js and RCL assets) are mapped
// when running in non-Development environments against build outputs (such as integration tests);
// safely no-ops in published containers where static assets live directly in wwwroot/.
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// Configure Storage Paths
var dataPath = builder.Configuration["Storage:DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataPath);
var dbPath = Path.Combine(dataPath, "receptbanken.db");
var imagesPath = Path.Combine(dataPath, "images");
var keysPath = Path.Combine(dataPath, "keys");
Directory.CreateDirectory(imagesPath);
Directory.CreateDirectory(Path.Combine(imagesPath, "recipes"));
Directory.CreateDirectory(keysPath);

// Persist DataProtection keys in the data volume so antiforgery tokens survive container updates
builder.Services.AddDataProtection()
    .SetApplicationName("Receptbanken")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // Default is 32 KB. Pasted JSON-LD (max WebRecipeImporter.MaxJsonLdLength characters) is sent
        // through the circuit on input; a message above the limit would terminate the circuit.
        options.MaximumReceiveMessageSize = 1024 * 1024;
    });

// Microsoft Fluent UI Blazor
builder.Services.AddFluentUIComponents();

// EF Core SQLite via IDbContextFactory for safe Blazor Server concurrency
builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}");
});

// Image storage service
builder.Services.Configure<ImageStorageOptions>(options =>
{
    options.BaseDataPath = dataPath;
    options.ImageFolderName = "images/recipes";
});
builder.Services.AddSingleton<IImageStorageService, LocalImageStorageService>();

// Pragmatic Application Services
builder.Services.AddSingleton<ServingCalculator>();
builder.Services.AddSingleton<ShoppingListAggregator>();
builder.Services.AddSingleton<ShoppingListNotifier>();
builder.Services.AddSingleton<IRandomProvider, SystemRandomProvider>();
builder.Services.AddSingleton<PantryIngredientMatcher>();
builder.Services.AddSingleton<WeeklyMenuGenerationWeights>();
builder.Services.AddSingleton<WeeklyMenuGenerator>();
builder.Services.AddScoped<ILocalDateProvider, SwedishLocalDateProvider>();
builder.Services.AddScoped<RecipeService>();
builder.Services.AddScoped<CategoryService>();
builder.Services.AddScoped<ShoppingListService>();
builder.Services.AddScoped<WeeklyMenuService>();
builder.Services.AddScoped<IBackupService, ZipBackupService>();
builder.Services.AddScoped<AppNotificationService>();

// Recipe Import Services with Strengthened SSRF Protection
builder.Services.AddSingleton<SwedishIngredientParser>();
builder.Services.AddSingleton<VoiceShoppingListParser>();
builder.Services.AddSingleton<JsonLdRecipeExtractor>();
builder.Services.AddSingleton<HtmlFallbackExtractor>();
builder.Services.AddSingleton<HtmlRecipeTextExtractor>();
builder.Services.AddSingleton<RecipeTextParser>();
builder.Services.AddHttpClient<IRecipeImporter, WebRecipeImporter>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => SsrfValidator.CreateSafeSocketsHttpHandler());

var app = builder.Build();

// Configure the HTTP request pipeline.
var enableHttpsRedirection = builder.Configuration.GetValue<bool>("Security:EnableHttpsRedirection", false);
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    if (enableHttpsRedirection)
    {
        app.UseHsts();
    }
}

if (enableHttpsRedirection)
{
    app.UseHttpsRedirection();
}
app.UseAntiforgery();

// Serve optimized static web assets (.NET 10 MapStaticAssets) and standard static files
app.MapStaticAssets();
app.UseStaticFiles();

// Serve user uploaded & imported recipe images from persistent data directory
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imagesPath),
    RequestPath = "/images"
});

app.MapGet("/health", async (IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct) =>
{
    try
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var canConnect = await db.Database.CanConnectAsync(ct);
        if (!canConnect)
        {
            return Results.Json(
                new { status = "Unhealthy", database = "Disconnected" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await db.Database.ExecuteSqlRawAsync("SELECT 1;", ct);
        return Results.Ok(new { status = "Healthy", database = "Connected" });
    }
    catch (Exception)
    {
        return Results.Json(
            new { status = "Unhealthy", database = "Error" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/backup/export", async (IBackupService backupService, CancellationToken ct) =>
{
    var bytes = await backupService.CreateBackupArchiveAsync(ct);
    var fileName = $"receptbanken-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip";
    return Results.File(bytes, "application/zip", fileName);
});

app.MapPost("/api/voice/shopping-list/add", async (
    [Microsoft.AspNetCore.Mvc.FromBody] VoiceShoppingListRequest? request,
    ShoppingListService shoppingListService,
    VoiceShoppingListParser parser,
    CancellationToken ct) =>
{
    if (request == null || request.Items == null || request.Items.Count == 0)
    {
        return Results.BadRequest(new
        {
            error = "Inga varor angavs. Fältet 'items' måste innehålla minst en varufras."
        });
    }

    if (request.Items.Count > VoiceShoppingListParser.MaxItemsPerRequest)
    {
        return Results.BadRequest(new
        {
            error = $"Maximalt {VoiceShoppingListParser.MaxItemsPerRequest} varufraser kan tolkas per anrop."
        });
    }

    var result = await shoppingListService.AddVoiceItemsAsync(request, parser, ct);
    return Results.Ok(result);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Initialize database schema and default seed data (fail-fast if migration or initialization fails)
using (var scope = app.Services.CreateScope())
{
    try
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await DbInitializer.InitializeAsync(context, logger);
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogCritical(ex, "Kritiskt fel vid databasinitiering. Applikationen avbryter uppstart.");
        throw;
    }
}

app.Run();

public partial class Program
{
    public static async Task<int> RunHealthCheckProbeAsync(string? url = null, HttpMessageHandler? handler = null)
    {
        var targetUrl = string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:8080/health" : url;
        try
        {
            using var client = handler != null
                ? new HttpClient(handler, disposeHandler: false)
                : new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(4);

            using var response = await client.GetAsync(targetUrl);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
