using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;

namespace Receptbanken.Infrastructure.Importers;

public class WebRecipeImporter : IRecipeImporter
{
    private const int MaxRedirects = 5;
    private const int MaxHtmlBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxImageBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>Maximum accepted length (characters) of manually pasted JSON-LD.</summary>
    public const int MaxJsonLdLength = RecipeImportLimits.MaxJsonLdLength;

    private readonly HttpClient _httpClient;
    private readonly JsonLdRecipeExtractor _jsonLdExtractor;
    private readonly HtmlFallbackExtractor _fallbackExtractor;
    private readonly HtmlRecipeTextExtractor _htmlTextExtractor;
    private readonly RecipeTextParser _textParser;
    private readonly IImageStorageService _imageStorage;
    private readonly ILogger<WebRecipeImporter> _logger;

    public WebRecipeImporter(
        HttpClient httpClient,
        JsonLdRecipeExtractor jsonLdExtractor,
        HtmlFallbackExtractor fallbackExtractor,
        IImageStorageService imageStorage,
        ILogger<WebRecipeImporter> logger)
        : this(
            httpClient,
            jsonLdExtractor,
            fallbackExtractor,
            new HtmlRecipeTextExtractor(),
            new RecipeTextParser(new SwedishIngredientParser()),
            imageStorage,
            logger)
    {
    }

    [ActivatorUtilitiesConstructor]
    public WebRecipeImporter(
        HttpClient httpClient,
        JsonLdRecipeExtractor jsonLdExtractor,
        HtmlFallbackExtractor fallbackExtractor,
        HtmlRecipeTextExtractor htmlTextExtractor,
        RecipeTextParser textParser,
        IImageStorageService imageStorage,
        ILogger<WebRecipeImporter> logger)
    {
        _httpClient = httpClient;
        _jsonLdExtractor = jsonLdExtractor;
        _fallbackExtractor = fallbackExtractor;
        _htmlTextExtractor = htmlTextExtractor;
        _textParser = textParser;
        _imageStorage = imageStorage;
        _logger = logger;
    }

