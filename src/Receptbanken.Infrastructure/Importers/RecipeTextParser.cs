using System.Text.RegularExpressions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Infrastructure.Importers;

/// <summary>
/// Rule-based parser for structured or plain Swedish recipe text.
/// Extracts recipe title, servings, ingredients (via SwedishIngredientParser),
/// instruction steps, serving tips, and cooking times without requiring external AI services.
/// </summary>
public class RecipeTextParser
{
    private readonly SwedishIngredientParser _ingredientParser;

    // Headings indicating instruction sections
    private static readonly Regex InstructionHeaderRegex = new(
        @"^(?:##\s*|#\s*)?(?:gör så här|gör såhär|tillagning|instruktioner|så här gör du|beredning|arbetsgång|så gör du)(?:\s*:)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Headings indicating ingredient sections
    private static readonly Regex IngredientHeaderRegex = new(
        @"^(?:##\s*|#\s*)?(?:ingredienser|det här behöver du|vad du behöver|ingredienser\s*\(.*?\))(?:\s*:)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Numbered step detection: 1) text, 1. text, 1: text, Steg 1: text, [1] text
    private static readonly Regex NumberedStepRegex = new(
        @"^(?:(?:steg|step)\s*)?\[?(\d+)\]?[\.\)\:]\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Servings pattern within title or heading: e.g. "Ninas Köttfärssås, 4 p", "Pannkakor (4 port)", "Kladdkaka - 8 bitar"
    private static readonly Regex HeadingServingsRegex = new(
        @"(?:[\(,\-–]|\b)\s*(?:ca\.?\s*)?(\d+)(?:\s*[\-–]\s*\d+)?\s*(port(?:ioner)?|p(?:ersoner)?|st|bitar|kakor|bullar|glas)\b\s*\)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Standalone servings line: e.g. "4 portioner", "Portioner: 4", "Antal: 4 port", "4 p"
    private static readonly Regex StandaloneServingsRegex = new(
        @"^(?:(?:portioner|antal|ger)(?:\s*:)?\s*)?(?:ca\.?\s*)?(\d+)(?:\s*[\-–]\s*\d+)?\s*(port(?:ioner)?|p(?:ersoner)?|portioner|st|bitar|kakor|bullar|glas)\b$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Time pattern: e.g. "Tid: 30 min", "Tillagningstid: ca 45 min", "Aktiv tid: 20 min"
    private static readonly Regex TimeRegex = new(
        @"^(?:tid|tillagningstid|förberedelsetid|aktiv tid|koktid|stektid)(?:\s*:)?\s*(?:ca\.?\s*)?(\d+)\s*(?:[\-–]|\s+till\s+)?(\d+)?\s*(min(?:uter)?|tim(?:mar)?|h)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Serving tips or general notes: e.g. "Tips: Riv över parmesan innan servering.", "Serveringstips: ..."
    private static readonly Regex TipRegex = new(
        @"^(?:tips|serveringstips|servera med|tips!|servering)(?:\s*:|\s*!)\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Blog outro / closing conversational narrative
    private static readonly Regex NarrativeOutroRegex = new(
        @"^(?:lämna\s+(?:gärna\s+)?en\s+kommentar|nu\s+önskar\s+jag|smaklig\s+måltid|kram|hälsningar|dela\s+gärna|följ\s+mig|kommentera|hoppas\s+ni)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Known common culinary ingredients without leading numbers
    private static readonly Regex KnownIngredientWordsRegex = new(
        @"^(?:salt(?:\s*(?:och|&)\s*(?:svart|vit)?peppar)?|svartpeppar|vitpeppar|peppar|smör(?:\s+(?:till|att)\s+stek(?:a|ning)(?:\s+i)?)?|olivolja(?:\s+(?:till|att)\s+stek(?:a|ning)(?:\s+i)?)?|olja(?:\s+(?:till|att)\s+stek(?:a|ning)(?:\s+i)?)?|parmesanost|parmesan|riven\s+ost|färsk\s+basilika|färsk\s+persilja|persilja|dill|oregano|timjan|rosmarin|vatten|ströbröd|vetemjöl)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RecipeTextParser(SwedishIngredientParser ingredientParser)
    {
        _ingredientParser = ingredientParser;
    }

    public RecipeImportResult Parse(string? text, string? sourceUrl = null, string? remoteImageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.EmptyInput,
                "Klistra in recepttext innan du tolkar.");
        }

        if (text.Length > RecipeImportLimits.MaxRecipeTextLength)
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.InputTooLarge,
                $"Den inklistrade texten är för stor (max {RecipeImportLimits.MaxRecipeTextLength / 1024} kB).");
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .ToList();

        // 1. Identify line classification and collect components
        string? recipeTitle = null;
        int? detectedServings = null;
        string? detectedServingsUnit = null;
        int? cookTimeMinutes = null;
        int? prepTimeMinutes = null;
        var notes = new List<string>();
        var ingredients = new List<Ingredient>();
        var instructions = new List<InstructionStep>();

        bool inIngredientsSection = false;
        bool inInstructionsSection = false;
        bool seenNumberedSteps = false;
        string? currentGroup = null;

        int ingredientOrderIndex = 1;
        int instructionStepNumber = 1;

        // Candidate titles seen before ingredients
        var headingCandidates = new List<(string RawTitle, string CleanTitle, int? Servings, string? Unit, int LineIndex)>();

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Check for instruction header: "Gör så här:", etc.
            if (InstructionHeaderRegex.IsMatch(line))
            {
                inInstructionsSection = true;
                inIngredientsSection = false;
                currentGroup = null;
                continue;
            }

            // Check for ingredient header: "Ingredienser:", etc.
            if (IngredientHeaderRegex.IsMatch(line))
            {
                inIngredientsSection = true;
                inInstructionsSection = false;
                currentGroup = null;

                // Extract servings if present in header, e.g. "Ingredienser (4 portioner)"
                var headerServingsMatch = HeadingServingsRegex.Match(line);
                if (headerServingsMatch.Success && !detectedServings.HasValue)
                {
                    if (int.TryParse(headerServingsMatch.Groups[1].Value, out var s))
                    {
                        detectedServings = s;
                        detectedServingsUnit = NormalizeServingsUnit(headerServingsMatch.Groups[2].Value);
                    }
                }
                continue;
            }

            // Check for Tip: e.g. "Tips: Riv över parmesan..."
            var tipMatch = TipRegex.Match(line);
            if (tipMatch.Success)
            {
                var tipText = tipMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(tipText))
                {
                    notes.Add(line.StartsWith("Tips", StringComparison.OrdinalIgnoreCase) ? line : $"Tips: {tipText}");
                }
                if (inInstructionsSection)
                {
                    inInstructionsSection = false; // Tip marks completion of instructions
                }
                continue;
            }

            // Check for Time: e.g. "Tid: 30 min"
            var timeMatch = TimeRegex.Match(line);
            if (timeMatch.Success)
            {
                if (int.TryParse(timeMatch.Groups[1].Value, out var val))
                {
                    var unit = timeMatch.Groups[3].Value.ToLowerInvariant();
                    if (unit.StartsWith('t') || unit.Equals("h"))
                    {
                        val *= 60;
                    }

                    if (line.Contains("förbered", StringComparison.OrdinalIgnoreCase) || line.Contains("aktiv", StringComparison.OrdinalIgnoreCase))
                    {
                        prepTimeMinutes ??= val;
                    }
                    else
                    {
                        cookTimeMinutes ??= val;
                    }
                }
                continue;
            }

            // Check for Standalone Servings: e.g. "4 portioner"
            var standaloneServingsMatch = StandaloneServingsRegex.Match(line);
            if (standaloneServingsMatch.Success && !inInstructionsSection)
            {
                if (int.TryParse(standaloneServingsMatch.Groups[1].Value, out var s))
                {
                    detectedServings ??= s;
                    detectedServingsUnit ??= NormalizeServingsUnit(standaloneServingsMatch.Groups[2].Value);
                }
                continue;
            }

            // If we are in the instruction section:
            if (inInstructionsSection)
            {
                // Group header within instructions, e.g. "Sås:", "Montering:"
                if (IsSubGroupHeader(line))
                {
                    currentGroup = CleanGroupHeader(line);
                    continue;
                }

                var stepMatch = NumberedStepRegex.Match(line);
                if (stepMatch.Success)
                {
                    seenNumberedSteps = true;
                    var stepNum = int.TryParse(stepMatch.Groups[1].Value, out var parsedNum) ? parsedNum : instructionStepNumber++;
                    var textContent = stepMatch.Groups[2].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(textContent))
                    {
                        instructions.Add(new InstructionStep
                        {
                            StepNumber = stepNum,
                            InstructionText = textContent,
                            GroupHeader = currentGroup
                        });
                        instructionStepNumber = stepNum + 1;
                    }
                }
                else
                {
                    var cleanText = line.TrimStart('-', '•', '*').Trim();
                    if (NarrativeOutroRegex.IsMatch(cleanText) || (seenNumberedSteps && !line.StartsWith('-') && !line.StartsWith('•') && !line.StartsWith('*')))
                    {
                        // Ignore conversational closing text or unexpected unnumbered lines after numbered list
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(cleanText))
                    {
                        instructions.Add(new InstructionStep
                        {
                            StepNumber = instructionStepNumber++,
                            InstructionText = cleanText,
                            GroupHeader = currentGroup
                        });
                    }
                }
                continue;
            }

            // Numbered step appearing before an explicit instruction header: transition to instructions!
            var earlyStepMatch = NumberedStepRegex.Match(line);
            if (earlyStepMatch.Success)
            {
                inInstructionsSection = true;
                inIngredientsSection = false;
                seenNumberedSteps = true;
                currentGroup = null;

                var stepNum = int.TryParse(earlyStepMatch.Groups[1].Value, out var parsedNum) ? parsedNum : instructionStepNumber++;
                var textContent = earlyStepMatch.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(textContent))
                {
                    instructions.Add(new InstructionStep
                    {
                        StepNumber = stepNum,
                        InstructionText = textContent,
                        GroupHeader = currentGroup
                    });
                    instructionStepNumber = stepNum + 1;
                }
                continue;
            }

            // Check if line looks like an ingredient
            bool isIngredient = inIngredientsSection || IsIngredientCandidate(line);

            if (isIngredient)
            {
                // Subgroup within ingredients, e.g. "Fyllning:", "Pajdeg:"
                if (IsSubGroupHeader(line))
                {
                    currentGroup = CleanGroupHeader(line);
                    continue;
                }

                var cleanLine = line.TrimStart('-', '•', '*', '+').Trim();
                if (!string.IsNullOrWhiteSpace(cleanLine))
                {
                    var parsedIngredient = _ingredientParser.Parse(cleanLine, ingredientOrderIndex++, currentGroup);
                    if (!string.IsNullOrWhiteSpace(parsedIngredient.Name))
                    {
                        ingredients.Add(parsedIngredient);
                        inIngredientsSection = true; // Stay in ingredients
                    }
                }
                continue;
            }

            // If not in ingredients or instructions, inspect line as potential title/heading
            if (!inIngredientsSection && !inInstructionsSection)
            {
                var isExplicitHeading = line.StartsWith("##") || line.StartsWith('#');
                var headingText = line.TrimStart('#').Trim();

                var headingServingsMatch = HeadingServingsRegex.Match(headingText);
                int? headingServings = null;
                string? headingUnit = null;
                string cleanTitle = headingText;

                if (headingServingsMatch.Success)
                {
                    if (int.TryParse(headingServingsMatch.Groups[1].Value, out var s))
                    {
                        headingServings = s;
                        headingUnit = NormalizeServingsUnit(headingServingsMatch.Groups[2].Value);
                    }
                    cleanTitle = headingText[..headingServingsMatch.Index].Trim().TrimEnd(',', '-', '–', '(').Trim();
                }

                if (!string.IsNullOrWhiteSpace(cleanTitle))
                {
                    headingCandidates.Add((line, cleanTitle, headingServings, headingUnit, i));
                }
            }
        }

        // 2. Resolve Recipe Title & Servings from candidates
        if (headingCandidates.Count > 0)
        {
            // Pick candidate with explicit servings or the one closest to ingredients
            var candidateWithServings = headingCandidates.LastOrDefault(c => c.Servings.HasValue);
            var bestCandidate = candidateWithServings.CleanTitle != null
                ? candidateWithServings
                : headingCandidates.Last(); // Closest heading to recipe body

            recipeTitle = bestCandidate.CleanTitle;

            if (bestCandidate.Servings.HasValue && !detectedServings.HasValue)
            {
                detectedServings = bestCandidate.Servings;
                detectedServingsUnit = bestCandidate.Unit;
            }
        }

        // Conservative validation: Del F
        if (ingredients.Count == 0 && instructions.Count == 0)
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.NoRecipeData,
                "Inget recept kunde identifieras i den inklistrade texten. Kontrollera att texten innehåller ingredienser eller instruktioner, eller mata in receptet manuellt.");
        }

        if (string.IsNullOrWhiteSpace(recipeTitle))
        {
            // Fallback to first line or generic title if title was omitted
            recipeTitle = headingCandidates.FirstOrDefault().CleanTitle ?? "Importerat recept";
        }

        var recipe = new Recipe
        {
            Title = recipeTitle,
            Servings = detectedServings ?? 4,
            ServingsUnit = detectedServingsUnit ?? "portioner",
            CookTimeMinutes = cookTimeMinutes,
            PrepTimeMinutes = prepTimeMinutes,
            Notes = notes.Count > 0 ? string.Join("\n\n", notes) : null,
            Ingredients = ingredients,
            Instructions = instructions,
            SourceUrl = sourceUrl
        };

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            recipe.SourceHost = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        }

