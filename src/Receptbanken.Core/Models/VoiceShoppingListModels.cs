using System.Text.Json.Serialization;

namespace Receptbanken.Core.Models;

/// <summary>
/// Inkommande förfrågan för röststyrd inköpslista.
/// </summary>
public sealed class VoiceShoppingListRequest
{
    [JsonPropertyName("items")]
    public List<string>? Items { get; set; }
}

/// <summary>
/// Strukturerat förhandsgranskningsresultat för en enskild tolkad varufras.
/// </summary>
public sealed class VoiceShoppingListItemPreviewDto
{
    [JsonPropertyName("rawPhrase")]
    public required string RawPhrase { get; set; }

    [JsonPropertyName("success")]
    public required bool Success { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("quantityMax")]
    public decimal? QuantityMax { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("displayText")]
    public string? DisplayText { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Svar för skrivskyddad förhandsgranskning av röststyrda varufraser.
/// </summary>
public sealed class VoiceShoppingListPreviewResponse
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<VoiceShoppingListItemPreviewDto> Items { get; set; }

    [JsonPropertyName("totalCount")]
    public required int TotalCount { get; set; }

    [JsonPropertyName("validCount")]
    public required int ValidCount { get; set; }

    [JsonPropertyName("errorCount")]
    public required int ErrorCount { get; set; }
}