    public async Task<RecipeImportResult> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        string? ssrfError = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var targetUri) ||
            !SsrfValidator.IsSafeUri(targetUri, out ssrfError))
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.InvalidUrl,
                ssrfError ?? "Ogiltig eller otillåten webbadress.");
        }

        try
        {
            var fetchResult = await FetchStringSafelyAsync(targetUri, MaxHtmlBytes, cancellationToken);
            if (!fetchResult.Success)
            {
                return RecipeImportResult.Failure(
                    fetchResult.FailureReason,
                    fetchResult.ErrorMessage ?? "Kunde inte hämta receptet från webbplatsen. Du kan istället mata in receptet manuellt.");
            }

            var finalUri = fetchResult.FinalUri;
            var htmlContent = fetchResult.Content;
            var result = ParseFromHtml(htmlContent, finalUri.ToString());

            if (!result.Success || result.DraftRecipe == null)
            {
                return result;
            }

            // If a remote image URL was found, download and validate it through the same SSRF-guarded pipeline
            if (!string.IsNullOrWhiteSpace(result.RemoteImageUrl))
            {
                result.DraftRecipe.ImagePath = await DownloadAndSaveImageAsync(result.RemoteImageUrl, finalUri, cancellationToken);
            }

            return result;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SSRF eller säkerhetsavvisning vid import från {Url}", url);
            return RecipeImportResult.Failure(RecipeImportFailureReason.SecurityRejected, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Tidsgränsen överskreds (timeout) vid hämtning av recept från {Url}", url);
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.Timeout,
                "Tidsgränsen överskreds när receptet skulle hämtas från webbplatsen. Försök igen om en stund.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Nätverks- eller HTTP-fel vid hämtning av recept från {Url} (StatusCode: {StatusCode})", url, ex.StatusCode);
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.NetworkError,
                "Kunde inte ansluta till webbplatsen. Kontrollera adressen och försök igen, eller mata in receptet manuellt.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Oväntat fel vid import från {Url}", url);
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.NetworkError,
                "Kunde inte hämta receptet från webbplatsen. Du kan istället mata in receptet manuellt.");
        }
    }

    public RecipeImportResult ParseFromHtml(string html, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            _logger.LogWarning("Tomt HTML-svar vid recepttolkning för {SourceUrl}", sourceUrl);
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.NoRecipeData,
                "Webbplatsen returnerade ett tomt svar.");
        }

        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        if (IsAccessBlockedDocument(document, html))
        {
            _logger.LogWarning(
                "Webbplatsen returnerade en åtkomstblockeringssida (WAF/CDN Access Denied) istället för receptinnehåll för {SourceUrl} (Title: {PageTitle}, Längd: {HtmlLength} tecken)",
                sourceUrl,
                document.Title ?? "<ingen titel>",
                html.Length);

            return RecipeImportResult.Failure(
                RecipeImportFailureReason.AccessBlocked,
                "Webbplatsen blockerade automatisk hämtning av receptet (åtkomst nekad av sidans botskydd).");
        }

        Recipe? extracted = null;
        string? remoteImage = null;

        var usedJsonLd = _jsonLdExtractor.TryExtract(document, out extracted, out remoteImage);
        var usedFallback = false;
        if (!usedJsonLd)
        {
            usedFallback = _fallbackExtractor.TryExtract(document, out extracted, out remoteImage);
        }

        if (extracted == null || string.IsNullOrWhiteSpace(extracted.Title))
        {
            var jsonLdScriptCount = document.QuerySelectorAll("script[type='application/ld+json']").Length;
            _logger.LogWarning(
                "Tolkningsfel: Inget strukturerat recept kunde extraheras från {SourceUrl} (Title: {PageTitle}, HtmlLength: {HtmlLength}, JsonLdScripts: {JsonLdCount})",
                sourceUrl,
                document.Title ?? "<ingen titel>",
                html.Length,
                jsonLdScriptCount);

            return RecipeImportResult.Failure(
                RecipeImportFailureReason.NoRecipeData,
                "Inget strukturerat recept kunde hittas på sidan.");
        }

        _logger.LogInformation(
            "Extraherade recept '{RecipeTitle}' från {SourceUrl} via {ExtractorType} ({IngredientCount} ingredienser, {StepCount} steg)",
            extracted.Title,
            sourceUrl,
            usedJsonLd ? "JSON-LD" : (usedFallback ? "HTML-fallback" : "Okänd"),
            extracted.Ingredients.Count,
            extracted.Instructions.Count);

        return BuildSuccessResult(extracted, remoteImage, sourceUrl);
    }

    public RecipeImportResult ParseFromJsonLd(string? jsonLd, string? fallbackSourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(jsonLd))
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.EmptyInput,
                "Klistra in receptdata (JSON-LD) innan du tolkar.");
        }

        if (jsonLd.Length > MaxJsonLdLength)
        {
            _logger.LogWarning(
                "Manuellt inklistrad JSON-LD avvisades: {Length} tecken överskrider maxgränsen {MaxLength}",
                jsonLd.Length,
                MaxJsonLdLength);

            return RecipeImportResult.Failure(
                RecipeImportFailureReason.InputTooLarge,
                $"Den inklistrade texten är för stor (max {MaxJsonLdLength / 1024} kB). Kopiera endast receptets strukturerade data.");
        }

        // Shared JSON-LD mapping: exactly the same logic as for script blocks found in fetched HTML.
        // No HTTP requests are made here; remote image URLs are intentionally not downloaded.
        var extraction = _jsonLdExtractor.ExtractFromJson(jsonLd);

        switch (extraction.Status)
        {
            case JsonLdExtractionStatus.InvalidJson:
                _logger.LogInformation("Manuellt inklistrad JSON-LD kunde inte tolkas som giltig JSON ({Length} tecken)", jsonLd.Length);
                return RecipeImportResult.Failure(
                    RecipeImportFailureReason.InvalidJson,
                    "Texten är inte giltig JSON. Kontrollera att du har kopierat hela resultatet och försök igen.");

            case JsonLdExtractionStatus.ParseError:
                _logger.LogWarning(extraction.Error, "Oväntad datastruktur i manuellt inklistrad JSON-LD ({Length} tecken)", jsonLd.Length);
                return RecipeImportResult.Failure(
                    RecipeImportFailureReason.ParseError,
                    "Receptdatan innehåller en oväntad struktur och kunde inte tolkas.");

            case JsonLdExtractionStatus.NoRecipe:
                _logger.LogInformation(
                    "Manuellt inklistrad JSON-LD innehöll inget recept ({Length} tecken, {CandidateCount} receptkandidater utan namn)",
                    jsonLd.Length,
                    extraction.RecipeCandidateCount);
                return RecipeImportResult.Failure(
                    RecipeImportFailureReason.NoRecipeData,
                    "Inget recept hittades i den inklistrade datan. Webbplatsen kanske inte publicerar strukturerad receptdata – skapa i så fall receptet manuellt.");
        }

        var recipe = extraction.Recipe!;
        var sourceUrl = ResolveSourceUrl(extraction.RecipeUrl, fallbackSourceUrl);

        _logger.LogInformation(
            "Extraherade recept '{RecipeTitle}' från manuellt inklistrad JSON-LD ({IngredientCount} ingredienser, {StepCount} steg, {CandidateCount} receptkandidater, källa: {SourceUrl})",
            recipe.Title,
            recipe.Ingredients.Count,
            recipe.Instructions.Count,
            extraction.RecipeCandidateCount,
            sourceUrl ?? "<ingen>");

        var result = BuildSuccessResult(recipe, extraction.RemoteImageUrl, sourceUrl);
        result.HasImageProperty = extraction.HasImageProperty;
        return result;
    }

    /// <summary>
    /// Parses manually pasted recipe text or HTML fragment (without making any HTTP requests),
    /// using the HTML extractor if HTML markup is present, followed by the rule-based recipe text parser.
    /// </summary>
    /// <param name="textOrHtml">Untrusted recipe text or HTML fragment pasted by the user.</param>
    /// <param name="fallbackSourceUrl">Previously entered URL or optional source URL.</param>
    public RecipeImportResult ParseFromText(string? textOrHtml, string? fallbackSourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(textOrHtml))
        {
            return RecipeImportResult.Failure(
                RecipeImportFailureReason.EmptyInput,
                "Klistra in recepttext eller HTML innan du tolkar.");
        }

        if (textOrHtml.Length > RecipeImportLimits.MaxRecipeTextLength)
        {
            _logger.LogWarning(
                "Manuellt inklistrad recepttext avvisades: {Length} tecken överskrider maxgränsen {MaxLength}",
                textOrHtml.Length,
                RecipeImportLimits.MaxRecipeTextLength);

            return RecipeImportResult.Failure(
                RecipeImportFailureReason.InputTooLarge,
                $"Den inklistrade texten är för stor (max {RecipeImportLimits.MaxRecipeTextLength / 1024} kB). Kopiera endast receptets text eller HTML.");
        }

        string structuredText = textOrHtml;
        string? extractedImageUrl = null;
        string? extractedSourceUrl = null;

        if (HtmlRecipeTextExtractor.IsHtml(textOrHtml))
        {
            var extraction = _htmlTextExtractor.Extract(textOrHtml);
            if (!extraction.Success || string.IsNullOrWhiteSpace(extraction.ExtractedText))
            {
                return RecipeImportResult.Failure(
                    RecipeImportFailureReason.NoRecipeData,
                    "Inget läsbart receptinnehåll kunde extraheras från HTML-koden.");
            }

            structuredText = extraction.ExtractedText;
            extractedImageUrl = extraction.RemoteImageUrl;
            extractedSourceUrl = extraction.SourceUrl;
        }

        var resolvedSourceUrl = ResolveSourceUrl(extractedSourceUrl, fallbackSourceUrl);
        var parseResult = _textParser.Parse(structuredText, resolvedSourceUrl, extractedImageUrl);

        if (parseResult.Success && parseResult.DraftRecipe != null)
        {
            _logger.LogInformation(
                "Extraherade recept '{RecipeTitle}' från manuellt inklistrad text ({IngredientCount} ingredienser, {StepCount} steg, källa: {SourceUrl})",
                parseResult.DraftRecipe.Title,
                parseResult.DraftRecipe.Ingredients.Count,
                parseResult.DraftRecipe.Instructions.Count,
                resolvedSourceUrl ?? "<ingen>");
        }
        else
        {
            _logger.LogInformation(
                "Kunde inte extrahera recept från manuellt inklistrad text ({Length} tecken, orsak: {Reason})",
                textOrHtml.Length,
                parseResult.FailureReason);
        }

        return parseResult;
    }

    /// <summary>
    /// Source URL rule for manual JSON-LD: 1) JSON-LD "url" if valid http/https,
    /// 2) otherwise the previously entered URL if valid, 3) otherwise empty.
    /// </summary>
    internal static string? ResolveSourceUrl(string? jsonLdUrl, string? fallbackUrl)
    {
        foreach (var candidate in new[] { jsonLdUrl, fallbackUrl })
        {
            if (Uri.TryCreate(candidate?.Trim(), UriKind.Absolute, out var uri) &&
                SsrfValidator.IsSafeUri(uri, out _))
            {
                return uri.ToString();
            }
        }

        return null;
    }

    private static RecipeImportResult BuildSuccessResult(Recipe recipe, string? remoteImageUrl, string? sourceUrl)
    {
        recipe.SourceUrl = sourceUrl;
        recipe.SourceHost = null;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            recipe.SourceHost = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        }

        return new RecipeImportResult
        {
            Success = true,
            FailureReason = RecipeImportFailureReason.None,
            DraftRecipe = recipe,
            RemoteImageUrl = remoteImageUrl
        };
    }

    private static bool IsAccessBlockedDocument(AngleSharp.Html.Dom.IHtmlDocument document, string rawHtml)
    {
        var title = document.Title?.Trim() ?? string.Empty;
        if (title.Equals("Access Denied", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (rawHtml.Length < 4096 &&
            (rawHtml.Contains("errors.edgesuite.net", StringComparison.OrdinalIgnoreCase) ||
             rawHtml.Contains("You don't have permission to access", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public async Task<string?> DownloadAndSaveImageAsync(string imageUrl, Uri? baseUri = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        try
        {
            Uri? imageUri;
            if (baseUri != null)
            {
                if (!Uri.TryCreate(baseUri, imageUrl.Trim(), out imageUri))
                    return null;
            }
            else
            {
                if (!Uri.TryCreate(imageUrl.Trim(), UriKind.Absolute, out imageUri))
                    return null;
            }

            if (!SsrfValidator.IsSafeUri(imageUri, out var ssrfError))
            {
                _logger.LogWarning("Avvisade osäker bild-URL vid receptimport: {ImageUrl} ({Reason})", imageUrl, ssrfError);
                return null;
            }

            using var imageStream = await FetchStreamSafelyAsync(imageUri, MaxImageBytes, cancellationToken);
            var ext = Path.GetExtension(imageUri.AbsolutePath);
            if (!_imageStorage.IsValidImageExtension(ext))
            {
                ext = ".jpg";
            }

            return await _imageStorage.SaveImageAsync(imageStream, $"imported{ext}", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kunde inte ladda ner bild från {ImageUrl}; fortsätter utan bild.", imageUrl);
            return null;
        }
    }

    private sealed record FetchStringResult(
        bool Success,
        Uri FinalUri,
        string Content,
        string? ErrorMessage = null,
        RecipeImportFailureReason FailureReason = RecipeImportFailureReason.None);

    private async Task<FetchStringResult> FetchStringSafelyAsync(Uri startUri, int maxBytes, CancellationToken cancellationToken)
    {
        var currentUri = startUri;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!SsrfValidator.IsSafeUri(currentUri, out var reason))
            {
                throw new InvalidOperationException(reason ?? "Otillåten omdirigeringsadress.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("Receptbanken/1.0 (+SelfHosted)");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("sv-SE,sv;q=0.9,en;q=0.8");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Omdirigering saknar mål-URL.");

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                _logger.LogDebug(
                    "HTTP-omdirigering ({Hop}/{MaxRedirects}): {StatusCode} från {FromUri} till {ToUri}",
                    hop + 1,
                    MaxRedirects,
                    (int)response.StatusCode,
                    currentUri,
                    nextUri);

                currentUri = nextUri;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var host = currentUri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning(
                        "HTTP-blockering vid receptimport från {Url}: webbplatsen svarade med HTTP {StatusCode} ({ReasonPhrase})",
                        currentUri,
                        statusCode,
                        response.ReasonPhrase);

                    var isRateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
                    return new FetchStringResult(
                        Success: false,
                        FinalUri: currentUri,
                        Content: string.Empty,
                        ErrorMessage: isRateLimited
                            ? $"Webbplatsen ({host}) begränsar antalet förfrågningar just nu (HTTP 429 TooManyRequests)."
                            : $"Webbplatsen ({host}) blockerade automatisk hämtning av receptet (HTTP {statusCode} {response.StatusCode}).",
                        FailureReason: isRateLimited
                            ? RecipeImportFailureReason.RateLimited
                            : RecipeImportFailureReason.AccessBlocked);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning(
                        "Receptsidan hittades inte (HTTP 404 Not Found) för {Url}",
                        currentUri);

                    return new FetchStringResult(
                        Success: false,
                        FinalUri: currentUri,
                        Content: string.Empty,
                        ErrorMessage: "Receptsidan kunde inte hittas (HTTP 404). Kontrollera att länken är korrekt eller mata in receptet manuellt.",
                        FailureReason: RecipeImportFailureReason.NotFound);
                }

                _logger.LogWarning(
                    "Misslyckat HTTP-svar vid receptimport från {Url}: HTTP {StatusCode} ({ReasonPhrase})",
                    currentUri,
                    statusCode,
                    response.ReasonPhrase);

                return new FetchStringResult(
                    Success: false,
                    FinalUri: currentUri,
                    Content: string.Empty,
                    ErrorMessage: $"Webbplatsen returnerade ett fel (HTTP {statusCode}). Du kan istället mata in receptet manuellt.",
                    FailureReason: RecipeImportFailureReason.HttpError);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int totalRead = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new InvalidOperationException("Svaret från webbplatsen överskred maxgränsen (5 MB).");
                }
                memory.Write(buffer, 0, read);
            }

            memory.Position = 0;
            using var reader = new StreamReader(memory);
            var text = await reader.ReadToEndAsync(cancellationToken);
            return new FetchStringResult(Success: true, FinalUri: currentUri, Content: text);
        }

        throw new InvalidOperationException("För många HTTP-omdirigeringar (max 5 tillåtna).");
    }

    private async Task<MemoryStream> FetchStreamSafelyAsync(Uri startUri, int maxBytes, CancellationToken cancellationToken)
    {
        var currentUri = startUri;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!SsrfValidator.IsSafeUri(currentUri, out var reason))
            {
                throw new InvalidOperationException(reason ?? "Otillåten bildadress.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("Receptbanken/1.0 (+SelfHosted)");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Omdirigering för bild saknar mål-URL.");

                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var memory = new MemoryStream();
            var buffer = new byte[8192];
            int totalRead = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                {
                    memory.Dispose();
                    throw new InvalidOperationException("Bildfilen överskred maxgränsen (10 MB).");
                }
                memory.Write(buffer, 0, read);
            }

            memory.Position = 0;
            return memory;
        }

        throw new InvalidOperationException("För många HTTP-omdirigeringar vid bildhämtning.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