        return new RecipeImportResult
        {
            Success = true,
            FailureReason = RecipeImportFailureReason.None,
            DraftRecipe = recipe,
            RemoteImageUrl = remoteImageUrl,
            HasImageProperty = !string.IsNullOrWhiteSpace(remoteImageUrl)
        };
    }

    private static bool IsIngredientCandidate(string line)
    {
        var clean = line.TrimStart('-', '•', '*', '+').Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return false;

        // Starts with digits or fractions
        if (Regex.IsMatch(clean, @"^(?:ca\.?\s*|cirka\s*)?(?:\d+|[½¼¾⅓⅔⅛])"))
            return true;

        // Known culinary ingredient without quantity (e.g. "salt och peppar")
        if (KnownIngredientWordsRegex.IsMatch(clean))
            return true;

        // Bulleted short text (e.g. "- smör", "• lök")
        if ((line.StartsWith('-') || line.StartsWith('•') || line.StartsWith('*')) &&
            clean.Length < 60 &&
            !clean.Contains('.') &&
            !InstructionHeaderRegex.IsMatch(clean))
        {
            return true;
        }

        return false;
    }

    private static bool IsSubGroupHeader(string line)
    {
        var clean = line.TrimStart('#').Trim();
        return clean.EndsWith(':') && clean.Length <= 40 && !clean.Contains('.') && !Regex.IsMatch(clean, @"\d");
    }

    private static string CleanGroupHeader(string line)
    {
        return line.TrimStart('#').Trim().TrimEnd(':').Trim();
    }

    private static string NormalizeServingsUnit(string unit)
    {
        var lower = unit.Trim().ToLowerInvariant();
        if (lower.StartsWith('p'))
            return "portioner";
        if (lower.StartsWith("port"))
            return "portioner";
        if (lower.StartsWith("st"))
            return "st";
        if (lower.StartsWith("bit"))
            return "bitar";
        if (lower.StartsWith("kak"))
            return "kakor";
        if (lower.StartsWith("bull"))
            return "bullar";
        if (lower.StartsWith("glas"))
            return "glas";

        return unit;
    }
}
