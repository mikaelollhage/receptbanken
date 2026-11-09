using System.Text.RegularExpressions;
using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

/// <summary>
/// Parser för röststyrd inköpslista. Återanvänder Receptbankens svenska ingredienstolk
/// och normaliseringslogik utan att utföra några databasanrop eller skrivningar.
/// </summary>
public class VoiceShoppingListParser
{
    public const int MaxItemPhraseLength = 200;
    public const int MaxItemsPerRequest = 100;

    private readonly SwedishIngredientParser _ingredientParser;
    private readonly ShoppingListAggregator _aggregator;

    public VoiceShoppingListParser(
        SwedishIngredientParser ingredientParser,
        ShoppingListAggregator aggregator)
    {
        _ingredientParser = ingredientParser ?? throw new ArgumentNullException(nameof(ingredientParser));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
    }

    /// <summary>
    /// Tolkar och förhandsgranskar samtliga inkommande varufraser i förfrågan.
    /// </summary>
    public VoiceShoppingListPreviewResponse ParsePreview(VoiceShoppingListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ArgumentException("Minst en varufras måste anges i 'items'.", nameof(request));
        }

        if (request.Items.Count > MaxItemsPerRequest)
        {
            throw new ArgumentException($"Maximalt {MaxItemsPerRequest} varufraser kan tolkas per anrop.", nameof(request));
        }

        var results = request.Items.Select(ParseItem).ToList();

        return new VoiceShoppingListPreviewResponse
        {
            Items = results,
            TotalCount = results.Count,
            ValidCount = results.Count(x => x.Success),
            ErrorCount = results.Count(x => !x.Success)
        };
    }

    /// <summary>
    /// Tolkar en enskild varufras och extraherar identifierade mängder, enheter, varunamn och kommentarer.
    /// </summary>
    public VoiceShoppingListItemPreviewDto ParseItem(string? rawPhrase)
    {
        if (string.IsNullOrWhiteSpace(rawPhrase))
        {
            return new VoiceShoppingListItemPreviewDto
            {
                RawPhrase = rawPhrase ?? string.Empty,
                Success = false,
                Error = "Varufrasen kan inte vara tom."
            };
        }

        var trimmed = rawPhrase.Trim();

        if (trimmed.Length > MaxItemPhraseLength)
        {
            return new VoiceShoppingListItemPreviewDto
            {
                RawPhrase = trimmed,
                Success = false,
                Error = $"Varufrasen överskrider tillåten längd (max {MaxItemPhraseLength} tecken)."
            };
        }

        try
        {
            var parsed = _ingredientParser.Parse(trimmed);
            var item = _aggregator.CreateFromRecipeIngredient(parsed, baseServings: 1, targetServings: 1, recipeId: null);

            // Validera att ett faktiskt råvaru-/produktnamn identifierades.
            // I SwedishIngredientParser faller Name tillbaka på rawText om namePart efter mängd och enhet var tom.
            if (item.Quantity.HasValue && string.Equals(item.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return new VoiceShoppingListItemPreviewDto
                {
                    RawPhrase = trimmed,
                    Success = false,
                    Quantity = item.Quantity,
                    QuantityMax = item.QuantityMax,
                    Unit = item.Unit,
                    Error = $"Inget varunamn angavs i frasen '{trimmed}'."
                };
            }

            if (!Regex.IsMatch(item.Name, @"\p{L}"))
            {
                return new VoiceShoppingListItemPreviewDto
                {
                    RawPhrase = trimmed,
                    Success = false,
                    Quantity = item.Quantity,
                    QuantityMax = item.QuantityMax,
                    Unit = item.Unit,
                    Error = $"Inget giltigt varunamn kunde identifieras i frasen '{trimmed}'."
                };
            }

            return new VoiceShoppingListItemPreviewDto
            {
                RawPhrase = trimmed,
                Success = true,
                Name = item.Name,
                Quantity = item.Quantity,
                QuantityMax = item.QuantityMax,
                Unit = item.Unit,
                Comment = item.Comment,
                DisplayText = item.RawText
            };
        }
        catch (Exception ex)
        {
            return new VoiceShoppingListItemPreviewDto
            {
                RawPhrase = trimmed,
                Success = false,
                Error = $"Fel vid tolkning av varufras: {ex.Message}"
            };
        }
    }
}
