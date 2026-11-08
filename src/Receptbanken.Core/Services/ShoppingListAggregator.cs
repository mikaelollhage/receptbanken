using System.Globalization;
using System.Text.RegularExpressions;
using Receptbanken.Core.Models;

namespace Receptbanken.Core.Services;

public class ShoppingListAggregator
{
    private readonly ServingCalculator _servingCalculator;

    private static readonly Dictionary<string, string> SafeInflectionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Kyckling & fågel
        ["kycklingfiléer"] = "kycklingfilé",
        ["kycklingfileer"] = "kycklingfilé",
        ["kycklingfile"] = "kycklingfilé",
        ["kycklingbröstfiléer"] = "kycklingbröstfilé",
        ["kycklingbröstfile"] = "kycklingbröstfilé",
        ["kycklinglårfiléer"] = "kycklinglårfilé",
        ["kycklinglårfile"] = "kycklinglårfilé",
        ["kycklingklubbor"] = "kycklingklubba",
        ["kycklingvingar"] = "kycklingvinge",

        // Fisk & skaldjur
        ["laxfiléer"] = "laxfilé",
        ["laxfileer"] = "laxfilé",
        ["laxfile"] = "laxfilé",
        ["laxkotletter"] = "laxkotlett",
        ["torskfiléer"] = "torskfilé",
        ["torskfileer"] = "torskfilé",
        ["torskfile"] = "torskfilé",
        ["räkor"] = "räka",
        ["kräftstjärtar"] = "kräftstjärt",

        // Kött & färs
        ["fläskfiléer"] = "fläskfilé",
        ["fläskfileer"] = "fläskfilé",
        ["fläskfile"] = "fläskfilé",
        ["fläskkotletter"] = "fläskkotlett",
        ["kotletter"] = "kotlett",
        ["korvar"] = "korv",
        ["köttbullar"] = "köttbulle",

        // Grönsaker, frukt & rotfrukter
        ["lökar"] = "lök",
        ["gula lökar"] = "gul lök",
        ["rödlökar"] = "röd lök",
        ["rödlök"] = "röd lök",
        ["röda lökar"] = "röd lök",
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
        ["bananer"] = "banan",
        ["apelsiner"] = "apelsin",
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

