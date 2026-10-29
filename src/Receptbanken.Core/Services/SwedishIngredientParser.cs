using System.Globalization;
using System.Text.RegularExpressions;
using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

public class SwedishIngredientParser
{
    private static readonly Dictionary<char, decimal> UnicodeFractions = new()
    {
        ['½'] = 0.5m,
        ['¼'] = 0.25m,
        ['¾'] = 0.75m,
        ['⅓'] = 0.33m,
        ['⅔'] = 0.67m,
        ['⅛'] = 0.125m
    };

    private static readonly HashSet<string> KnownUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "dl", "msk", "tsk", "krm", "l", "cl", "ml",
        "g", "kg", "hg",
        "st", "stycken", "styck",
        "klyfta", "klyftor",
        "kruka", "krukor",
        "burk", "burkar",
        "förp", "förpackning", "förpackningar",
        "paket", "pkt", "påse", "påsar",
        "skiva", "skivor",
        "nypa", "nypor",
        "blad", "kvist", "kvistar",
        "port", "portion", "portioner",
        "knippe", "ask", "askar", "flaska", "flaskor"
    };

    public Ingredient Parse(string rawLine, int orderIndex = 1, string? groupHeader = null)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return new Ingredient
            {
                OrderIndex = orderIndex,
                RawText = string.Empty,
                Name = string.Empty,
                GroupHeader = groupHeader
            };
        }

        var rawText = rawLine.Trim();
        var working = Regex.Replace(rawText, @"^(?:ca\.?|cirka)\s+", "", RegexOptions.IgnoreCase).Trim();

        decimal? quantity = null;
        decimal? quantityMax = null;
        string? unit = null;

        // 1. Try parsing leading quantity (range, mixed fraction, unicode fraction, or decimal)
        working = TryExtractQuantity(working, out quantity, out quantityMax);

        // 2. If a quantity was found, check if the next token is a known Swedish unit
        if (quantity.HasValue && !string.IsNullOrWhiteSpace(working))
        {
            var tokenMatch = Regex.Match(working, @"^([a-zA-ZåäöÅÄÖ]+)\.?(?:\s+|(?=\()|$)(.*)$");
            if (tokenMatch.Success)
            {
                var candidateUnit = tokenMatch.Groups[1].Value;
                if (KnownUnits.Contains(candidateUnit))
                {
                    unit = NormalizeUnit(candidateUnit);
                    working = tokenMatch.Groups[2].Value.Trim();
                }
                else
                {
                    var attachedUnitMatch = Regex.Match(
                        working,
                        @"^(dl|msk|tsk|krm|cl|ml|kg|hg|förp|pkt)\.?(?=[a-zA-ZåäöÅÄÖ]{2,})(.+)$",
                        RegexOptions.IgnoreCase);
                    if (attachedUnitMatch.Success)
                    {
                        unit = NormalizeUnit(attachedUnitMatch.Groups[1].Value);
                        working = attachedUnitMatch.Groups[2].Value.Trim();
                    }
                }
            }
        }

        // 3. Separate ingredient name and preparation note (e.g. "smör, smält")
        string namePart = working;
        string? preparation = null;

        var commaIndex = FindCommaOutsideParentheses(working);
        if (commaIndex > 0 && commaIndex < working.Length - 1)
        {
            namePart = working[..commaIndex].Trim();
            preparation = working[(commaIndex + 1)..].Trim();
        }

        // 4. Extract trailing parenthetical comment(s) from the ingredient name part
        ExtractTrailingParentheses(namePart, out var name, out var comment);

        if (string.IsNullOrWhiteSpace(name))
        {
            name = rawText;
        }

        return new Ingredient
        {
            OrderIndex = orderIndex,
            RawText = rawText,
            Quantity = quantity,
            QuantityMax = quantityMax,
            Unit = unit,
            Name = name,
            Comment = comment,
            Preparation = preparation,
            GroupHeader = groupHeader
        };
    }

    private static void ExtractTrailingParentheses(string input, out string name, out string? comment)
    {
        name = input.Trim();
        comment = null;

        var comments = new List<string>();

        while (true)
        {
            if (!name.EndsWith(')'))
                break;

            int depth = 0;
            int openParenIndex = -1;

            // Scan backwards to find the matching '(' for the trailing ')'
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == ')')
                {
                    depth++;
                }
                else if (name[i] == '(')
                {
                    depth--;
                    if (depth == 0)
                    {
                        openParenIndex = i;
                        break;
                    }
                }
            }

            // Unbalanced parentheses (e.g. missing '(')
            if (openParenIndex < 0 || depth != 0)
                break;

            // Entire string is wrapped in parentheses, e.g. "(valfritt)"
            // Do not strip into an empty name! Conservative approach.
            var candidateName = name[..openParenIndex].Trim();
            if (string.IsNullOrWhiteSpace(candidateName))
                break;

            var insideText = name.Substring(openParenIndex + 1, name.Length - openParenIndex - 2).Trim();

            // Empty parentheses e.g. "mjölk ()"
            // Conservative: do not treat empty parentheses as a comment. Keep as part of name.
            if (string.IsNullOrWhiteSpace(insideText))
                break;

            comments.Insert(0, insideText);
            name = candidateName;
        }

        if (comments.Count > 0)
        {
            comment = string.Join(", ", comments);
        }
    }

    private static string TryExtractQuantity(string input, out decimal? quantity, out decimal? quantityMax)
    {
        quantity = null;
        quantityMax = null;

        if (string.IsNullOrWhiteSpace(input))
            return input;

        // Range: e.g. "1-2 klyftor", "1,5 - 2 dl"
        var rangeMatch = Regex.Match(input, @"^(\d+(?:[.,]\d+)?)\s*[-–]\s*(\d+(?:[.,]\d+)?)(?:\s+|(?=[a-zA-ZåäöÅÄÖ])|$)(.*)$");
        if (rangeMatch.Success &&
            TryParseDecimal(rangeMatch.Groups[1].Value, out var minVal) &&
            TryParseDecimal(rangeMatch.Groups[2].Value, out var maxVal))
        {
            quantity = minVal;
            quantityMax = maxVal;
            return rangeMatch.Groups[3].Value.Trim();
        }

        // Mixed fraction with unicode: e.g. "1 ½ dl" or "1½ dl"
        var mixedUnicodeMatch = Regex.Match(input, @"^(\d+)\s*([½¼¾⅓⅔⅛])(?:\s+|(?=[a-zA-ZåäöÅÄÖ])|$)(.*)$");
        if (mixedUnicodeMatch.Success &&
            int.TryParse(mixedUnicodeMatch.Groups[1].Value, out var wholePart) &&
            UnicodeFractions.TryGetValue(mixedUnicodeMatch.Groups[2].Value[0], out var fracPart))
        {
            quantity = wholePart + fracPart;
            return mixedUnicodeMatch.Groups[3].Value.Trim();
        }

        // Mixed slash fraction: e.g. "1 1/2 dl"
        var mixedSlashMatch = Regex.Match(input, @"^(\d+)\s+(\d+)/(\d+)(?:\s+|(?=[a-zA-ZåäöÅÄÖ])|$)(.*)$");
        if (mixedSlashMatch.Success &&
            int.TryParse(mixedSlashMatch.Groups[1].Value, out var wholeSlash) &&
            int.TryParse(mixedSlashMatch.Groups[2].Value, out var num) &&
            int.TryParse(mixedSlashMatch.Groups[3].Value, out var den) && den > 0)
        {
            quantity = wholeSlash + Math.Round((decimal)num / den, 2);
            return mixedSlashMatch.Groups[4].Value.Trim();
        }

        // Standalone unicode fraction: e.g. "½ tsk salt"
        var unicodeMatch = Regex.Match(input, @"^([½¼¾⅓⅔⅛])(?:\s+|(?=[a-zA-ZåäöÅÄÖ])|$)(.*)$");
        if (unicodeMatch.Success &&
            UnicodeFractions.TryGetValue(unicodeMatch.Groups[1].Value[0], out var singleUnicode))
        {
            quantity = singleUnicode;
            return unicodeMatch.Groups[2].Value.Trim();
        }

        // Standalone slash fraction: e.g. "1/2 tsk salt"
        var slashMatch = Regex.Match(input, @"^(\d+)/(\d+)(?:\s+|(?=[a-zA-ZåäöÅÄÖ])|$)(.*)$");
        if (slashMatch.Success &&
            int.TryParse(slashMatch.Groups[1].Value, out var sNum) &&
            int.TryParse(slashMatch.Groups[2].Value, out var sDen) && sDen > 0)
        {
            quantity = Math.Round((decimal)sNum / sDen, 2);
            return slashMatch.Groups[3].Value.Trim();
        }

        // Standard integer or decimal (comma or dot): e.g. "1,5 dl", "200 g", "2m" (if space separated or attached to unit)
        var decMatch = Regex.Match(input, @"^(\d+(?:[.,]\d+)?)(?:\s+|(?=[a-zA-ZåäöÅÄÖ])|$)(.*)$");
        if (decMatch.Success && TryParseDecimal(decMatch.Groups[1].Value, out var decVal))
        {
            quantity = decVal;
            return decMatch.Groups[2].Value.Trim();
        }

        return input;
    }

    private static bool TryParseDecimal(string text, out decimal result)
    {
        var normalized = text.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static int FindCommaOutsideParentheses(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeUnit(string unit)
    {
        var lower = unit.ToLowerInvariant();
        return lower switch
        {
            "stycken" or "styck" => "st",
            "förpackning" or "förpackningar" => "förp",
            "pkt" => "paket",
            "portion" or "portioner" => "port",
            _ => lower
        };
    }
}

