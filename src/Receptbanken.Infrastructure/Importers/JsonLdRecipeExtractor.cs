using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Infrastructure.Importers;

public class JsonLdRecipeExtractor
{
    private readonly SwedishIngredientParser _ingredientParser;

    public JsonLdRecipeExtractor(SwedishIngredientParser ingredientParser)
    {
        _ingredientParser = ingredientParser;
    }

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    public bool TryExtract(IDocument document, out Recipe? recipe, out string? remoteImageUrl)
    {
        recipe = null;
        remoteImageUrl = null;

        var scriptNodes = document.QuerySelectorAll("script[type='application/ld+json']");
        foreach (var script in scriptNodes)
        {
            var jsonText = script.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(jsonText))
                continue;

            // Malformed blocks or blocks without a recipe are skipped; try the next script block.
            var result = ExtractFromJson(jsonText);
            if (result.Status == JsonLdExtractionStatus.Success)
            {
                recipe = result.Recipe;
                remoteImageUrl = result.RemoteImageUrl;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shared JSON-LD entry point used both for script blocks found in fetched HTML and for
    /// manually pasted JSON-LD. Accepts a single object, an object with @graph, or an array.
    /// When several recipes are present, the first recipe with a name in document order
    /// (depth-first) is selected deterministically.
    /// </summary>
    public JsonLdExtractionResult ExtractFromJson(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
            return new JsonLdExtractionResult(JsonLdExtractionStatus.NoRecipe);

        try
        {
            using var doc = JsonDocument.Parse(jsonText, ParseOptions);

            var candidates = new List<JsonElement>();
            CollectRecipeElements(doc.RootElement, candidates, depth: 0);

            foreach (var recipeEl in candidates)
            {
                var recipe = MapRecipeElement(recipeEl, out var remoteImageUrl);
                if (!string.IsNullOrWhiteSpace(recipe.Title))
                {
                    var url = CleanUrl(GetStringProperty(recipeEl, "url"));
                    var hasImageProp = recipeEl.TryGetProperty("image", out _);
                    return new JsonLdExtractionResult(
                        JsonLdExtractionStatus.Success,
                        recipe,
                        remoteImageUrl,
                        url,
                        candidates.Count,
                        HasImageProperty: hasImageProp);
                }
            }

            return new JsonLdExtractionResult(JsonLdExtractionStatus.NoRecipe, RecipeCandidateCount: candidates.Count);
        }
        catch (JsonException)
        {
            return new JsonLdExtractionResult(JsonLdExtractionStatus.InvalidJson);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // Defensive: unexpected data shapes in untrusted JSON-LD must never crash the caller.
            return new JsonLdExtractionResult(JsonLdExtractionStatus.ParseError, Error: ex);
        }
    }

    private const int MaxSearchDepth = 32;

    private static void CollectRecipeElements(JsonElement element, List<JsonElement> found, int depth)
    {
        if (depth > MaxSearchDepth)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsRecipeType(element))
            {
                found.Add(element);
                return;
            }

            if (element.TryGetProperty("@graph", out var graphEl))
            {
                CollectRecipeElements(graphEl, found, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectRecipeElements(item, found, depth + 1);
            }
        }
    }

    private static string? CleanUrl(string? url)
    {
        var trimmed = url?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool IsRecipeType(JsonElement obj)
    {
        if (!obj.TryGetProperty("@type", out var typeProp))
            return false;

        if (typeProp.ValueKind == JsonValueKind.String)
        {
            var t = typeProp.GetString() ?? "";
            return t.Equals("Recipe", StringComparison.OrdinalIgnoreCase) ||
                   t.EndsWith("/Recipe", StringComparison.OrdinalIgnoreCase);
        }

        if (typeProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var t = item.GetString() ?? "";
                    if (t.Equals("Recipe", StringComparison.OrdinalIgnoreCase) ||
                        t.EndsWith("/Recipe", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    private Recipe MapRecipeElement(JsonElement el, out string? remoteImageUrl)
    {
        remoteImageUrl = ExtractImageUrl(el);

        var title = CleanText(GetStringProperty(el, "name")) ?? string.Empty;
        var description = CleanText(GetStringProperty(el, "description"));

        var prepMinutes = ParseIsoDurationMinutes(GetStringProperty(el, "prepTime"));
        var cookMinutes = ParseIsoDurationMinutes(GetStringProperty(el, "cookTime"));
        var totalMinutes = ParseIsoDurationMinutes(GetStringProperty(el, "totalTime"));

        if (!prepMinutes.HasValue && !cookMinutes.HasValue && totalMinutes.HasValue)
        {
            cookMinutes = totalMinutes;
        }

        ParseYield(el, out int servings, out string servingsUnit);

        var ingredients = ExtractIngredients(el);
        var instructions = ExtractInstructions(el);

        return new Recipe
        {
            Title = title,
            Description = description,
            PrepTimeMinutes = prepMinutes,
            CookTimeMinutes = cookMinutes,
            Servings = servings,
            ServingsUnit = servingsUnit,
            Ingredients = ingredients,
            Instructions = instructions
        };
    }

    private List<Ingredient> ExtractIngredients(JsonElement el)
    {
        var result = new List<Ingredient>();
        if (!el.TryGetProperty("recipeIngredient", out var ingProp))
            return result;

        int index = 1;
        if (ingProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ingProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var line = CleanText(item.GetString());
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        result.Add(_ingredientParser.Parse(line, index++));
                    }
                }
            }
        }
        else if (ingProp.ValueKind == JsonValueKind.String)
        {
            var line = CleanText(ingProp.GetString());
            if (!string.IsNullOrWhiteSpace(line))
            {
                result.Add(_ingredientParser.Parse(line, index));
            }
        }

        return result;
    }

    private static List<InstructionStep> ExtractInstructions(JsonElement el)
    {
        var steps = new List<InstructionStep>();
        if (!el.TryGetProperty("recipeInstructions", out var instProp))
            return steps;

        int stepNum = 1;
        AppendInstructionNodes(instProp, steps, ref stepNum, null);
        return steps;
    }

    private static void AppendInstructionNodes(JsonElement node, List<InstructionStep> steps, ref int stepNum, string? currentGroup)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                var raw = CleanText(node.GetString());
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    steps.Add(new InstructionStep
                    {
                        StepNumber = stepNum++,
                        InstructionText = raw,
                        GroupHeader = currentGroup
                    });
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    AppendInstructionNodes(item, steps, ref stepNum, currentGroup);
                }
                break;

            case JsonValueKind.Object:
                var type = GetStringProperty(node, "@type") ?? "";
                if (type.Contains("HowToSection", StringComparison.OrdinalIgnoreCase) ||
                    node.TryGetProperty("itemListElement", out _))
                {
                    var sectionName = CleanText(GetStringProperty(node, "name")) ?? currentGroup;
                    if (node.TryGetProperty("itemListElement", out var items))
                    {
                        AppendInstructionNodes(items, steps, ref stepNum, sectionName);
                    }
                }
                else
                {
                    var text = CleanText(GetStringProperty(node, "text")) ?? CleanText(GetStringProperty(node, "name"));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        steps.Add(new InstructionStep
                        {
                            StepNumber = stepNum++,
                            InstructionText = text,
                            GroupHeader = currentGroup
                        });
                    }
                }
                break;
        }
    }

    private static string? ExtractImageUrl(JsonElement el)
    {
        if (!el.TryGetProperty("image", out var imgProp))
            return null;

        return ExtractFirstValidImageUrl(imgProp);
    }

    private static string? ExtractFirstValidImageUrl(JsonElement prop)
    {
        switch (prop.ValueKind)
        {
            case JsonValueKind.String:
                return CleanAndValidateImageUrl(prop.GetString());

            case JsonValueKind.Object:
                if (prop.TryGetProperty("url", out var urlEl))
                {
                    var found = ExtractFirstValidImageUrl(urlEl);
                    if (found != null)
                        return found;
                }

                if (prop.TryGetProperty("contentUrl", out var cUrlEl))
                {
                    var found = ExtractFirstValidImageUrl(cUrlEl);
                    if (found != null)
                        return found;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in prop.EnumerateArray())
                {
                    var found = ExtractFirstValidImageUrl(item);
                    if (found != null)
                        return found;
                }
                break;
        }

        return null;
    }

    private static string? CleanAndValidateImageUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        if (!SsrfValidator.IsSafeUri(uri, out _))
            return null;

        return uri.AbsoluteUri;
    }

    private static void ParseYield(JsonElement el, out int servings, out string servingsUnit)
    {
        servings = 4;
        servingsUnit = "portioner";

        if (!el.TryGetProperty("recipeYield", out var yieldProp))
            return;

        string? yieldText = null;
        if (yieldProp.ValueKind == JsonValueKind.Number && yieldProp.TryGetInt32(out var numYield) && numYield > 0)
        {
            servings = numYield;
            return;
        }
        else if (yieldProp.ValueKind == JsonValueKind.String)
        {
            yieldText = yieldProp.GetString();
        }
        else if (yieldProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in yieldProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    yieldText = item.GetString();
                    break;
                }
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n) && n > 0)
                {
                    servings = n;
                    return;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(yieldText))
        {
            var match = Regex.Match(yieldText.Trim(), @"(\d+)\s*([a-zA-ZåäöÅÄÖ]+)?");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsedCount) && parsedCount > 0)
            {
                servings = parsedCount;
                if (match.Groups[2].Success && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
                {
                    servingsUnit = match.Groups[2].Value.ToLowerInvariant();
                }
            }
        }
    }

    public static int? ParseIsoDurationMinutes(string? isoDuration)
    {
        if (string.IsNullOrWhiteSpace(isoDuration))
            return null;

        // Matches ISO 8601 duration e.g. PT1H30M, PT45M, P0DT1H0M0S
        var match = Regex.Match(isoDuration.Trim(), @"^P(?:(\d+)D)?T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        int totalMinutes = 0;
        if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var days))
            totalMinutes += days * 24 * 60;
        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var hours))
            totalMinutes += hours * 60;
        if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var mins))
            totalMinutes += mins;

        return totalMinutes > 0 ? totalMinutes : null;
    }

    private static string? GetStringProperty(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var decoded = WebUtility.HtmlDecode(text);
        var strippedHtml = Regex.Replace(decoded, @"<[^>]+>", " ");
        return Regex.Replace(strippedHtml, @"\s+", " ").Trim();
    }
}

public enum JsonLdExtractionStatus
{
    Success,
    InvalidJson,
    NoRecipe,
    ParseError
}

public sealed record JsonLdExtractionResult(
    JsonLdExtractionStatus Status,
    Recipe? Recipe = null,
    string? RemoteImageUrl = null,
    string? RecipeUrl = null,
    int RecipeCandidateCount = 0,
    Exception? Error = null,
    bool HasImageProperty = false);

