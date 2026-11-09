using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Tests.Integration;

public class VoiceShoppingListApiTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly string? _previousStorageEnv;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public VoiceShoppingListApiTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), $"rb_voice_api_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataPath);
        Directory.CreateDirectory(Path.Combine(_tempDataPath, "images", "recipes"));
        Directory.CreateDirectory(Path.Combine(_tempDataPath, "keys"));

        _previousStorageEnv = Environment.GetEnvironmentVariable("Storage__DataPath");
        Environment.SetEnvironmentVariable("Storage__DataPath", _tempDataPath);

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Storage__DataPath", _previousStorageEnv);

        try
        {
            if (Directory.Exists(_tempDataPath))
            {
                Directory.Delete(_tempDataPath, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in temp directory
        }
    }

    private async Task<int> GetShoppingListItemCountAsync()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ShoppingListItems.CountAsync();
    }

    private async Task<int> GetRecipeCountAsync()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Recipes.CountAsync();
    }

    [Fact]
    public async Task PostPreview_SingleItem_ShouldReturn200AndStructuredPreviewWithoutModifyingDatabase()
    {
        var countBefore = await GetShoppingListItemCountAsync();
        var notifier = _factory.Services.GetRequiredService<ShoppingListNotifier>();
        var versionBefore = notifier.Version;

        var payload = new VoiceShoppingListRequest
        {
            Items = ["2 liter mjölk"]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/preview", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotContain("added", because: "Preview endpoint must never state that items were added");
        content.Should().NotContain("saved", because: "Preview endpoint must never state that items were saved");

        var result = JsonSerializer.Deserialize<VoiceShoppingListPreviewResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(1);
        result.ValidCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);
        result.Items.Should().HaveCount(1);

        var item = result.Items[0];
        item.Success.Should().BeTrue();
        item.RawPhrase.Should().Be("2 liter mjölk");
        item.Name.Should().Be("mjölk");
        item.Quantity.Should().Be(2m);
        item.Unit.Should().Be("l");
        item.DisplayText.Should().Be("2 l mjölk");
        item.Error.Should().BeNull();

        // STRICT VERIFICATION: Database must not have been modified
        var countAfter = await GetShoppingListItemCountAsync();
        countAfter.Should().Be(countBefore, "Preview endpoint must be strictly read-only");
        notifier.Version.Should().Be(versionBefore, "Notifier version must not increment on read-only preview");
    }

    [Fact]
    public async Task PostPreview_MultipleItemsWithQuantitiesAndUnits_ShouldParseAllAndMakeZeroDatabaseWrites()
    {
        var countBefore = await GetShoppingListItemCountAsync();
        var recipeCountBefore = await GetRecipeCountAsync();
        var notifier = _factory.Services.GetRequiredService<ShoppingListNotifier>();
        var versionBefore = notifier.Version;

        var payload = new VoiceShoppingListRequest
        {
            Items =
            [
                "1,5 dl mjölk",
                "½ tsk salt",
                "1-2 klyftor vitlök, finhackade",
                "500 g blandfärs",
                "1 paket smör",
                "ekologiska bananer"
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/preview", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListPreviewResponse>();

        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(6);
        result.ValidCount.Should().Be(6);
        result.ErrorCount.Should().Be(0);

        result.Items[0].Name.Should().Be("mjölk");
        result.Items[0].Quantity.Should().Be(1.5m);
        result.Items[0].Unit.Should().Be("dl");

        result.Items[1].Name.Should().Be("salt");
        result.Items[1].Quantity.Should().Be(0.5m);
        result.Items[1].Unit.Should().Be("tsk");

        result.Items[2].Name.Should().Be("vitlök");
        result.Items[2].Quantity.Should().Be(1m);
        result.Items[2].QuantityMax.Should().Be(2m);
        result.Items[2].Unit.Should().Be("klyftor");
        result.Items[2].Comment.Should().Be("finhackade");

        result.Items[3].Name.Should().Be("blandfärs");
        result.Items[3].Quantity.Should().Be(500m);
        result.Items[3].Unit.Should().Be("g");

        result.Items[4].Name.Should().Be("smör");
        result.Items[4].Quantity.Should().Be(1m);
        result.Items[4].Unit.Should().Be("paket");

        result.Items[5].Name.Should().Be("ekologiska bananer");
        result.Items[5].Quantity.Should().BeNull();
        result.Items[5].Unit.Should().BeNull();

        // Verify zero writes
        (await GetShoppingListItemCountAsync()).Should().Be(countBefore);
        (await GetRecipeCountAsync()).Should().Be(recipeCountBefore);
        notifier.Version.Should().Be(versionBefore);
    }

    [Fact]
    public async Task PostPreview_MixedValidAndParserErrors_ShouldReportErrorsPerItemWithoutThrowingAndWithoutWriting()
    {
        var countBefore = await GetShoppingListItemCountAsync();
        var notifier = _factory.Services.GetRequiredService<ShoppingListNotifier>();
        var versionBefore = notifier.Version;

        var payload = new VoiceShoppingListRequest
        {
            Items =
            [
                "2 liter mjölk",
                "",
                "   ",
                "2 dl",
                "???",
                "falukorv"
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/preview", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListPreviewResponse>();

        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(6);
        result.ValidCount.Should().Be(2);
        result.ErrorCount.Should().Be(4);

        // Valid 1
        result.Items[0].Success.Should().BeTrue();
        result.Items[0].Name.Should().Be("mjölk");

        // Empty string
        result.Items[1].Success.Should().BeFalse();
        result.Items[1].Error.Should().Be("Varufrasen kan inte vara tom.");

        // Whitespace string
        result.Items[2].Success.Should().BeFalse();
        result.Items[2].Error.Should().Be("Varufrasen kan inte vara tom.");

        // Parser error: only quantity/unit without name
        result.Items[3].Success.Should().BeFalse();
        result.Items[3].Quantity.Should().Be(2m);
        result.Items[3].Unit.Should().Be("dl");
        result.Items[3].Error.Should().Contain("Inget varunamn angavs");

        // Invalid characters
        result.Items[4].Success.Should().BeFalse();
        result.Items[4].Error.Should().Contain("Inget giltigt varunamn");

        // Valid 2
        result.Items[5].Success.Should().BeTrue();
        result.Items[5].Name.Should().Be("falukorv");

        // Strict verification: zero writes
        (await GetShoppingListItemCountAsync()).Should().Be(countBefore);
        notifier.Version.Should().Be(versionBefore);
    }

    [Fact]
    public async Task PostPreview_EmptyItemList_ShouldReturn400BadRequestAndZeroWrites()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var payload = new VoiceShoppingListRequest
        {
            Items = []
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/preview", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Inga varor angavs");

        (await GetShoppingListItemCountAsync()).Should().Be(countBefore);
    }

    [Fact]
    public async Task PostPreview_NullItems_ShouldReturn400BadRequestAndZeroWrites()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/voice/shopping-list/preview", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Inga varor angavs");

        (await GetShoppingListItemCountAsync()).Should().Be(countBefore);
    }

    [Fact]
    public async Task PostPreview_MalformedJson_ShouldReturn400BadRequestAndZeroWrites()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var malformedJson = "{ \"items\": [ \"2 dl mjölk\", ";
        var content = new StringContent(malformedJson, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/voice/shopping-list/preview", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetShoppingListItemCountAsync()).Should().Be(countBefore);
    }
}
