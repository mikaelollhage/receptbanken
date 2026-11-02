using System.Text.RegularExpressions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Infrastructure.Importers;

/// <summary>
/// Rule-based parser for structured or plain Swedish recipe text.
/// Extracts recipe title, servings, ingredients (via SwedishIngredientParser),
/// instruction steps, serving tips, and cooking times without requiring external AI services.
/// Identifies coherent recipe boundaries, filters page noise, and strips commercial insertions.
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
        @"^(?:##\s*|#\s*)?(?:ingredienser|det här behöver du|vad du behöver|du behöver|detta behöver du|det behöver du)(?:\s+(?:för|till)\s+.*?)?(?:\s*\(.*?\))?(?:\s*:)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Common culinary action verbs in Swedish to distinguish real instructions from general advice
    private static readonly Regex CulinaryActionVerbsRegex = new(
        @"\b(?:börja|börjar|börjas|starta|koka|kokar|koker|kokas|stek|steka|steker|steks|stekas|ugn|ugnen|bryn|bryna|bryner|bryns|vispa|vispar|vispas|blanda|blandar|blandas|skär|skära|skärs|hacka|hackar|hackas|häll|hälla|häller|hälls|fräs|fräsa|fräser|fräses|smaka|smakar|smakas|smaka av|sjud|sjuda|sjuder|puttra|puttrar|grädda|gräddar|gräddas|värm|värma|värmer|värms|forma|formar|formas|rör|röra|rörs|tillsätt|tillsätta|tillsätter|tillsätts|strö|strör|strös|pensla|penslar|penslas|skiva|skivar|skivas|tärna|tärnar|tärnas|strimla|strimlar|strimlas|mosa|mosar|mosas|pressa|pressar|pressas|lägg|lägga|lägger|läggs|sätt|sätta|sätter|sätts|toppa|toppar|toppas|krydda|kryddar|kryddas|fördela|fördelar|fördelas|smält|smälta|smälter|smälts|stekpanna|kastrull|ugnsform|servera|serverar|serveras|skala|skalar|skalas|dela|delar|delas|skölj|skölja|sköljer|sköljs|rosta|rostar|rostas|mixa|mixar|mixas|kyl|kyla|kyls|smula|smular|smulas|hyvla|hyvlar|hyvlas|marinera|marinerar|marineras|ringla|ringlar|ringlas|låt|låta|låter|vänd|vända|vänder|vänds|ställ|ställa|ställer|ställs|knåda|knådar|knådas|jäs|jäsa|jäser|kavla|kavlar|kavlas|grilla|grillar|grillas|plocka|plockar|garnera|garnerar|garneras|doppa|doppar|doppas|öppna|öppnar|riv|riva|river|rivs|avsluta|avslutar)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Numbered step detection: 1) text, 1. text, 1: text, Steg 1: text, [1] text
    private static readonly Regex NumberedStepRegex = new(
        @"^(?:(?:steg|step)\s*)?\[?(\d+)\]?[\.\)\:]\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Servings pattern within title or heading: e.g. "Ninas Köttfärssås, 4 p", "Pannkakor (4 port)", "Kladdkaka - 8 bitar", "Chokladmuffins – 12 stycken", "Bananbröd – 1 limpa"
    private static readonly Regex HeadingServingsRegex = new(
        @"(?:[\(,\-–—]|\b)\s*(?:ca\.?\s*)?(\d+)(?:\s*[\-–—]\s*\d+)?\s*(port(?:ioner)?|p(?:ersoner)?|st(?:ycken)?|styck|bitar?|kakor?|kaka|bullar?|bulle|glas|limp(?:a|or)?)\b\s*\)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Standalone servings line: e.g. "4 portioner", "Portioner: 4", "Antal: 4 port", "4 p", "12 stycken"
    private static readonly Regex StandaloneServingsRegex = new(
        @"^(?:(?:portioner|antal|ger)(?:\s*:)?\s*)?(?:ca\.?\s*)?(\d+)(?:\s*[\-–—]\s*\d+)?\s*(port(?:ioner)?|p(?:ersoner)?|portioner|st(?:ycken)?|styck|bitar?|kakor?|kaka|bullar?|bulle|glas|limp(?:a|or)?)\b$",
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
        @"^(?:lämna\s+(?:gärna\s+)?en\s+kommentar|nu\s+önskar\s+jag|smaklig\s+måltid|kram(?:mar)?(?:\s+på\s+er)?|hälsningar|dela\s+gärna|följ\s+mig|kommentera|hoppas\s+ni|glöm\s+inte\s+att\s+följa|läs\s+(?:också|även|mer)|prenumerera)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Known common culinary ingredients without leading numbers
    private static readonly Regex KnownIngredientWordsRegex = new(
        @"^(?:salt(?:\s*(?:och|&)\s*(?:svart|vit)?peppar)?|svartpeppar|vitpeppar|peppar|smör(?:\s+(?:till|att)\s+stek(?:a|ning)(?:\s+i)?)?|olivolja(?:\s+(?:till|att)\s+stek(?:a|ning)(?:\s+i)?)?|olja(?:\s+(?:till|att)\s+stek(?:a|ning)(?:\s+i)?)?|parmesanost|parmesan|riven\s+ost|färsk\s+basilika|färsk\s+persilja|persilja|dill|oregano|timjan|rosmarin|vatten|ströbröd|vetemjöl)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Obvious standalone noise lines
    private static readonly Regex StandaloneNoiseRegex = new(
        @"^(?:annons|ad|sponsrat(?:\s+inlägg)?|i\s+samarbete\s+med|reklam|betalt\s+samarbete|svg|icon|img|bild|dela(?:\s+på\s+\w+)?|share|pinna|skriv\s+ut(?:\s+recept)?|spara(?:\s+recept)?|betygsätt|betyg\s*:\s*\d+|läs\s+(?:även|mer|också)|relaterade\s+recept|fler\s+recept|prenumerera)(?:\s*:.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Social sharing and noise snippets that can terminate or be ignored in instruction parsing
    private static readonly Regex SocialOrNoiseRegex = new(
        @"(?:dela\s+på\s+|share|pinna|skriv\s+ut|spara\s+recept|betygsätt|följ\s+oss|gillade\s+du)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Promotional clauses inside instructions (e.g. ", tips att använda denna köttfärshackare ... rabattkod: NINAJUL35", ", glöm inte att använda min rabattkod...")
    private static readonly Regex AdClauseRegex = new(
        @"(?:,\s*|\s*-\s*|\s*\(\s*)?(?:tips(?:\s+att)?\s+(?:använda|köpa)|glöm inte att\s+använda\s+[^,.)]*?)?(?:rabattkod|kampanjkod|adlink|annonslänk|annons\b)[^,.)]*(?:,\s*|\s*-\s*|\s*\)\s*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Standalone discount code sentences (e.g. "Använd koden NINA20 för 20% rabatt...")
    private static readonly Regex AdSentenceRegex = new(
        @"(?:(?:använd(?:\s+min)?\s+kod|rabattkod|kampanjkod)\s*:[^.)]+[.)]?|(?:^|\.\s*)glöm inte att\s+.*?(?:rabattkod|kampanjkod)[^.)]*[.)]?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RecipeTextParser(SwedishIngredientParser ingredientParser)
    {
        _ingredientParser = ingredientParser;
    }

    /// <summary>
    /// Parses plain or HTML-extracted Swedish recipe text into a structured RecipeImportResult.
    /// Work is factored into discrete, maintainable stages:
    /// validation, line normalization, section identification, metadata extraction,
    /// ingredient collection, instruction parsing, and plausibility verification.
    /// </summary>
    public RecipeImportResult Parse(string? text, string? sourceUrl = null, string? remoteImageUrl = null)
    {
        var validationFailure = ValidateInput(text, out var rawText);
        if (validationFailure != null)
            return validationFailure;

        var lines = NormalizeAndFilterLines(rawText!);
        if (lines.Count == 0)
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.NoRecipeData,
                "Inget recept kunde identifieras i den inklistrade texten. Kontrollera att texten innehåller ingredienser eller instruktioner, eller mata in receptet manuellt.");
        }

        var bounds = IdentifySectionBounds(lines);
        var metadata = ExtractMetadata(lines, bounds);
        var ingredients = ExtractIngredients(lines, bounds, metadata);
        var (instructions, notes, hasMultipleRecipes) = ExtractInstructionsAndNotes(lines, bounds, metadata);

        return ValidateAndBuildResult(
            metadata,
            ingredients,
            instructions,
            notes,
            bounds,
            hasMultipleRecipes,
            sourceUrl,
            remoteImageUrl);
    }

    #region 1. Validation and Line Normalization

    private sealed record ParsedLine(
        string Text,
        int OriginalIndex,
        int LineIndex,
        bool HasPrecedingBlankLine);

    private static RecipeImportResult? ValidateInput(string? text, out string? rawText)
    {
        rawText = null;
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

        rawText = text;
        return null;
    }

    private static List<ParsedLine> NormalizeAndFilterLines(string text)
    {
        var rawLines = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .ToList();

        var lines = new List<ParsedLine>();
        bool sawBlankSinceLastLine = false;

        for (int i = 0; i < rawLines.Count; i++)
        {
            var l = rawLines[i];
            if (string.IsNullOrWhiteSpace(l))
            {
                sawBlankSinceLastLine = true;
                continue;
            }

            if (StandaloneNoiseRegex.IsMatch(l))
            {
                continue;
            }

            lines.Add(new ParsedLine(l, i, lines.Count, sawBlankSinceLastLine));
            sawBlankSinceLastLine = false;
        }

        return lines;
    }

    #endregion

    #region 2. Section Bounds Identification

    private sealed record RecipeSectionBounds
    {
        public int? TitleIndex { get; init; }
        public string? TitleText { get; init; }
        public int? Servings { get; init; }
        public string? ServingsUnit { get; init; }
        public int? IngredientHeaderIndex { get; init; }
        public int IngredientBlockStart { get; init; } = -1;
        public int IngredientBlockEnd { get; init; } = -1;
        public int? InstructionHeaderIndex { get; init; }
        public int InstructionBlockStart { get; init; } = -1;
    }

    private static RecipeSectionBounds IdentifySectionBounds(IReadOnlyList<ParsedLine> lines)
    {
        int instructionHeaderIdx = -1;
        int firstNumberedStepIdx = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Text;
            if (InstructionHeaderRegex.IsMatch(l))
            {
                instructionHeaderIdx = i;
                break;
            }
            if (firstNumberedStepIdx < 0 && NumberedStepRegex.IsMatch(l))
            {
                var match = NumberedStepRegex.Match(l);
                if (match.Success && match.Groups[1].Value == "1")
                {
                    firstNumberedStepIdx = i;
                }
            }
        }

        int instructionsEffectiveStart = instructionHeaderIdx >= 0 ? instructionHeaderIdx : firstNumberedStepIdx;

        int explicitIngHeaderIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (instructionsEffectiveStart >= 0 && i >= instructionsEffectiveStart)
                break;

            if (IngredientHeaderRegex.IsMatch(lines[i].Text))
            {
                explicitIngHeaderIdx = i;
                break;
            }
        }

        int ingBlockStart = -1;
        int ingBlockEnd = -1;
        int instBlockStart = -1;
        int? detectedServings = null;
        string? detectedServingsUnit = null;

        if (explicitIngHeaderIdx >= 0)
        {
            var hServ = HeadingServingsRegex.Match(lines[explicitIngHeaderIdx].Text);
            if (hServ.Success && int.TryParse(hServ.Groups[1].Value, out var s))
            {
                detectedServings = s;
                detectedServingsUnit = NormalizeServingsUnit(hServ.Groups[2].Value);
            }
        }

        if (explicitIngHeaderIdx >= 0)
        {
            // Case 1: Explicit ingredient header exists
            if (instructionsEffectiveStart >= 0)
            {
                ingBlockStart = explicitIngHeaderIdx + 1;
                ingBlockEnd = instructionsEffectiveStart - 1;
                instBlockStart = instructionsEffectiveStart;
            }
            else if (explicitIngHeaderIdx + 1 < lines.Count)
            {
                // Ingredient header exists without an explicit instruction header:
                // detect transition from ingredients into unnumbered instruction sentences
                FindUnheadedTransition(lines, explicitIngHeaderIdx + 1, out ingBlockStart, out ingBlockEnd, out instBlockStart);
                if (ingBlockStart < 0)
                {
                    ingBlockStart = explicitIngHeaderIdx + 1;
                    ingBlockEnd = lines.Count - 1;
                }
            }
        }
        else if (instructionsEffectiveStart > 0)
        {
            // Case 2: Explicit instruction header or numbered step 1 exists, but no ingredient header
            instBlockStart = instructionsEffectiveStart;

            int scanIdx = instructionsEffectiveStart - 1;
            int lastIngIdx = -1;
            int firstIngIdx = -1;

            while (scanIdx >= 0)
            {
                var candidate = lines[scanIdx].Text;

                if (TipRegex.IsMatch(candidate) || TimeRegex.IsMatch(candidate))
                {
                    scanIdx--;
                    continue;
                }

                if (IsTitleOrHeading(candidate))
                {
                    break;
                }

                if (scanIdx == 0 && !IsDefiniteIngredientLine(candidate))
                {
                    break;
                }

                if (IsIngredientLine(candidate))
                {
                    if (lastIngIdx < 0) lastIngIdx = scanIdx;
                    firstIngIdx = scanIdx;
                    scanIdx--;
                }
                else
                {
                    break;
                }
            }

            if (firstIngIdx >= 0 && lastIngIdx >= firstIngIdx)
            {
                ingBlockStart = firstIngIdx;
                ingBlockEnd = lastIngIdx;
            }
        }
        else
        {
            // Case 3: Neither explicit instruction header nor numbered step was found (headerless recipe, e.g. Test 14, 20, 20B, 22)
            int candIngStart = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsDefiniteIngredientLine(lines[i].Text))
                {
                    candIngStart = i;
                    break;
                }
            }

            if (candIngStart >= 0)
            {
                FindUnheadedTransition(lines, candIngStart, out ingBlockStart, out ingBlockEnd, out instBlockStart);
            }
        }

        return new RecipeSectionBounds
        {
            IngredientHeaderIndex = explicitIngHeaderIdx >= 0 ? explicitIngHeaderIdx : null,
            IngredientBlockStart = ingBlockStart,
            IngredientBlockEnd = ingBlockEnd,
            InstructionHeaderIndex = instructionHeaderIdx >= 0 ? instructionHeaderIdx : null,
            InstructionBlockStart = instBlockStart,
            Servings = detectedServings,
            ServingsUnit = detectedServingsUnit
        };
    }

    private static void FindUnheadedTransition(
        IReadOnlyList<ParsedLine> lines,
        int candIngStart,
        out int ingBlockStart,
        out int ingBlockEnd,
        out int instBlockStart)
    {
        ingBlockStart = -1;
        ingBlockEnd = -1;
        instBlockStart = -1;

        int candIngEnd = candIngStart - 1;
        for (int i = candIngStart; i < lines.Count; i++)
        {
            var l = lines[i].Text;
            if (IsDefiniteIngredientLine(l))
            {
                candIngEnd = i;
            }
            else if (IsIngredientLine(l))
            {
                // Stop if line is preceded by blank line and represents an instruction sentence
                if (lines[i].HasPrecedingBlankLine && (IsInstructionSentence(l) || IsSentenceLikeInstructionCandidate(l)))
                {
                    break;
                }
                candIngEnd = i;
            }
            else
            {
                break;
            }
        }

        if (candIngEnd < candIngStart)
            return;

        int candInstStart = -1;
        int instCount = 0;
        int culinaryInstCount = 0;

        for (int i = candIngEnd + 1; i < lines.Count; i++)
        {
            var l = lines[i].Text;
            if (IsInstructionSentence(l))
            {
                if (candInstStart < 0) candInstStart = i;
                instCount++;
                culinaryInstCount++;
            }
            else if (IsSentenceLikeInstructionCandidate(l))
            {
                if (candInstStart < 0) candInstStart = i;
                instCount++;
            }
            else if (instCount > 0)
            {
                break;
            }
            else
            {
                break;
            }
        }

        if (instCount >= 2 && culinaryInstCount >= 1 && candInstStart >= 0)
        {
            ingBlockStart = candIngStart;
            ingBlockEnd = candIngEnd;
            instBlockStart = candInstStart;
        }
        else
        {
            ingBlockStart = candIngStart;
            ingBlockEnd = candIngEnd;
        }
    }

    #endregion

    #region 3. Metadata Extraction

    private sealed record RecipeMetadata(
        string Title,
        int? Servings,
        string? ServingsUnit,
        int? CookTimeMinutes,
        int? PrepTimeMinutes,
        int? TitleLineIndex);

    private static RecipeMetadata ExtractMetadata(
        IReadOnlyList<ParsedLine> lines,
        RecipeSectionBounds bounds)
    {
        string? recipeTitle = null;
        int? titleLineIdx = null;
        int? detectedServings = bounds.Servings;
        string? detectedServingsUnit = bounds.ServingsUnit;

        int titleSearchCeiling = bounds.IngredientBlockStart >= 0
            ? bounds.IngredientBlockStart
            : (bounds.InstructionBlockStart >= 0 ? bounds.InstructionBlockStart : lines.Count);

        for (int i = titleSearchCeiling - 1; i >= 0; i--)
        {
            var l = lines[i].Text;
            if (IngredientHeaderRegex.IsMatch(l))
            {
                var hServ = HeadingServingsRegex.Match(l);
                if (hServ.Success && !detectedServings.HasValue && int.TryParse(hServ.Groups[1].Value, out var s))
                {
                    detectedServings = s;
                    detectedServingsUnit = NormalizeServingsUnit(hServ.Groups[2].Value);
                }
                continue;
            }

            if (TipRegex.IsMatch(l) || TimeRegex.IsMatch(l))
                continue;

            var cleanHeading = l.TrimStart('#').Trim();
            var matchServings = HeadingServingsRegex.Match(cleanHeading);
            if (matchServings.Success)
            {
                if (!detectedServings.HasValue && int.TryParse(matchServings.Groups[1].Value, out var s))
                {
                    detectedServings = s;
                    detectedServingsUnit = NormalizeServingsUnit(matchServings.Groups[2].Value);
                }
                var clean = cleanHeading[..matchServings.Index].Trim().TrimEnd(',', '-', '–', '—', '(', ')', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    recipeTitle = clean;
                    titleLineIdx = i;
                    break;
                }
            }

            if (l.StartsWith("##") || l.StartsWith('#'))
            {
                var clean = cleanHeading;
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    recipeTitle = clean;
                    titleLineIdx = i;
                    break;
                }
            }

            if (i == titleSearchCeiling - 1 && !IsDefiniteIngredientLine(l) && l.Length < 80 && !l.Contains('.'))
            {
                recipeTitle = l;
                titleLineIdx = i;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(recipeTitle))
        {
            recipeTitle = lines.FirstOrDefault()?.Text?.TrimStart('#').Trim() ?? "Importerat recept";
            titleLineIdx = 0;
            if (recipeTitle.Length > 80)
            {
                recipeTitle = "Importerat recept";
            }
        }

        if (!string.IsNullOrWhiteSpace(recipeTitle))
        {
            var titleServingsMatch = HeadingServingsRegex.Match(recipeTitle);
            if (titleServingsMatch.Success)
            {
                if (!detectedServings.HasValue && int.TryParse(titleServingsMatch.Groups[1].Value, out var s))
                {
                    detectedServings = s;
                    detectedServingsUnit = NormalizeServingsUnit(titleServingsMatch.Groups[2].Value);
                }
                var clean = recipeTitle[..titleServingsMatch.Index].Trim().TrimEnd(',', '-', '–', '—', '(', ')', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    recipeTitle = clean;
                }
            }
        }

        int? cookTimeMinutes = null;
        int? prepTimeMinutes = null;

        for (int i = 0; i < lines.Count; i++)
        {
            if (bounds.InstructionBlockStart >= 0 && i >= bounds.InstructionBlockStart)
                break;

            var l = lines[i].Text;
            var timeMatch = TimeRegex.Match(l);
            if (timeMatch.Success && int.TryParse(timeMatch.Groups[1].Value, out var val))
            {
                var unit = timeMatch.Groups[3].Value.ToLowerInvariant();
                if (unit.StartsWith('t') || unit.Equals("h")) val *= 60;
                if (l.Contains("förbered", StringComparison.OrdinalIgnoreCase) || l.Contains("aktiv", StringComparison.OrdinalIgnoreCase))
                    prepTimeMinutes ??= val;
                else
                    cookTimeMinutes ??= val;
            }

            var standServMatch = StandaloneServingsRegex.Match(l);
            if (standServMatch.Success && !detectedServings.HasValue && int.TryParse(standServMatch.Groups[1].Value, out var s))
            {
                detectedServings = s;
                detectedServingsUnit = NormalizeServingsUnit(standServMatch.Groups[2].Value);
            }
        }

        return new RecipeMetadata(
            recipeTitle,
            detectedServings,
            detectedServingsUnit,
            cookTimeMinutes,
            prepTimeMinutes,
            titleLineIdx);
    }

    #endregion

    #region 4. Ingredient Extraction

    private List<Ingredient> ExtractIngredients(
        IReadOnlyList<ParsedLine> lines,
        RecipeSectionBounds bounds,
        RecipeMetadata metadata)
    {
        var ingredients = new List<Ingredient>();
        string? currentIngGroup = null;
        int ingredientOrder = 1;

        int start = bounds.IngredientBlockStart;
        int end = bounds.IngredientBlockEnd;

        // Ensure ingredient block does not include or precede titleLineIdx
        if (metadata.TitleLineIndex.HasValue && start >= 0 && start <= metadata.TitleLineIndex.Value)
        {
            start = metadata.TitleLineIndex.Value + 1;
        }

        if (start >= 0 && end >= start)
        {
            for (int i = start; i <= end && i < lines.Count; i++)
            {
                if (metadata.TitleLineIndex.HasValue && i == metadata.TitleLineIndex.Value)
                    continue;

                var l = lines[i].Text;
                if (string.IsNullOrWhiteSpace(l) || IngredientHeaderRegex.IsMatch(l))
                    continue;

                if (!string.IsNullOrWhiteSpace(metadata.Title) && l.Trim().Equals(metadata.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsSubGroupHeader(l))
                {
                    currentIngGroup = CleanGroupHeader(l);
                    continue;
                }

                var cleanLine = l.TrimStart('-', '•', '*', '+').Trim();
                if (!string.IsNullOrWhiteSpace(cleanLine))
                {
                    var parsed = _ingredientParser.Parse(cleanLine, ingredientOrder++, currentIngGroup);
                    if (!string.IsNullOrWhiteSpace(parsed.Name))
                    {
                        ingredients.Add(parsed);
                    }
                }
            }
        }

        return ingredients;
    }

    #endregion

    #region 5. Instruction and Tip Extraction

    private static (List<InstructionStep> Instructions, List<string> Notes, bool HasMultipleRecipes)
        ExtractInstructionsAndNotes(
            IReadOnlyList<ParsedLine> lines,
            RecipeSectionBounds bounds,
            RecipeMetadata metadata)
    {
        var instructions = new List<InstructionStep>();
        var notes = new List<string>();
        string? currentInstGroup = null;
        int instructionStepNumber = 1;
        bool hasMultipleRecipes = false;

        if (bounds.InstructionBlockStart >= 0)
        {
            int instLoopStart = bounds.InstructionBlockStart;
            if (bounds.InstructionHeaderIndex.HasValue && instLoopStart == bounds.InstructionHeaderIndex.Value)
            {
                instLoopStart++; // skip header line itself
            }

            for (int i = instLoopStart; i < lines.Count; i++)
            {
                var l = lines[i].Text;
                if (string.IsNullOrWhiteSpace(l))
                    continue;

                var tipMatch = TipRegex.Match(l);
                if (tipMatch.Success)
                {
                    var tipText = tipMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(tipText))
                    {
                        notes.Add(l.StartsWith("Tips", StringComparison.OrdinalIgnoreCase) ? l : $"Tips: {tipText}");
                    }
                    continue;
                }

                if (NarrativeOutroRegex.IsMatch(l) || SocialOrNoiseRegex.IsMatch(l) || StandaloneNoiseRegex.IsMatch(l))
                {
                    break;
                }

                if (notes.Count > 0 && !NumberedStepRegex.IsMatch(l))
                {
                    break;
                }

                if (instructions.Count > 0)
                {
                    if (HeadingServingsRegex.IsMatch(l) ||
                        (l.StartsWith('#') && l.Length < 80) ||
                        IngredientHeaderRegex.IsMatch(l) ||
                        InstructionHeaderRegex.IsMatch(l))
                    {
                        hasMultipleRecipes = true;
                        break;
                    }
                }

                if (IsSubGroupHeader(l))
                {
                    currentInstGroup = CleanGroupHeader(l);
                    continue;
                }

                var stepMatch = NumberedStepRegex.Match(l);
                if (stepMatch.Success)
                {
                    var parsedStepNum = int.TryParse(stepMatch.Groups[1].Value, out var num) ? num : instructionStepNumber++;
                    var rawStepText = stepMatch.Groups[2].Value.Trim();
                    var cleanedStepText = CleanInstructionStepText(rawStepText);

                    if (!string.IsNullOrWhiteSpace(cleanedStepText))
                    {
                        instructions.Add(new InstructionStep
                        {
                            StepNumber = parsedStepNum,
                            InstructionText = cleanedStepText,
                            GroupHeader = currentInstGroup
                        });
                        instructionStepNumber = parsedStepNum + 1;
                    }
                }
                else
                {
                    if (metadata.TitleLineIndex.HasValue && i == metadata.TitleLineIndex.Value)
                        continue;

                    if (!string.IsNullOrWhiteSpace(metadata.Title) && l.Trim().Equals(metadata.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (IsDefiniteIngredientLine(l) || IsSubGroupHeader(l))
                        continue;

                    var cleanText = l.TrimStart('-', '•', '*').Trim();
                    var cleanedStepText = CleanInstructionStepText(cleanText);

                    if (!string.IsNullOrWhiteSpace(cleanedStepText))
                    {
                        instructions.Add(new InstructionStep
                        {
                            StepNumber = instructionStepNumber++,
                            InstructionText = cleanedStepText,
                            GroupHeader = currentInstGroup
                        });
                    }
                }
            }
        }

        return (instructions, notes, hasMultipleRecipes);
    }

    #endregion

    #region 6. Plausibility Check and Result Construction

    private static RecipeImportResult ValidateAndBuildResult(
        RecipeMetadata metadata,
        List<Ingredient> ingredients,
        List<InstructionStep> instructions,
        List<string> notes,
        RecipeSectionBounds bounds,
        bool hasMultipleRecipes,
        string? sourceUrl,
        string? remoteImageUrl)
    {
        bool hasCulinaryInstruction = instructions.Any(inst => CulinaryActionVerbsRegex.IsMatch(inst.InstructionText));
        bool hasVerifiedIngredient = ingredients.Any(i =>
            i.Quantity.HasValue ||
            !string.IsNullOrWhiteSpace(i.Unit) ||
            KnownIngredientWordsRegex.IsMatch(i.Name));
        bool hasExplicitRecipeHeading = bounds.InstructionHeaderIndex.HasValue
            || bounds.IngredientHeaderIndex.HasValue
            || metadata.Servings.HasValue;

        if (!hasExplicitRecipeHeading)
        {
            if (ingredients.Count < 2 || instructions.Count < 2 || !hasCulinaryInstruction)
            {
                return RecipeImportResult.Failure(
                    RecipeImportFailureReason.NoRecipeData,
                    "Inget recept kunde identifieras i den inklistrade texten. Kontrollera att texten innehåller ingredienser eller instruktioner, eller mata in receptet manuellt.");
            }
        }
        else if ((ingredients.Count == 0 && instructions.Count == 0) ||
                 (!hasVerifiedIngredient && !hasCulinaryInstruction))
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.NoRecipeData,
                "Inget recept kunde identifieras i den inklistrade texten. Kontrollera att texten innehåller ingredienser eller instruktioner, eller mata in receptet manuellt.");
        }

        string? warningMessage = null;

        if (hasMultipleRecipes)
        {
            warningMessage = "Texten verkar innehålla flera recept. Endast det första receptet har importerats. Granska ingredienser och steg noggrant.";
        }
        else if (ingredients.Count > 30)
        {
            warningMessage = "Receptet innehåller ovanligt många ingredienser (ovanligt många ingredienser identifierades). Kontrollera att text som inte hör till receptet inte har kommit med.";
        }
        else if (ingredients.Count == 0 && instructions.Count > 0)
        {
            warningMessage = "Inga ingredienser kunde identifieras automatiskt. Du kan lägga till ingredienser manuellt nedan.";
        }
        else if (instructions.Count == 0 && ingredients.Count > 0)
        {
            warningMessage = "Inga tillagningssteg kunde identifieras automatiskt. Du kan lägga till instruktioner manuellt nedan.";
        }
        else if (ingredients.Count <= 2 && instructions.Count <= 2)
        {
            warningMessage = "Receptet har få ingredienser eller steg. Granska att all information kommit med.";
        }

        string? sourceHost = null;
        if (!string.IsNullOrWhiteSpace(sourceUrl) && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            sourceHost = uri.Host;
        }

        var draft = new Recipe
        {
            Title = metadata.Title,
            Servings = metadata.Servings ?? 4,
            ServingsUnit = metadata.ServingsUnit ?? "portioner",
            CookTimeMinutes = metadata.CookTimeMinutes,
            PrepTimeMinutes = metadata.PrepTimeMinutes,
            Ingredients = ingredients,
            Instructions = instructions,
            Notes = notes.Count > 0 ? string.Join("\n", notes) : null,
            SourceUrl = sourceUrl,
            SourceHost = sourceHost
        };

        return new RecipeImportResult
        {
            Success = true,
            DraftRecipe = draft,
            RemoteImageUrl = remoteImageUrl,
            HasImageProperty = !string.IsNullOrWhiteSpace(remoteImageUrl),
            WarningMessage = warningMessage
        };
    }

    #endregion

    #region Helper Rules and Text Cleaning

    private static string CleanInstructionStepText(string text)
    {
        var cleaned = text.Trim();

        // 1. Remove obvious advertising clauses
        cleaned = AdClauseRegex.Replace(cleaned, ", ");
        cleaned = Regex.Replace(cleaned, @",\s*,+", ",");

        // 2. Remove URLs
        cleaned = Regex.Replace(cleaned, @"https?://[^\s]+", "", RegexOptions.IgnoreCase);

        // 3. Remove hashtags
        cleaned = Regex.Replace(cleaned, @"#\w+", "");

        // 4. Remove sentences that are purely ad/discount code
        cleaned = AdSentenceRegex.Replace(cleaned, "");

        // 5. Remove standalone ad markers like "[annons]" or "(adlink)"
        cleaned = Regex.Replace(cleaned, @"\s*\[(?:annons|adlink|ad)\]\s*", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*\((?:annons|adlink|annonslänk)\)\s*", " ", RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        cleaned = cleaned.TrimEnd(',', '-', '–', ':').Trim();

        if (!string.IsNullOrEmpty(cleaned) && !cleaned.EndsWith('.') && !cleaned.EndsWith('!') && !cleaned.EndsWith('?'))
        {
            cleaned += ".";
        }

        return cleaned;
    }

    private static bool IsTitleOrHeading(string line)
    {
        var clean = line.Trim();
        if (clean.StartsWith("##") || clean.StartsWith('#'))
            return true;

        if (HeadingServingsRegex.IsMatch(clean))
            return true;

        return false;
    }

    private static bool IsDefiniteIngredientLine(string line)
    {
        var clean = line.TrimStart('-', '•', '*', '+').Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return false;

        if (StandaloneNoiseRegex.IsMatch(clean))
            return false;

        // Instruction sentences starting with a number/time/quantity (e.g. "2 minuter senare tillsätter du grädden."
        // or "1 dl vatten tillsätts om riset blir för torrt.") must not be classified as ingredients.
        if (IsQuantityLedInstructionSentence(clean))
            return false;

        // Starts with digits or fractions, or Swedish word numbers "en" / "ett"
        if (Regex.IsMatch(clean, @"^(?:ca\.?\s*|cirka\s*)?(?:\d+|[½¼¾⅓⅔⅛]|en\s+|ett\s+)", RegexOptions.IgnoreCase))
            return true;

        // Starts with known culinary packaging or measure (e.g. "kruka persilja", "burk tomater")
        if (Regex.IsMatch(clean, @"^(?:kruka|krukor|burk|burkar|paket|pkt|påse|påsar|knippe|ask|askar|flaska|flaskor|nypa|nypor|klick|klickar|stänk|skvätt|droppe|droppar)\s+", RegexOptions.IgnoreCase))
            return true;

        // Known culinary ingredient without quantity (e.g. "salt och peppar")
        if (KnownIngredientWordsRegex.IsMatch(clean))
            return true;

        // Subgroup header
        if (IsSubGroupHeader(line))
            return true;

        return false;
    }

    private static bool IsQuantityLedInstructionSentence(string cleanLine)
    {
        if (string.IsNullOrWhiteSpace(cleanLine) || NumberedStepRegex.IsMatch(cleanLine))
            return false;

        // 1. Starts with a number followed by a time or temperature unit (e.g. "2 minuter senare tillsätter du grädden.")
        if (Regex.IsMatch(
                cleanLine,
                @"^(?:ca\.?\s*|cirka\s*)?(?:\d+|[½¼¾⅓⅔⅛])(?:\s*[\-–—]\s*\d+)?\s*(?:min(?:ut(?:er)?)?|sek(?:und(?:er)?)?|tim(?:m(?:e|ar))?|grad(?:er)?|°C?|gång(?:er)?)\b",
                RegexOptions.IgnoreCase))
        {
            return CulinaryActionVerbsRegex.IsMatch(cleanLine) ||
                   cleanLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4;
        }

        // 2. Starts with a quantity/measure or "en"/"ett", but is a full culinary instruction sentence
        // (e.g. "1 dl vatten tillsätts om riset blir för torrt.")
        if ((cleanLine.EndsWith('.') || cleanLine.EndsWith('!')) &&
            CulinaryActionVerbsRegex.IsMatch(cleanLine))
        {
            var words = cleanLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 4)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInstructionSentence(string line)
    {
        var clean = line.Trim();
        if (clean.Length < 6)
            return false;

        if (NumberedStepRegex.IsMatch(clean))
            return true;

        if (IsDefiniteIngredientLine(clean))
            return false;

        if (StandaloneNoiseRegex.IsMatch(clean) || NarrativeOutroRegex.IsMatch(clean) || SocialOrNoiseRegex.IsMatch(clean))
            return false;

        // Matches culinary action verbs
        if (!CulinaryActionVerbsRegex.IsMatch(clean))
            return false;

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2;
    }

    private static bool IsSentenceLikeInstructionCandidate(string line)
    {
        var clean = line.Trim();
        if (clean.Length < 10)
            return false;

        if (!clean.EndsWith('.') && !clean.EndsWith('!'))
            return false;

        if (IsDefiniteIngredientLine(clean) || IsTitleOrHeading(clean) || TipRegex.IsMatch(clean))
            return false;

        if (StandaloneNoiseRegex.IsMatch(clean) || NarrativeOutroRegex.IsMatch(clean) || SocialOrNoiseRegex.IsMatch(clean))
            return false;

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3;
    }

    private static bool IsIngredientLine(string line)
    {
        if (IsDefiniteIngredientLine(line))
            return true;

        var clean = line.TrimStart('-', '•', '*', '+').Trim();
        if (string.IsNullOrWhiteSpace(clean) || StandaloneNoiseRegex.IsMatch(clean))
            return false;

        // Plausible unquantified ingredient line (e.g. "Salladsblad", "Parmesan till servering", "Flingsalt", "Färsk basilika")
        if (IsPlausibleUnquantifiedIngredient(clean))
            return true;

        return false;
    }

    private static bool IsPlausibleUnquantifiedIngredient(string line)
    {
        if (line.Length > 50 || line.Length < 2)
            return false;

        // Do not classify sentences with punctuation or colons as ingredients
        if (line.EndsWith('.') || line.EndsWith('!') || line.EndsWith('?') || line.EndsWith(':'))
            return false;

        // Avoid markdown headers or numbered steps
        if (line.StartsWith('#') || Regex.IsMatch(line, @"^\d+[\.\)]"))
            return false;

        // Avoid links
        if (line.Contains("http", StringComparison.OrdinalIgnoreCase) || line.Contains("www.", StringComparison.OrdinalIgnoreCase))
            return false;

        // Avoid lines with culinary action verbs, general advice/planning verbs, or conversational phrases
        if (CulinaryActionVerbsRegex.IsMatch(line) ||
            Regex.IsMatch(line, @"\b(?:tips|kram|kommentera|lämna|prenumerera|planera|handla|förvara|kom\s+ihåg|tänk|välj|undvik)\b", RegexOptions.IgnoreCase))
            return false;

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 1 && words.Length <= 5;
    }

    private static bool IsSubGroupHeader(string line)
    {
        var clean = line.TrimStart('#').Trim();
        if (!clean.EndsWith(':') || clean.Length > 40 || clean.Contains('.') || Regex.IsMatch(clean, @"\d"))
            return false;

        if (InstructionHeaderRegex.IsMatch(line) || IngredientHeaderRegex.IsMatch(line))
            return false;

        if (StandaloneNoiseRegex.IsMatch(clean) || NarrativeOutroRegex.IsMatch(clean))
            return false;

        return true;
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
        if (lower == "st")
            return "st";
        if (lower.StartsWith("styck"))
            return "stycken";
        if (lower.StartsWith("bit"))
            return "bitar";
        if (lower.StartsWith("kak"))
            return "kakor";
        if (lower.StartsWith("bull"))
            return "bullar";
        if (lower.StartsWith("glas"))
            return "glas";
        if (lower.StartsWith("limp"))
            return "limpa";

        return unit;
    }

    #endregion
}
