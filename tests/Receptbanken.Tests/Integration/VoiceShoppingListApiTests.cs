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
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Integration;

[Collection("IntegrationTests")]
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
    public async Task PostAdd_SingleValidItem_ShouldSaveInShoppingListAndReturnAdded()
    {
        var countBefore = await GetShoppingListItemCountAsync();
        var notifier = _factory.Services.GetRequiredService<ShoppingListNotifier>();
        var versionBefore = notifier.Version;

        var payload = new VoiceShoppingListRequest
        {
            Items = ["2 liter mellanmjölk"]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.TotalCount.Should().Be(1);
        result.AddedCount.Should().Be(1);
        result.AlreadyExistsCount.Should().Be(0);
        result.ErrorCount.Should().Be(0);
        result.Items.Should().HaveCount(1);

        var item = result.Items[0];
        item.RawPhrase.Should().Be("2 liter mellanmjölk");
        item.Status.Should().Be(VoiceShoppingListItemStatus.Added);
        item.DisplayText.Should().Be("2 l mellanmjölk");
        item.Error.Should().BeNull();

        // Verify DB write
        var countAfter = await GetShoppingListItemCountAsync();
        countAfter.Should().Be(countBefore + 1);
        notifier.Version.Should().BeGreaterThan(versionBefore);

        // Verify regular shopping list service reads the saved item
        using var scope = _factory.Services.CreateScope();
        var shoppingService = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
        var allItems = await shoppingService.GetItemsAsync();
        var savedItem = allItems.FirstOrDefault(x => x.Name == "mellanmjölk");
        savedItem.Should().NotBeNull();
        savedItem!.Quantity.Should().Be(2m);
        savedItem.Unit.Should().Be("l");
        savedItem.IsChecked.Should().BeFalse();
        savedItem.RawText.Should().Be("2 l mellanmjölk");
    }

    [Fact]
    public async Task PostAdd_MultipleItems_ShouldSaveAllWithPreservedFields()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var payload = new VoiceShoppingListRequest
        {
            Items =
            [
                "3 bananer",
                "1 paket kaffe",
                "1 burk krossade tomater (à 400 g)",
                "2 msk smör, smält"
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.TotalCount.Should().Be(4);
        result.AddedCount.Should().Be(4);
        result.AlreadyExistsCount.Should().Be(0);
        result.ErrorCount.Should().Be(0);

        (await GetShoppingListItemCountAsync()).Should().Be(countBefore + 4);

        using var scope = _factory.Services.CreateScope();
        var shoppingService = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
        var allItems = await shoppingService.GetItemsAsync();

        var bananer = allItems.First(x => x.Name == "bananer");
        bananer.Quantity.Should().Be(3m);
        bananer.Unit.Should().BeNull();

        var kaffe = allItems.First(x => x.Name == "kaffe");
        kaffe.Quantity.Should().Be(1m);
        kaffe.Unit.Should().Be("paket");

        var tomater = allItems.First(x => x.Name == "krossade tomater");
        tomater.Quantity.Should().Be(1m);
        tomater.Unit.Should().Be("burk");
        tomater.Comment.Should().Be("à 400 g");

        var smor = allItems.First(x => x.Name == "smör");
        smor.Quantity.Should().Be(2m);
        smor.Unit.Should().Be("msk");
        smor.Comment.Should().Be("smält");
    }

    [Fact]
    public async Task PostAdd_AlreadyExistingItem_ShouldReturnAlreadyExistsAndNotModifyDatabase()
    {
        // Pre-populate shopping list with 1 item
        using (var scope = _factory.Services.CreateScope())
        {
            var shoppingService = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
            await shoppingService.AddManualItemAsync("1 liter mellanmjölk");
        }

        var countBefore = await GetShoppingListItemCountAsync();
        var notifier = _factory.Services.GetRequiredService<ShoppingListNotifier>();
        var versionBefore = notifier.Version;

        var payload = new VoiceShoppingListRequest
        {
            Items = ["2 liter mellanmjölk"]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.TotalCount.Should().Be(1);
        result.AddedCount.Should().Be(0);
        result.AlreadyExistsCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);

        var item = result.Items[0];
        item.Status.Should().Be(VoiceShoppingListItemStatus.AlreadyExists);
        item.DisplayText.Should().Be("2 l mellanmjölk");
        item.Error.Should().BeNull();

        // Verify DB was NOT modified (count same, existing item quantity still 1)
        (await GetShoppingListItemCountAsync()).Should().Be(countBefore);
        notifier.Version.Should().Be(versionBefore);

        using (var scope = _factory.Services.CreateScope())
        {
            var shoppingService = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
            var allItems = await shoppingService.GetItemsAsync();
            var existing = allItems.First(x => x.Name == "mellanmjölk");
            existing.Quantity.Should().Be(1m, "Existing item must not be modified when voice item already exists");
        }
    }

    [Fact]
    public async Task PostAdd_SameItemTwiceInOneRequest_ShouldAddFirstThenReturnAlreadyExists()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var payload = new VoiceShoppingListRequest
        {
            Items = ["3 bananer", "3 bananer"]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.TotalCount.Should().Be(2);
        result.AddedCount.Should().Be(1);
        result.AlreadyExistsCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);

        result.Items[0].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[0].DisplayText.Should().Be("3 bananer");

        result.Items[1].Status.Should().Be(VoiceShoppingListItemStatus.AlreadyExists);
        result.Items[1].DisplayText.Should().Be("3 bananer");

        // Exactly 1 new record in DB
        (await GetShoppingListItemCountAsync()).Should().Be(countBefore + 1);
    }

    [Fact]
    public async Task PostAdd_RepeatedIdenticalCall_ShouldReturnAlreadyExistsForSavedItems()
    {
        var payload = new VoiceShoppingListRequest
        {
            Items = ["1 paket kaffe"]
        };

        // Call 1
        var response1 = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        var result1 = await response1.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();
        result1!.AddedCount.Should().Be(1);
        result1.AlreadyExistsCount.Should().Be(0);

        var countAfterCall1 = await GetShoppingListItemCountAsync();

        // Call 2 (identical)
        var response2 = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);
        var result2 = await response2.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();
        result2!.AddedCount.Should().Be(0);
        result2.AlreadyExistsCount.Should().Be(1);
        result2.Success.Should().BeTrue();

        var countAfterCall2 = await GetShoppingListItemCountAsync();
        countAfterCall2.Should().Be(countAfterCall1);
    }

    [Fact]
    public async Task PostAdd_InvalidPhrase_ShouldReturnErrorWithoutWriting()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var payload = new VoiceShoppingListRequest
        {
            Items = ["2 dl"]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.TotalCount.Should().Be(1);
        result.AddedCount.Should().Be(0);
        result.AlreadyExistsCount.Should().Be(0);
        result.ErrorCount.Should().Be(1);

        result.Items[0].Status.Should().Be(VoiceShoppingListItemStatus.Error);
        result.Items[0].DisplayText.Should().BeNull();
        result.Items[0].Error.Should().Contain("Inget varunamn angavs");

        (await GetShoppingListItemCountAsync()).Should().Be(countBefore);
    }

    [Fact]
    public async Task PostAdd_MixedValidAndInvalidItems_ShouldNotBlockLaterValidItems()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var payload = new VoiceShoppingListRequest
        {
            Items =
            [
                "2 liter mellanmjölk",
                "2 dl",
                "1 paket kaffe"
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.TotalCount.Should().Be(3);
        result.AddedCount.Should().Be(2);
        result.AlreadyExistsCount.Should().Be(0);
        result.ErrorCount.Should().Be(1);

        result.Items[0].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[0].DisplayText.Should().Be("2 l mellanmjölk");

        result.Items[1].Status.Should().Be(VoiceShoppingListItemStatus.Error);
        result.Items[1].Error.Should().Contain("Inget varunamn angavs");

        result.Items[2].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[2].DisplayText.Should().Be("1 paket kaffe");

        (await GetShoppingListItemCountAsync()).Should().Be(countBefore + 2);
    }

    [Fact]
    public async Task PostAdd_WriteErrorForSingleItem_ShouldReportErrorAndContinue()
    {
        using var scope = _factory.Services.CreateScope();
        var shoppingService = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
        var parser = scope.ServiceProvider.GetRequiredService<VoiceShoppingListParser>();

        // Set test hook to simulate a write error on "falukorv"
        shoppingService.BeforeVoiceItemSaveTestHook = item =>
        {
            if (item.Name == "falukorv")
            {
                throw new InvalidOperationException("Simulerat databasfel vid sparning");
            }
        };

        var request = new VoiceShoppingListRequest
        {
            Items = ["smör", "falukorv", "ägg"]
        };

        var result = await shoppingService.AddVoiceItemsAsync(request, parser);

        result.Success.Should().BeFalse();
        result.TotalCount.Should().Be(3);
        result.AddedCount.Should().Be(2);
        result.AlreadyExistsCount.Should().Be(0);
        result.ErrorCount.Should().Be(1);

        result.Items[0].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[0].DisplayText.Should().Be("smör");

        result.Items[1].Status.Should().Be(VoiceShoppingListItemStatus.Error);
        result.Items[1].Error.Should().Contain("Simulerat databasfel");

        result.Items[2].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[2].DisplayText.Should().Be("ägg");

        var allItems = await shoppingService.GetItemsAsync();
        allItems.Should().Contain(x => x.Name == "smör");
        allItems.Should().NotContain(x => x.Name == "falukorv");
        allItems.Should().Contain(x => x.Name == "ägg");
    }

    [Fact]
    public async Task PostAdd_EmptyItems_ShouldReturn400BadRequest()
    {
        var payload = new VoiceShoppingListRequest { Items = [] };
        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostAdd_MalformedJson_ShouldReturn400BadRequest()
    {
        var content = new StringContent("{ \"items\": [ \"2 dl mjölk\", ", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/voice/shopping-list/add", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }


    [Fact]
    public async Task PostAdd_SwedishWordNumbersEnEtt_ShouldSaveCorrectQuantitiesAndHandleDuplicates()
    {
        var countBefore = await GetShoppingListItemCountAsync();

        var payload = new VoiceShoppingListRequest
        {
            Items =
            [
                "en mjölk",
                "en ketchup",
                "ett äpple",
                "ett paket ris"
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.TotalCount.Should().Be(4);
        result.AddedCount.Should().Be(4);
        result.AlreadyExistsCount.Should().Be(0);
        result.ErrorCount.Should().Be(0);

        result.Items[0].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[0].DisplayText.Should().Be("1 mjölk");

        result.Items[1].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[1].DisplayText.Should().Be("1 ketchup");

        result.Items[2].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[2].DisplayText.Should().Be("1 äpple");

        result.Items[3].Status.Should().Be(VoiceShoppingListItemStatus.Added);
        result.Items[3].DisplayText.Should().Be("1 paket ris");

        // Verify persisted items in DB
        (await GetShoppingListItemCountAsync()).Should().Be(countBefore + 4);

        using var scope = _factory.Services.CreateScope();
        var shoppingService = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
        var allItems = await shoppingService.GetItemsAsync();

        var mjölk = allItems.First(x => x.Name == "mjölk");
        mjölk.Quantity.Should().Be(1m);
        mjölk.Unit.Should().BeNull();

        var ketchup = allItems.First(x => x.Name == "ketchup");
        ketchup.Quantity.Should().Be(1m);
        ketchup.Unit.Should().BeNull();

        var äpple = allItems.First(x => x.Name == "äpple");
        äpple.Quantity.Should().Be(1m);
        äpple.Unit.Should().BeNull();

        var ris = allItems.First(x => x.Name == "ris");
        ris.Quantity.Should().Be(1m);
        ris.Unit.Should().Be("paket");

        // Verify duplicate check on repeated call with word number
        var duplicatePayload = new VoiceShoppingListRequest
        {
            Items = ["en mjölk", "ett äpple"]
        };
        var dupResponse = await _client.PostAsJsonAsync("/api/voice/shopping-list/add", duplicatePayload);
        dupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var dupResult = await dupResponse.Content.ReadFromJsonAsync<VoiceShoppingListAddResponse>();
        dupResult!.AddedCount.Should().Be(0);
        dupResult.AlreadyExistsCount.Should().Be(2);
        dupResult.Success.Should().BeTrue();
    }
}
