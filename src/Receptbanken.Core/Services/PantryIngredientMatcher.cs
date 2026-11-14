using System.Text.RegularExpressions;
using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

public sealed record PantryMatchEvaluation(
    int MatchedPantryCount,
    double TotalRelevanceScore,
    IReadOnlyList<string> MatchedPantryTags);

/// <summary>
/// Deterministic normalizer and matcher for "Ingredienser hemma" (pantry tags) against recipe ingredients.
/// Handles lowercase, trimming, reliable Swedish inflections, and a curated set of culinary synonyms
/// while avoiding aggressive fuzzy matching and preventing trivial staples (salt, water, pepper) from dominating.
/// </summary>
public class PantryIngredientMatcher
{
    private static readonly HashSet<string> TrivialIngredients = new(StringComparer.OrdinalIgnoreCase)
    {
        "salt", "flingsalt", "örtsalt", "havssalt", "bergsalt",
        "vatten", "kallt vatten", "varmt vatten", "ljummet vatten", "kokande vatten", "pastavatten",
        "peppar", "svartpeppar", "vitpeppar", "nymalen svartpeppar", "malen vitpeppar", "salt och peppar",
        "matfett", "olja", "matolja", "rapsolja", "olivolja", "solrosolja", "neutral olja",
        "smör", "margarin", "smör eller olja", "smör till stekning", "olja till stekning",
        "socker", "strösocker"
    };