        // Mejeri & övrigt
        ["creme fraiche"] = "crème fraiche",
        ["äggulor"] = "äggula",
        ["äggvitor"] = "äggvita",
        ["tortillabröd"] = "tortilla",
        ["tortillas"] = "tortilla"
    };

    /// <summary>
    /// Pure home-preparation adjectives that do not change which product is purchased in the store
    /// (unlike "färsk", "fryst", "torkad", "pressad", "krossade", "syrad", etc.).
    /// </summary>
    private static readonly HashSet<string> SafeHomePreparationPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "vispad", "lättvispad",
        "smält", "rumsvarm", "rumsvarmt",
        "hackad", "hackade", "finhackad", "finhackade", "grovhackad", "grovhackade",
        "skivad", "skivade", "strimlad", "strimlade", "tärnad", "tärnade", "klyftad", "klyftade"
    };

    private static readonly Dictionary<string, decimal> WeightFactorsToGrams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g"] = 1m,
        ["hg"] = 100m,
        ["kg"] = 1000m
    };

    private static readonly Dictionary<string, decimal> VolumeFactorsToMilliliters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ml"] = 1m,
        ["krm"] = 1m,
        ["tsk"] = 5m,
        ["cl"] = 10m,
        ["msk"] = 15m,
        ["dl"] = 100m,
        ["l"] = 1000m
    };

    private static readonly Regex TrailingPurposeRegex = new(
        @"^(?<base>.+?)\s+(?<purpose>till\s+(?:garnering|servering|topping|dekorering|dekoration|pensling|stekning|formen|plåten|utbakning)(?:\s+och\s+(?:garnering|servering|topping|dekorering|dekoration))?|efter\s+smak|vid\s+servering|vid\s+behov)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PackageSizeRegex = new(
        @"(?:à\s*)?(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>g|gram|kg|kilo|kilogram|hg|hekto|hektogram|ml|milliliter|cl|centiliter|dl|deciliter|l|liter)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> RecognizedUnitTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "dl", "deciliter",
        "msk", "matsked", "matskedar",
        "tsk", "tesked", "teskedar",
        "krm", "kryddmått",
        "l", "liter",
        "cl", "centiliter",
        "ml", "milliliter",
        "g", "gram",
        "kg", "kilo", "kilogram",
        "hg", "hekto", "hektogram",
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
        "knippe", "ask", "askar", "flaska", "flaskor",
        "klick", "klickar", "stänk", "skvätt", "droppe", "droppar"
    };

    private static readonly Regex ExistingExtraCommentRegex = new(
        @"^Extra\s+(?<item>.+?)(?:\s+(?<purpose>till\s+.+?))?,\s*mängd\s+ej\s+angiven$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ShoppingListAggregator(ServingCalculator servingCalculator)
    {
        _servingCalculator = servingCalculator;
    }

    /// <summary>
    /// Checks whether two ingredient names refer to the same grocery item under conservative normalization.
    /// </summary>
    public bool AreSameIngredient(string? nameA, string? nameB)
    {
        if (string.IsNullOrWhiteSpace(nameA) || string.IsNullOrWhiteSpace(nameB))
            return false;

        var idA = AnalyzeIdentity(nameA, null);
        var idB = AnalyzeIdentity(nameB, null);

        return !string.IsNullOrWhiteSpace(idA.CanonicalKey) &&
               string.Equals(idA.CanonicalKey, idB.CanonicalKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Aggregates incoming shopping list items into an existing list of unchecked items when
    /// ingredient names and units are conservatively compatible. Incompatible units or conflicting
    /// package sizes remain separate entries, and unquantified occurrences are preserved as comments.
    /// </summary>
    public List<ShoppingListItem> MergeItems(
        IEnumerable<ShoppingListItem> existingItems,
        IEnumerable<ShoppingListItem> newItems)
    {
        var result = existingItems.ToList();

        // Normalize any existing unchecked items that may have inline qualifiers in Name
        foreach (var existing in result.Where(x => !x.IsChecked))
        {
            NormalizeItemInPlace(existing);
        }

        foreach (var rawIncoming in newItems)
        {
            if (string.IsNullOrWhiteSpace(rawIncoming.Name))
                continue;

            var incoming = CloneAndNormalize(rawIncoming);
            if (string.IsNullOrWhiteSpace(incoming.Name))
                continue;

            if (incoming.IsChecked)
            {
                result.Add(incoming);
                continue;
            }

            var incomingIdentity = AnalyzeIdentity(incoming.Name, incoming.Comment);
            if (string.IsNullOrWhiteSpace(incomingIdentity.CanonicalKey))
                continue;

            var match = FindCompatibleMatch(result, incoming, incomingIdentity);

            if (match != null)
            {
                MergeIntoExisting(match, incoming, incomingIdentity);
            }
            else
            {
                result.Add(incoming);
            }
        }

        return result;
    }

    public ShoppingListItem CreateFromRecipeIngredient(
        Ingredient ingredient,
        int baseServings,
        int targetServings,
        int? recipeId = null)
    {
        var scaledQuantity = _servingCalculator.ScaleQuantity(ingredient.Quantity, baseServings, targetServings);
        decimal? scaledQuantityMax = null;
        if (ingredient.Quantity.HasValue &&
            ingredient.QuantityMax.HasValue &&
            ingredient.QuantityMax.Value > ingredient.Quantity.Value)
        {
            scaledQuantityMax = _servingCalculator.ScaleQuantity(ingredient.QuantityMax, baseServings, targetServings);
        }

        var unit = NormalizeUnit(ingredient.Unit);
        ExtractFromIngredientFields(
            ingredient.Name,
            ingredient.Comment,
            ingredient.Preparation,
            ingredient.GroupHeader,
            out var cleanName,
            out var combinedComment);

        TryExtractLeadingUnitFromNameWhenMissing(scaledQuantity, ref unit, ref cleanName);

        var normalizedUnit = string.IsNullOrWhiteSpace(unit) ? null : unit;

        return new ShoppingListItem
        {
            RecipeId = recipeId,
            Name = cleanName,
            Quantity = scaledQuantity,
            QuantityMax = scaledQuantityMax,
            Unit = normalizedUnit,
            Comment = combinedComment,
            IsChecked = false,
            CreatedAt = DateTimeOffset.UtcNow,
            RawText = FormatRawText(scaledQuantity, scaledQuantityMax, normalizedUnit, cleanName)
        };
    }

    public string FormatRawText(decimal? quantity, string? unit, string name) =>
        FormatRawText(quantity, null, unit, name);

    public string FormatRawText(decimal? quantity, decimal? quantityMax, string? unit, string name)
    {
        var parts = new List<string>();
        if (quantity.HasValue)
        {
            parts.Add(_servingCalculator.FormatQuantityRange(quantity, quantityMax));
        }
        if (!string.IsNullOrWhiteSpace(unit))
        {
            parts.Add(unit.Trim());
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name.Trim());
        }
        return string.Join(" ", parts);
    }

    private ShoppingListItem? FindCompatibleMatch(
        List<ShoppingListItem> candidates,
        ShoppingListItem incoming,
        IngredientIdentity incomingIdentity)
    {
        foreach (var existing in candidates)
        {
            if (existing.IsChecked)
                continue;

            var existingIdentity = AnalyzeIdentity(existing.Name, existing.Comment);
            if (!string.Equals(existingIdentity.CanonicalKey, incomingIdentity.CanonicalKey, StringComparison.OrdinalIgnoreCase))
                continue;

            // Conflicting package sizes (e.g. "à 400 g" vs "à 200 g") must never be merged silently
            if (HasConflictingPackageSize(existing.Comment, incoming.Comment))
                continue;

            // Both items have quantities -> units must be compatible for summation
            if (existing.Quantity.HasValue && incoming.Quantity.HasValue)
            {
                if (CanSumUnits(existing.Unit, incoming.Unit))
                {
                    return existing;
                }
                continue;
            }

            // One or both items lack a quantity (e.g. "2 dl blåbär" + "blåbär till garnering"
            // or "blåbär till garnering" + "blåbär till servering")
            // Only merge when the unquantified item does not carry an incompatible unit
            var unquantified = existing.Quantity.HasValue ? incoming : existing;
            if (string.IsNullOrWhiteSpace(unquantified.Unit))
            {
                return existing;
            }
        }

        return null;
    }

    private void MergeIntoExisting(
        ShoppingListItem match,
        ShoppingListItem incoming,
        IngredientIdentity incomingIdentity)
    {
        var existingIdentity = AnalyzeIdentity(match.Name, match.Comment);

        // 1. Both items have known quantities -> sum quantities/intervals and convert compatible units
        if (match.Quantity.HasValue && incoming.Quantity.HasValue)
        {
            TrySumQuantitiesAndUnits(
                match.Quantity.Value,
                match.QuantityMax,
                match.Unit,
                incoming.Quantity.Value,
                incoming.QuantityMax,
                incoming.Unit,
                out var sumMin,
                out var sumMax,
                out var targetUnit);

            match.Quantity = sumMin;
            match.QuantityMax = sumMax;
            match.Unit = targetUnit;
            match.Name = SelectMergedDisplayName(match.Name, existingIdentity, incoming.Name, incomingIdentity);
            match.Comment = MergeQuantifiedComments(
                match.Comment,
                existingIdentity.LeadingPreparation,
                incoming.Comment,
                incomingIdentity.LeadingPreparation);
            match.RawText = FormatRawText(match.Quantity, match.QuantityMax, match.Unit, match.Name);
            return;
        }

        // 2. Existing is quantified, incoming is unquantified (e.g. "2 dl blåbär" + "blåbär till garnering")
        if (match.Quantity.HasValue && !incoming.Quantity.HasValue)
        {
            match.Name = SelectQuantifiedCanonicalName(match.Name, existingIdentity, incomingIdentity);
            var extraComment = BuildExtraCommentForUnquantified(incoming.Name, incoming.Comment, incomingIdentity);
            match.Comment = CombineCommentWithExtra(match.Comment, extraComment);
            match.RawText = FormatRawText(match.Quantity, match.QuantityMax, match.Unit, match.Name);
            return;
        }

        // 3. Existing is unquantified, incoming is quantified (e.g. "blåbär till garnering" + "2 dl blåbär")
        if (!match.Quantity.HasValue && incoming.Quantity.HasValue)
        {
            var previousUnquantifiedName = match.Name;
            var previousUnquantifiedComment = match.Comment;

            match.Quantity = incoming.Quantity;
            match.QuantityMax = incoming.QuantityMax;
            match.Unit = incoming.Unit;
            match.Name = SelectQuantifiedCanonicalName(incoming.Name, incomingIdentity, existingIdentity);

            var extraComment = BuildExtraCommentForUnquantified(previousUnquantifiedName, previousUnquantifiedComment, existingIdentity);
            var incomingBaseComment = CombineQualifiers(incomingIdentity.LeadingPreparation, incoming.Comment);
            match.Comment = CombineCommentWithExtra(incomingBaseComment, extraComment);
            match.RawText = FormatRawText(match.Quantity, match.QuantityMax, match.Unit, match.Name);
            return;
        }

        // 4. Both items are unquantified (e.g. "blåbär till garnering" + "blåbär till servering")
        match.Name = SelectMergedDisplayName(match.Name, existingIdentity, incoming.Name, incomingIdentity);
        match.Comment = MergeTwoUnquantifiedComments(
            match.Comment,
            existingIdentity.LeadingPreparation,
            incoming.Comment,
            incomingIdentity.LeadingPreparation);
        match.RawText = FormatRawText(null, null, match.Unit, match.Name);
    }

    private static string SelectQuantifiedCanonicalName(
        string quantifiedName,
        IngredientIdentity quantifiedIdentity,
        IngredientIdentity unquantifiedIdentity)
    {
        if (!string.IsNullOrWhiteSpace(quantifiedIdentity.LeadingPreparation))
        {
            return quantifiedIdentity.BaseDisplayName;
        }

        if (!string.Equals(quantifiedName.Trim(), unquantifiedIdentity.BaseDisplayName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(quantifiedIdentity.CanonicalKey, unquantifiedIdentity.CanonicalKey, StringComparison.OrdinalIgnoreCase))
        {
            return quantifiedIdentity.BaseDisplayName;
        }

        return quantifiedName.Trim();
    }

    private static string SelectMergedDisplayName(
        string existingName,
        IngredientIdentity existingIdentity,
        string incomingName,
        IngredientIdentity incomingIdentity)
    {
        // If either item had a leading preparation (e.g. "vispad grädde" merged with "grädde"),
        // use the clean base ingredient name ("grädde") and move preparation to Comment.
        if (!string.IsNullOrWhiteSpace(existingIdentity.LeadingPreparation) ||
            !string.IsNullOrWhiteSpace(incomingIdentity.LeadingPreparation))
        {
            return existingIdentity.BaseDisplayName;
        }

        // If the two names only differ in casing (e.g. "Mjölk" and "mjölk"), keep existing casing.
        if (string.Equals(existingName.Trim(), incomingName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return existingName.Trim();
        }

        // If they differ by inflection (e.g. "gul lök" vs "gula lökar"), prefer the canonical singular form ("gul lök").
        return existingIdentity.CanonicalKey;
    }

    private static string BuildExtraCommentForUnquantified(
        string unquantifiedName,
        string? unquantifiedComment,
        IngredientIdentity unquantifiedIdentity)
    {
        // Determine display descriptor for the unquantified item (e.g. "blåbär" or "vispad grädde")
        string itemDescriptor;
        if (!string.IsNullOrWhiteSpace(unquantifiedIdentity.LeadingPreparation))
        {
            itemDescriptor = $"{unquantifiedIdentity.LeadingPreparation.ToLowerInvariant()} {unquantifiedIdentity.BaseDisplayName.ToLowerInvariant()}";
        }
        else if (HasLeadingPreparationInComment(unquantifiedComment, out var prepFromComment, out var remainingComment))
        {
            itemDescriptor = $"{prepFromComment.ToLowerInvariant()} {unquantifiedIdentity.BaseDisplayName.ToLowerInvariant()}";
            unquantifiedComment = remainingComment;
        }
        else
        {
            itemDescriptor = unquantifiedName.Trim().ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(unquantifiedComment))
        {
            return $"Extra {itemDescriptor}, mängd ej angiven";
        }

        var cleanedPurpose = StripAmountNotSpecifiedSuffix(unquantifiedComment.Trim());
        if (string.IsNullOrWhiteSpace(cleanedPurpose))
        {
            return $"Extra {itemDescriptor}, mängd ej angiven";
        }

        var lowerPurpose = LowercaseFirstChar(cleanedPurpose);
        return $"Extra {itemDescriptor} {lowerPurpose}, mängd ej angiven";
    }

    private static bool HasLeadingPreparationInComment(
        string? comment,
        out string preparation,
        out string? remainingComment)
    {
        preparation = string.Empty;
        remainingComment = comment;

        if (string.IsNullOrWhiteSpace(comment))
            return false;

        var parts = comment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        if (SafeHomePreparationPrefixes.Contains(parts[0]))
        {
            preparation = parts[0];
            remainingComment = parts.Length > 1 ? string.Join(", ", parts.Skip(1)) : null;
            return true;
        }

        return false;
    }

    private static string CombineCommentWithExtra(string? existingComment, string newExtraComment)
    {
        if (string.IsNullOrWhiteSpace(existingComment))
            return newExtraComment;

        if (string.Equals(existingComment.Trim(), newExtraComment.Trim(), StringComparison.OrdinalIgnoreCase))
            return existingComment.Trim();

        // If existingComment is already an "Extra <item> till <purpose1>, mängd ej angiven" and newExtraComment is also one, combine their purposes!
        var matchExisting = ExistingExtraCommentRegex.Match(existingComment.Trim());
        var matchNew = ExistingExtraCommentRegex.Match(newExtraComment.Trim());
        if (matchExisting.Success && matchNew.Success)
        {
            var itemPart = matchExisting.Groups["item"].Value.Trim();
            var purpose1 = matchExisting.Groups["purpose"].Success ? matchExisting.Groups["purpose"].Value.Trim() : null;
            var purpose2 = matchNew.Groups["purpose"].Success ? matchNew.Groups["purpose"].Value.Trim() : null;

            var combinedPurpose = CombinePurposePhrases(purpose1, purpose2);
            if (!string.IsNullOrWhiteSpace(combinedPurpose))
            {
                return $"Extra {itemPart} {LowercaseFirstChar(combinedPurpose)}, mängd ej angiven";
            }

            return $"Extra {itemPart}, mängd ej angiven";
        }

        return $"{existingComment.Trim()}, {LowercaseFirstChar(newExtraComment.Trim())}";
    }

    private static string? MergeQuantifiedComments(
        string? commentA,
        string? leadingPrepA,
        string? commentB,
        string? leadingPrepB)
    {
        var effA = CombineQualifiers(leadingPrepA, commentA);
        var effB = CombineQualifiers(leadingPrepB, commentB);

        if (string.IsNullOrWhiteSpace(effA))
            return effB;
        if (string.IsNullOrWhiteSpace(effB))
            return effA;

        if (string.Equals(effA, effB, StringComparison.OrdinalIgnoreCase))
            return effA;

        // If one of them is an "Extra ..., mängd ej angiven" comment, keep it after the regular comment
        var existingMatch = ExistingExtraCommentRegex.Match(effA);
        if (existingMatch.Success && !ExistingExtraCommentRegex.IsMatch(effB))
        {
            return $"{effB}, {LowercaseFirstChar(effA)}";
        }

        return CombineQualifiers(effA, effB);
    }

    private static string MergeTwoUnquantifiedComments(
        string? commentA,
        string? leadingPrepA,
        string? commentB,
        string? leadingPrepB)
    {
        var cleanA = StripAmountNotSpecifiedSuffix(CombineQualifiers(leadingPrepA, commentA));
        var cleanB = StripAmountNotSpecifiedSuffix(CombineQualifiers(leadingPrepB, commentB));

        var combinedPurpose = CombinePurposePhrases(cleanA, cleanB);
        if (string.IsNullOrWhiteSpace(combinedPurpose))
        {
            return "Mängd ej angiven";
        }

        return $"{CapitalizeFirstChar(combinedPurpose)}, mängd ej angiven";
    }

    private static string? CombinePurposePhrases(string? phraseA, string? phraseB)
    {
        if (string.IsNullOrWhiteSpace(phraseA))
            return phraseB?.Trim();
        if (string.IsNullOrWhiteSpace(phraseB))
            return phraseA.Trim();

        var a = phraseA.Trim();
        var b = phraseB.Trim();

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return a;

        if (a.StartsWith("till ", StringComparison.OrdinalIgnoreCase) &&
            b.StartsWith("till ", StringComparison.OrdinalIgnoreCase))
        {
            var targetA = a[5..].Trim();
            var targetB = b[5..].Trim();

            var targets = new List<string>();
            foreach (var part in targetA.Split(new[] { " och ", "," }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!targets.Contains(part, StringComparer.OrdinalIgnoreCase))
                    targets.Add(part.ToLowerInvariant());
            }
            foreach (var part in targetB.Split(new[] { " och ", "," }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!targets.Contains(part, StringComparer.OrdinalIgnoreCase))
                    targets.Add(part.ToLowerInvariant());
            }

            if (targets.Count == 1)
                return $"Till {targets[0]}";
            if (targets.Count == 2)
                return $"Till {targets[0]} och {targets[1]}";

            return $"Till {string.Join(", ", targets.Take(targets.Count - 1))} och {targets.Last()}";
        }

        return CombineQualifiers(a, b);
    }

    private static string? StripAmountNotSpecifiedSuffix(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        var stripped = Regex.Replace(comment.Trim(), @",?\s*mängd\s+ej\s+angiven\.?$", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    private ShoppingListItem CloneAndNormalize(ShoppingListItem source)
    {
        var normalizedUnit = NormalizeUnit(source.Unit);
        ExtractFromIngredientFields(
            source.Name,
            source.Comment,
            preparation: null,
            groupHeader: null,
            out var cleanName,
            out var combinedComment);

        TryExtractLeadingUnitFromNameWhenMissing(source.Quantity, ref normalizedUnit, ref cleanName);

        var unitOrNull = string.IsNullOrWhiteSpace(normalizedUnit) ? null : normalizedUnit;
        var qtyMax = (source.Quantity.HasValue && source.QuantityMax.HasValue && source.QuantityMax.Value > source.Quantity.Value)
            ? source.QuantityMax
            : null;

        return new ShoppingListItem
        {
            Id = source.Id,
            RecipeId = source.RecipeId,
            Name = cleanName,
            Quantity = source.Quantity,
            QuantityMax = qtyMax,
            Unit = unitOrNull,
            Comment = combinedComment,
            Department = source.Department,
            IsChecked = source.IsChecked,
            SortOrder = source.SortOrder,
            CreatedAt = source.CreatedAt,
            RawText = FormatRawText(source.Quantity, qtyMax, unitOrNull, cleanName)
        };
    }

    private void NormalizeItemInPlace(ShoppingListItem item)
    {
        var normalizedUnit = NormalizeUnit(item.Unit);
        ExtractFromIngredientFields(
            item.Name,
            item.Comment,
            preparation: null,
            groupHeader: null,
            out var cleanName,
            out var combinedComment);

        TryExtractLeadingUnitFromNameWhenMissing(item.Quantity, ref normalizedUnit, ref cleanName);

        item.Name = cleanName;
        item.Unit = string.IsNullOrWhiteSpace(normalizedUnit) ? null : normalizedUnit;
        item.Comment = combinedComment;
        if (!item.Quantity.HasValue || !item.QuantityMax.HasValue || item.QuantityMax.Value <= item.Quantity.Value)
        {
            item.QuantityMax = null;
        }
        item.RawText = FormatRawText(item.Quantity, item.QuantityMax, item.Unit, item.Name);
    }

    private static void TryExtractLeadingUnitFromNameWhenMissing(
        decimal? quantity,
        ref string normalizedUnit,
        ref string cleanName)
    {
        if (!quantity.HasValue || !string.IsNullOrWhiteSpace(normalizedUnit) || string.IsNullOrWhiteSpace(cleanName))
            return;

        var tokenMatch = Regex.Match(cleanName, @"^([a-zA-ZåäöÅÄÖ]+)\.?\s+(.+)$");
        if (!tokenMatch.Success)
            return;

        var candidateUnit = tokenMatch.Groups[1].Value;
        var restName = tokenMatch.Groups[2].Value.Trim();
        if (!string.IsNullOrWhiteSpace(restName) && RecognizedUnitTokens.Contains(candidateUnit))
        {
            normalizedUnit = NormalizeUnit(candidateUnit);
            cleanName = restName;
        }
    }

    private static void ExtractFromIngredientFields(
        string? rawName,
        string? rawComment,
        string? preparation,
        string? groupHeader,
        out string cleanName,
        out string? combinedComment)
    {
        var workingName = (rawName ?? string.Empty).Trim();
        string? inlinePreparation = null;

        // 1. Check for comma outside parentheses in Name (e.g. "smör, smält")
        var commaIndex = FindCommaOutsideParentheses(workingName);
        if (commaIndex > 0 && commaIndex < workingName.Length - 1)
        {
            inlinePreparation = workingName[(commaIndex + 1)..].Trim();
            workingName = workingName[..commaIndex].Trim();
        }

        // 2. Check for trailing parenthetical comments in Name (e.g. "krossade tomater (à 400 g)")
        ExtractTrailingParentheses(workingName, out workingName, out var inlineParenComment);

        // 3. Check for trailing purpose phrase in Name (e.g. "blåbär till garnering", "vispad grädde till garnering")
        string? inlinePurpose = null;
        var purposeMatch = TrailingPurposeRegex.Match(workingName);
        if (purposeMatch.Success)
        {
            var candidateBase = purposeMatch.Groups["base"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidateBase))
            {
                workingName = candidateBase;
                inlinePurpose = purposeMatch.Groups["purpose"].Value.Trim();
            }
        }

        cleanName = string.IsNullOrWhiteSpace(workingName) ? (rawName ?? string.Empty).Trim() : workingName;

        // 4. Check for relevant serving/garnish GroupHeader (ignoring structural headers like "Sås" or "Fyllning")
        var headerPurpose = ExtractRelevantGroupHeader(groupHeader);

        // 5. Combine all qualifiers without duplication
        var qualifiers = new List<string>();
        AddQualifierIfDistinct(qualifiers, preparation);
        AddQualifierIfDistinct(qualifiers, inlinePreparation);
        AddQualifierIfDistinct(qualifiers, rawComment);
        AddQualifierIfDistinct(qualifiers, inlineParenComment);
        AddQualifierIfDistinct(qualifiers, inlinePurpose);
        AddQualifierIfDistinct(qualifiers, headerPurpose);

        combinedComment = qualifiers.Count > 0 ? string.Join(", ", qualifiers) : null;
    }

    private static void AddQualifierIfDistinct(List<string> target, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var trimmed = candidate.Trim();
        foreach (var existing in target)
        {
            if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                return;

            // Avoid duplicating "till garnering" when "garnering" or "Till garnering" is already present
            if (existing.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(existing, StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Contains("garnering", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("servering", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("topping", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        target.Add(trimmed);
    }

    private static string? CombineQualifiers(string? first, string? second)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(first))
        {
            foreach (var part in first.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddQualifierIfDistinct(list, part);
            }
        }
        if (!string.IsNullOrWhiteSpace(second))
        {
            foreach (var part in second.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddQualifierIfDistinct(list, part);
            }
        }

        return list.Count > 0 ? string.Join(", ", list) : null;
    }

    private static string? ExtractRelevantGroupHeader(string? groupHeader)
    {
        if (string.IsNullOrWhiteSpace(groupHeader))
            return null;

        var cleaned = groupHeader.Trim().TrimEnd(':', '.', '!', '?').Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        bool isRelevantPurpose =
            cleaned.Contains("garnering", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("servering", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("topping", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("dekoration", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("dekorering", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("tillbehör", StringComparison.OrdinalIgnoreCase);

        if (!isRelevantPurpose)
            return null;

        if (cleaned.StartsWith("till ", StringComparison.OrdinalIgnoreCase))
        {
            return CapitalizeFirstChar(cleaned);
        }

        return $"Till {cleaned.ToLowerInvariant()}";
    }

    private static IngredientIdentity AnalyzeIdentity(string name, string? comment)
    {
        ExtractFromIngredientFields(name, comment, null, null, out var cleanName, out _);

        var normalizedSpace = Regex.Replace(cleanName.Trim(), @"\s+", " ");
        var lower = normalizedSpace.ToLowerInvariant();

        string? leadingPrep = null;
        string baseDisplayName = normalizedSpace;
        string baseLower = lower;

        var firstSpace = lower.IndexOf(' ');
        if (firstSpace > 0)
        {
            var firstToken = lower[..firstSpace];
            var restDisplay = normalizedSpace[(firstSpace + 1)..].Trim();
            var restLower = restDisplay.ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(restLower) && SafeHomePreparationPrefixes.Contains(firstToken))
            {
                leadingPrep = firstToken;
                baseDisplayName = restDisplay;
                baseLower = restLower;
            }
        }

        var canonicalKey = NormalizeCanonicalIngredientKey(baseLower);
        if (SafeInflectionMap.TryGetValue(baseLower, out var mappedBase))
        {
            baseDisplayName = mappedBase;
        }

        return new IngredientIdentity(canonicalKey, baseDisplayName, leadingPrep);
    }

    private static string NormalizeCanonicalIngredientKey(string lowerTerm)
    {
        if (string.IsNullOrWhiteSpace(lowerTerm))
            return string.Empty;

        if (SafeInflectionMap.TryGetValue(lowerTerm, out var exactMapped))
            return exactMapped;

        var tokens = lowerTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return lowerTerm;

        // Only normalize the head noun (last token) for safe plural inflections while keeping
        // all preceding product-distinguishing adjectives (e.g. "färsk", "torkad", "färska", "frysta", "gul", "röd") intact.
        var lastIdx = tokens.Length - 1;
        tokens[lastIdx] = NormalizeSingleNounInflection(tokens[lastIdx]);

        var rejoined = string.Join(" ", tokens);
        if (SafeInflectionMap.TryGetValue(rejoined, out var rejoinedMapped))
            return rejoinedMapped;

        return rejoined;
    }

    private static string NormalizeSingleNounInflection(string word)
    {
        if (SafeInflectionMap.TryGetValue(word, out var mapped))
            return mapped;

        if (word.Length >= 7)
        {
            if (word.EndsWith("filéer", StringComparison.Ordinal))
                return word[..^2];
            if (word.EndsWith("fileer", StringComparison.Ordinal))
                return word[..^6] + "filé";
            if (word.EndsWith("file", StringComparison.Ordinal))
                return word[..^4] + "filé";
            if (word.EndsWith("klyftor", StringComparison.Ordinal))
                return word[..^2] + "a";
        }

        return word;
    }

    private static bool HasConflictingPackageSize(string? commentA, string? commentB)
    {
        var sizeA = ExtractPackageSizeKey(commentA);
        var sizeB = ExtractPackageSizeKey(commentB);

        if (sizeA != null && sizeB != null &&
            !string.Equals(sizeA, sizeB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? ExtractPackageSizeKey(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        var match = PackageSizeRegex.Match(comment);
        if (!match.Success)
            return null;

        var numText = match.Groups["num"].Value.Replace(',', '.');
        if (!decimal.TryParse(numText, NumberStyles.Number, CultureInfo.InvariantCulture, out var val))
            return null;

        var unit = NormalizeUnit(match.Groups["unit"].Value);
        if (WeightFactorsToGrams.TryGetValue(unit, out var wFactor))
        {
            return $"W:{Math.Round(val * wFactor, 2)}g";
        }
        if (VolumeFactorsToMilliliters.TryGetValue(unit, out var vFactor))
        {
            return $"V:{Math.Round(val * vFactor, 2)}ml";
        }

        return $"{val}:{unit}";
    }

    private static bool CanSumUnits(string? unitA, string? unitB)
    {
        var normA = NormalizeUnit(unitA);
        var normB = NormalizeUnit(unitB);

        if (string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(normA) || string.IsNullOrWhiteSpace(normB))
            return false;

        if (WeightFactorsToGrams.ContainsKey(normA) && WeightFactorsToGrams.ContainsKey(normB))
            return true;

        if (VolumeFactorsToMilliliters.ContainsKey(normA) && VolumeFactorsToMilliliters.ContainsKey(normB))
            return true;

        return false;
    }

    private static bool TrySumQuantitiesAndUnits(
        decimal minA,
        decimal? maxA,
        string? unitA,
        decimal minB,
        decimal? maxB,
        string? unitB,
        out decimal resultMin,
        out decimal? resultMax,
        out string? resultUnit)
    {
        var normA = NormalizeUnit(unitA);
        var normB = NormalizeUnit(unitB);

        var effMaxA = (maxA.HasValue && maxA.Value > minA) ? maxA.Value : minA;
        var effMaxB = (maxB.HasValue && maxB.Value > minB) ? maxB.Value : minB;

        if (string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase))
        {
            resultMin = Math.Round(minA + minB, 2, MidpointRounding.AwayFromZero);
            var sumMax = Math.Round(effMaxA + effMaxB, 2, MidpointRounding.AwayFromZero);
            resultMax = sumMax > resultMin ? sumMax : null;
            resultUnit = string.IsNullOrWhiteSpace(normA) ? null : normA;
            return true;
        }

        if (WeightFactorsToGrams.TryGetValue(normA, out var wFactorA) &&
            WeightFactorsToGrams.TryGetValue(normB, out var wFactorB))
        {
            resultMin = Math.Round(minA * wFactorA + minB * wFactorB, 2, MidpointRounding.AwayFromZero);
            var sumMax = Math.Round(effMaxA * wFactorA + effMaxB * wFactorB, 2, MidpointRounding.AwayFromZero);
            resultMax = sumMax > resultMin ? sumMax : null;
            resultUnit = "g";
            return true;
        }

        if (VolumeFactorsToMilliliters.TryGetValue(normA, out var vFactorA) &&
            VolumeFactorsToMilliliters.TryGetValue(normB, out var vFactorB))
        {
            var sumMinMl = Math.Round(minA * vFactorA + minB * vFactorB, 2, MidpointRounding.AwayFromZero);
            var sumMaxMl = Math.Round(effMaxA * vFactorA + effMaxB * vFactorB, 2, MidpointRounding.AwayFromZero);

            bool hasKitchenMeasure = normA is "krm" or "tsk" or "msk" || normB is "krm" or "tsk" or "msk";
            bool hasDeciliterOrLiter = normA is "dl" or "l" or "cl" || normB is "dl" or "l" or "cl";

            if (!hasKitchenMeasure && hasDeciliterOrLiter && sumMinMl % 10m == 0m && sumMaxMl % 10m == 0m)
            {
                resultMin = Math.Round(sumMinMl / 100m, 2, MidpointRounding.AwayFromZero);
                var maxDl = Math.Round(sumMaxMl / 100m, 2, MidpointRounding.AwayFromZero);
                resultMax = maxDl > resultMin ? maxDl : null;
                resultUnit = "dl";
            }
            else
            {
                resultMin = sumMinMl;
                resultMax = sumMaxMl > resultMin ? sumMaxMl : null;
                resultUnit = "ml";
            }

            return true;
        }

        resultMin = minA;
        resultMax = maxA;
        resultUnit = string.IsNullOrWhiteSpace(normA) ? null : normA;
        return false;
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

            if (openParenIndex < 0 || depth != 0)
                break;

            var candidateName = name[..openParenIndex].Trim();
            if (string.IsNullOrWhiteSpace(candidateName))
                break;

            var insideText = name.Substring(openParenIndex + 1, name.Length - openParenIndex - 2).Trim();
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

    private static string NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return string.Empty;

        var lower = unit.Trim().TrimEnd('.').ToLowerInvariant();
        return lower switch
        {
            "kilo" or "kilogram" => "kg",
            "gram" => "g",
            "hekto" or "hektogram" => "hg",
            "liter" => "l",
            "deciliter" => "dl",
            "centiliter" => "cl",
            "milliliter" => "ml",
            "matsked" or "matskedar" => "msk",
            "tesked" or "teskedar" => "tsk",
            "kryddmått" => "krm",
            "stycken" or "styck" => "st",
            "förpackning" or "förpackningar" => "förp",
            "pkt" => "paket",
            "klyfta" => "klyftor",
            "portion" or "portioner" => "port",
            "burkar" => "burk",
            "påsar" => "påse",
            "askar" => "ask",
            "flaskor" => "flaska",
            "krukor" => "kruka",
            "skivor" => "skiva",
            "nypor" => "nypa",
            "klickar" => "klick",
            "droppar" => "droppe",
            "kvistar" => "kvist",
            _ => lower
        };
    }

    private static string CapitalizeFirstChar(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], CultureInfo.GetCultureInfo("sv-SE")) + text[1..];

    private static string LowercaseFirstChar(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLower(text[0], CultureInfo.GetCultureInfo("sv-SE")) + text[1..];

    private sealed record IngredientIdentity(
        string CanonicalKey,
        string BaseDisplayName,
        string? LeadingPreparation);
}
