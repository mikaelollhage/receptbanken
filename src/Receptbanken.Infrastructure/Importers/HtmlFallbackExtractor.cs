using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Infrastructure.Importers;

public class HtmlFallbackExtractor
{
    private readonly SwedishIngredientParser _ingredientParser;

    private static readonly string[] IngredientSelectors =
    [
        ".recipe-ingredients li",
        ".ingredients li",
        ".ingredienser li",
        "[class*='ingredient'] li",
        "[data-testid*='ingredient']",
        "ul.ingredients-list li"
    ];

    private static readonly string[] InstructionSelectors =
    [
        ".recipe-instructions li",
        ".instructions li",
        ".gor-sa-har li",
        ".tillagning li",
        ".steps li",
        "[class*='instruction'] li",
        "[class*='step'] p"
    ];

    public HtmlFallbackExtractor(SwedishIngredientParser ingredientParser)
    {
        _ingredientParser = ingredientParser;
    }

    public bool TryExtract(IDocument document, out Recipe? recipe, out string? remoteImageUrl)
    {
        recipe = null;
        remoteImageUrl = null;

        var title = ExtractMetaContent(document, "property", "og:title")
                    ?? document.QuerySelector("h1")?.TextContent?.Trim()
                    ?? document.Title?.Trim();

        title = CleanText(title);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var description = CleanText(
            ExtractMetaContent(document, "property", "og:description")
            ?? ExtractMetaContent(document, "name", "description"));

        var rawImage = ExtractMetaContent(document, "property", "og:image");
        if (!string.IsNullOrWhiteSpace(rawImage) &&
            Uri.TryCreate(rawImage.Trim(), UriKind.Absolute, out var imgUri) &&
            (imgUri.Scheme == Uri.UriSchemeHttp || imgUri.Scheme == Uri.UriSchemeHttps) &&
            SsrfValidator.IsSafeUri(imgUri, out _))
        {
            remoteImageUrl = imgUri.AbsoluteUri;
        }
        else
        {
            remoteImageUrl = null;
        }

        var ingredients = new List<Ingredient>();
        foreach (var selector in IngredientSelectors)
        {
            var nodes = document.QuerySelectorAll(selector);
            if (nodes.Length > 0)
            {
                int idx = 1;
                foreach (var node in nodes)
                {
                    var text = CleanText(node.InnerHtml);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        ingredients.Add(_ingredientParser.Parse(text, idx++));
                    }
                }
                if (ingredients.Count > 0)
                    break;
            }
        }

        if (ingredients.Count == 0)
        {
            foreach (var heading in document.QuerySelectorAll("h2, h3, h4"))
            {
                var hText = heading.TextContent?.Trim() ?? string.Empty;
                if (hText.Contains("ingrediens", StringComparison.OrdinalIgnoreCase))
                {
                    var nextList = heading.NextElementSibling;
                    while (nextList != null && nextList.TagName is not ("UL" or "OL"))
                    {
                        nextList = nextList.NextElementSibling;
                    }
                    if (nextList != null)
                    {
                        int idx = 1;
                        foreach (var li in nextList.QuerySelectorAll("li"))
                        {
                            var text = CleanText(li.InnerHtml);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                ingredients.Add(_ingredientParser.Parse(text, idx++));
                            }
                        }
                        if (ingredients.Count > 0)
                            break;
                    }
                }
            }
        }

        var instructions = new List<InstructionStep>();
        foreach (var selector in InstructionSelectors)
        {
            var nodes = document.QuerySelectorAll(selector);
            if (nodes.Length > 0)
            {
                int stepNum = 1;
                foreach (var node in nodes)
                {
                    var text = CleanText(node.TextContent);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        instructions.Add(new InstructionStep
                        {
                            StepNumber = stepNum++,
                            InstructionText = text
                        });
                    }
                }
                if (instructions.Count > 0)
                    break;
            }
        }

        if (instructions.Count == 0)
        {
            foreach (var heading in document.QuerySelectorAll("h2, h3, h4"))
            {
                var hText = heading.TextContent?.Trim() ?? string.Empty;
                if (hText.Contains("gör så här", StringComparison.OrdinalIgnoreCase) ||
                    hText.Contains("tillagning", StringComparison.OrdinalIgnoreCase) ||
                    hText.Contains("instruktion", StringComparison.OrdinalIgnoreCase))
                {
                    var nextList = heading.NextElementSibling;
                    while (nextList != null && nextList.TagName is not ("OL" or "UL"))
                    {
                        nextList = nextList.NextElementSibling;
                    }
                    if (nextList != null)
                    {
                        int stepNum = 1;
                        foreach (var li in nextList.QuerySelectorAll("li"))
                        {
                            var text = CleanText(li.TextContent);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                instructions.Add(new InstructionStep
                                {
                                    StepNumber = stepNum++,
                                    InstructionText = text
                                });
                            }
                        }
                        if (instructions.Count > 0)
                            break;
                    }
                }
            }
        }

        if (ingredients.Count == 0 && instructions.Count == 0)
        {
            return false;
        }

        recipe = new Recipe
        {
            Title = title,
            Description = description,
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients = ingredients,
            Instructions = instructions
        };

        return true;
    }

    private static string? ExtractMetaContent(IDocument document, string attrName, string attrValue)
    {
        var meta = document.QuerySelector($"meta[{attrName}='{attrValue}']");
        return meta?.GetAttribute("content")?.Trim();
    }

    private static string? CleanText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var strippedHtml = Regex.Replace(input, @"<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(strippedHtml);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }
}