    private static readonly HashSet<string> MinorUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "krm", "nypa", "nypor", "stänk", "droppe", "droppar"
    };

    private static readonly HashSet<string> IgnoredQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "färsk", "färska", "fryst", "frysta", "tinad", "tinade",
        "kokt", "kokta", "rå", "råa", "stekt", "stekta",
        "riven", "rivna", "rivet", "finriven", "grovriven",
        "hackad", "hackade", "finhackad", "finhackade", "grovhackad", "grovhackade",
        "skivad", "skivade", "strimlad", "strimlade", "tärnad", "tärnade",
        "delad", "delade", "halverad", "halverade", "krossad", "krossade",
        "pressad", "pressade", "smält", "rumsvarm", "rumsvarmt",
        "ekologisk", "ekologiska", "svensk", "svenska", "valfri", "valfria"
    };

    private static readonly Dictionary<string, string> ExactInflectionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Poultry
        ["kycklingfiléer"] = "kycklingfilé",
        ["kycklingfileer"] = "kycklingfilé",
        ["kycklingfile"] = "kycklingfilé",
        ["kycklingbröstfiléer"] = "kycklingbröstfilé",
        ["kycklingbröstfile"] = "kycklingbröstfilé",
        ["kycklinglårfiléer"] = "kycklinglårfilé",
        ["kycklinglårfile"] = "kycklinglårfilé",
        ["kycklingklubbor"] = "kycklingklubba",
        ["kycklingvingar"] = "kycklingvinge",

        // Fish & seafood
        ["laxfiléer"] = "laxfilé",
        ["laxfileer"] = "laxfilé",
        ["laxfile"] = "laxfilé",
        ["laxkotletter"] = "laxkotlett",
        ["torskfiléer"] = "torskfilé",
        ["torskfileer"] = "torskfilé",
        ["torskfile"] = "torskfilé",
        ["räkor"] = "räka",
        [" kräftstjärtar"] = "kräftstjärt",

        // Meat & mince
        ["fläskfiléer"] = "fläskfilé",
        ["fläskfileer"] = "fläskfilé",
        ["fläskfile"] = "fläskfilé",
        ["fläskkotletter"] = "fläskkotlett",
        ["kotletter"] = "kotlett",
        ["korvar"] = "korv",
        ["köttbullar"] = "köttbulle",

        // Vegetables & root vegetables
        ["lökar"] = "lök",
        ["gula lökar"] = "gul lök",
        ["rödlökar"] = "rödlök",
        ["röda lökar"] = "rödlök",
        ["vitlöksklyftor"] = "vitlöksklyfta",
        ["morötter"] = "morot",
        ["potatisar"] = "potatis",
        ["tomater"] = "tomat",
        ["körsbärstomater"] = "körsbärstomat",
        ["cocktailtomater"] = "cocktailtomat",
        ["paprikor"] = "paprika",
        ["gurkor"] = "gurka",
        ["avokador"] = "avokado",
        ["avokados"] = "avokado",
        ["citroner"] = "citron",
        ["limer"] = "lime",
        ["äpplen"] = "äpple",
        ["champinjoner"] = "champinjon",
        ["kantareller"] = "kantarell",
        ["svampar"] = "svamp",
        ["ärtor"] = "ärta",
        ["ärter"] = "ärta",
        ["bönor"] = "böna",
        ["svarta bönor"] = "svart böna",
        ["vita bönor"] = "vit böna",
        ["kidneybönor"] = "kidneyböna",
        ["kikärtor"] = "kikärta",
        ["kikärter"] = "kikärta",
        ["linser"] = "lins",
        ["röda linser"] = "röd lins",
        ["gröna linser"] = "grön lins",
        ["zucchinier"] = "zucchini",
        ["auberginer"] = "aubergine",
        ["palsternackor"] = "palsternacka",
        ["rödbetor"] = "rödbeta",
        ["sötpotatisar"] = "sötpotatis",

        // Dairy & other
        ["creme fraiche"] = "crème fraiche",
        ["äggulor"] = "äggula",
        ["äggvitor"] = "äggvita",
        ["tortillabröd"] = "tortilla",
        ["tortillas"] = "tortilla"
    };

    /// <summary>
    /// Well-motivated culinary synonym groups where normalized canonical forms match each other.
    /// </summary>
    private static readonly string[][] SynonymGroups =
    [
        ["kyckling", "kycklingfilé", "kycklingbröstfilé", "kycklingbröst", "kycklinglårfilé", "kycklinginnerfilé"],
        ["köttfärs", "nötfärs", "blandfärs", "malet kött"],
        ["lax", "laxfilé", "laxkotlett"],
        ["torsk", "torskfilé", "torskrygg", "torskblock"],
        ["fläskfärs", "skinkfärs"],
        ["korv", "falukorv", "grillkorv", "chorizo"],
        ["grädde", "vispgrädde", "matlagningsgrädde", "mellangrädde", "syrad grädde"],
        ["crème fraiche", "lätt crème fraiche"],
        ["tomatkross", "krossad tomat", "krossade tomater", "passerad tomat", "passerade tomater"],
        ["lök", "gul lök"],
        ["vitlök", "vitlöksklyfta"],
        ["potatis", "fast potatis", "mjölig potatis", "färskpotatis"],
        ["pasta", "spaghetti", "spagetti", "penne", "tagliatelle", "fusilli", "makaroner", "farfalle", "linguine", "rigatoni", "fettuccine", "lasagneplattor"],
        ["ris", "jasminris", "basmatiris", "långkornigt ris", "matris"],
        ["halloumi", "grillost", "eldost"],
        ["zucchini", "squash"]
    ];

    private static readonly Dictionary<string, HashSet<string>> SynonymLookup = BuildSynonymLookup();

    private static Dictionary<string, HashSet<string>> BuildSynonymLookup()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in SynonymGroups)
        {
            var normalizedGroup = group.Select(NormalizeTerm).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in normalizedGroup)
            {
                if (!map.TryGetValue(item, out var existing))
                {
                    existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[item] = existing;
                }
                foreach (var peer in normalizedGroup)
                {
                    existing.Add(peer);
                }
            }
        }
        return map;
    }

    public bool IsTrivial(string? ingredientName)
    {
        if (string.IsNullOrWhiteSpace(ingredientName))
            return true;

        var normalized = NormalizeTerm(ingredientName);
        return string.IsNullOrWhiteSpace(normalized) || TrivialIngredients.Contains(normalized);
    }

    /// <summary>
    /// Normalizes a Swedish ingredient term (lowercase, trim, strip common culinary qualifiers, normalize inflections).
    /// </summary>
    public static string NormalizeTerm(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var cleaned = raw.Trim().ToLowerInvariant();
        // Remove parenthetical notes if any
        cleaned = Regex.Replace(cleaned, @"\([^)]*\)", " ");
        // Remove punctuation except hyphens and accented letters
        cleaned = Regex.Replace(cleaned, @"[,.;:!?""'/\\]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        if (TrivialIngredients.Contains(cleaned))
            return cleaned;

        if (ExactInflectionMap.TryGetValue(cleaned, out var exactMapped))
            return exactMapped;

        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !IgnoredQualifiers.Contains(t))
            .Select(NormalizeSingleWord)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

        if (tokens.Length == 0)
            return cleaned;

        var rejoined = string.Join(" ", tokens);
        if (ExactInflectionMap.TryGetValue(rejoined, out var mappedRejoined))
            return mappedRejoined;

        return rejoined;
    }

    private static string NormalizeSingleWord(string word)
    {
        if (ExactInflectionMap.TryGetValue(word, out var mapped))
            return mapped;

        if (word.Length >= 7)
        {
            if (word.EndsWith("filéer", StringComparison.Ordinal))
                return word[..^2]; // -> filé
            if (word.EndsWith("fileer", StringComparison.Ordinal))
                return word[..^6] + "filé";
            if (word.EndsWith("file", StringComparison.Ordinal))
                return word[..^4] + "filé";
            if (word.EndsWith("klyftor", StringComparison.Ordinal))
                return word[..^2] + "a"; // -> klyfta
        }

        return word;
    }

    /// <summary>
    /// Checks whether a pantry tag matches a recipe ingredient name using deterministic normalization and curated synonyms.
    /// Does NOT count trivial staples (like salt or water).
    /// </summary>
    public bool IsMatch(string? pantryTag, string? recipeIngredientName)
    {
        if (string.IsNullOrWhiteSpace(pantryTag) || string.IsNullOrWhiteSpace(recipeIngredientName))
            return false;

        var normPantry = NormalizeTerm(pantryTag);
        var normRecipe = NormalizeTerm(recipeIngredientName);

        if (string.IsNullOrWhiteSpace(normPantry) || string.IsNullOrWhiteSpace(normRecipe))
            return false;

        if (TrivialIngredients.Contains(normPantry) || TrivialIngredients.Contains(normRecipe))
            return false;

        // 1. Direct normalized match
        if (string.Equals(normPantry, normRecipe, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Curated synonym group match
        if (SynonymLookup.TryGetValue(normPantry, out var pantrySynonyms) &&
            pantrySynonyms.Contains(normRecipe))
        {
            return true;
        }

        // 3. Multi-word recipe ingredient where one of its normalized head words equals the pantry term
        //    (e.g., pantry "lax" vs recipe "kallrökt lax", or pantry "kycklingfilé" vs "kycklingfilé i strimlor")
        //    Exclude ambiguous words like "lök" matching "vitlök"/"purjolök"/"salladslök"/"schalottenlök"
        //    (since those are single compound words in Swedish, token splitting naturally keeps "vitlök" as one token != "lök"!).
        var recipeTokens = normRecipe.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (recipeTokens.Length > 1 && ! normPantry.Contains(' '))
        {
            foreach (var token in recipeTokens)
            {
                var normToken = NormalizeSingleWord(token);
                if (string.Equals(normToken, normPantry, StringComparison.OrdinalIgnoreCase))
                {
                    // Guard: "röd lök" should not match "gul lök", and "kokosmjölk" is a single word so won't match "mjölk"
                    if (normPantry == "lök" && (recipeTokens.Contains("röd") || recipeTokens.Contains("röda")))
                        return false;

                    return true;
                }

                if (SynonymLookup.TryGetValue(normPantry, out var tokenSynonyms) &&
                    tokenSynonyms.Contains(normToken))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluates how well a recipe matches the user's available pantry ingredients.
    /// Trivial ingredients (salt, water, pepper, cooking fat) are ignored, and main ingredients receive higher weight
    /// than minor seasoning quantities.
    /// </summary>
    public PantryMatchEvaluation EvaluateRecipe(Recipe recipe, IEnumerable<string>? pantryIngredients)
    {
        if (recipe?.Ingredients == null || recipe.Ingredients.Count == 0 || pantryIngredients == null)
        {
            return new PantryMatchEvaluation(0, 0.0, Array.Empty<string>());
        }

        var matchedTags = new List<string>();
        double totalScore = 0.0;

        var distinctPantry = pantryIngredients
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var pantryTag in distinctPantry)
        {
            if (IsTrivial(pantryTag))
                continue;

            double bestIngredientRelevance = 0.0;
            foreach (var ingredient in recipe.Ingredients)
            {
                if (IsMatch(pantryTag, ingredient.Name))
                {
                    var relevance = ComputeIngredientRelevance(ingredient);
                    if (relevance > bestIngredientRelevance)
                    {
                        bestIngredientRelevance = relevance;
                    }
                }
            }

            if (bestIngredientRelevance > 0.0)
            {
                matchedTags.Add(pantryTag);
                totalScore += bestIngredientRelevance;
            }
        }

        return new PantryMatchEvaluation(matchedTags.Count, totalScore, matchedTags);
    }

    private static double ComputeIngredientRelevance(Ingredient ingredient)
    {
        // Garnish or serving suggestions are minor compared to main dish ingredients
        if (!string.IsNullOrWhiteSpace(ingredient.GroupHeader) &&
            (ingredient.GroupHeader.Contains("garnering", StringComparison.OrdinalIgnoreCase) ||
             ingredient.GroupHeader.Contains("servering", StringComparison.OrdinalIgnoreCase) ||
             ingredient.GroupHeader.Contains("topping", StringComparison.OrdinalIgnoreCase)))
        {
            return 0.4;
        }

        // Tiny pinch/spice units (krm, nypa)
        if (!string.IsNullOrWhiteSpace(ingredient.Unit) && MinorUnits.Contains(ingredient.Unit.Trim()))
        {
            return 0.35;
        }

        // Small teaspoon amount (tsk)
        if (string.Equals(ingredient.Unit?.Trim(), "tsk", StringComparison.OrdinalIgnoreCase))
        {
            return 0.5;
        }

        // Substantial main ingredient quantities (e.g. >= 200 g, >= 0.2 kg, >= 2 hg, förp, paket, burk, or early order index)
        var unit = ingredient.Unit?.Trim().ToLowerInvariant();
        var qty = ingredient.Quantity ?? 0m;

        bool isSubstantialMain =
            (unit == "g" && qty >= 150m) ||
            (unit == "kg" && qty >= 0.15m) ||
            (unit == "hg" && qty >= 1.5m) ||
            (unit is "förp" or "paket" or "burk" or "burkar") ||
            (unit == "dl" && qty >= 2m) ||
            (unit == "st" && qty >= 1m);

        if (isSubstantialMain)
        {
            return 1.2;
        }

        return 1.0;
    }
}

