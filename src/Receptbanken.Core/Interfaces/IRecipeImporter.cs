using Receptbanken.Core.Models;

namespace Receptbanken.Core.Interfaces;

/// <summary>
/// Typed classification of why a recipe import failed. UI logic must use this value
/// instead of inspecting the (Swedish) user-facing error message.
/// </summary>
public enum RecipeImportFailureReason
{
    None,
    InvalidUrl,
    SecurityRejected,
    AccessBlocked,
    RateLimited,
    NotFound,
    HttpError,
    NetworkError,
    Timeout,
    NoRecipeData,
    EmptyInput,
    InvalidJson,
    InputTooLarge,
    ParseError
}

public static class RecipeImportLimits
{
    /// <summary>Maximum accepted length (characters) of manually pasted JSON-LD.</summary>
    public const int MaxJsonLdLength = 256 * 1024;
}

public static class RecipeImportFailureReasonExtensions
{
    /// <summary>
    /// True when the website blocked automatic fetching or delivered no usable recipe data,
    /// meaning that manually pasted structured data (JSON-LD) is a meaningful fallback.
    /// </summary>
    public static bool SuggestsStructuredDataFallback(this RecipeImportFailureReason reason) =>
        reason is RecipeImportFailureReason.AccessBlocked
            or RecipeImportFailureReason.RateLimited
            or RecipeImportFailureReason.NoRecipeData;

    /// <summary>
    /// True for transient failures where simply retrying the same request may succeed.
    /// </summary>
    public static bool IsTransient(this RecipeImportFailureReason reason) =>
        reason is RecipeImportFailureReason.NetworkError
            or RecipeImportFailureReason.Timeout;
}

public class RecipeImportResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public RecipeImportFailureReason FailureReason { get; set; } = RecipeImportFailureReason.None;
    public Recipe? DraftRecipe { get; set; }
    public string? RemoteImageUrl { get; set; }
    public bool HasImageProperty { get; set; }

    public static RecipeImportResult Failure(RecipeImportFailureReason reason, string errorMessage) => new()
    {
        Success = false,
        FailureReason = reason,
        ErrorMessage = errorMessage
    };
}

public interface IRecipeImporter
{
    Task<RecipeImportResult> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default);
    RecipeImportResult ParseFromHtml(string html, string sourceUrl);

    /// <summary>
    /// Parses manually pasted JSON-LD (a single object, an object with @graph, or an array)
    /// using the same recipe mapping as the URL/HTML import. Never performs any HTTP request.
    /// </summary>
    /// <param name="jsonLd">Untrusted JSON-LD text pasted by the user.</param>
    /// <param name="fallbackSourceUrl">Previously entered URL, used as source if JSON-LD lacks a valid "url".</param>
    RecipeImportResult ParseFromJsonLd(string? jsonLd, string? fallbackSourceUrl = null);

    /// <summary>
    /// Downloads and persists a remote recipe image into local storage safely (with SSRF protection,
    /// redirect validation, timeout, size limits, and image validation/WebP conversion).
    /// Returns the relative image path (e.g. images/recipes/{guid}.webp) if successful, or null on failure.
    /// Never throws exceptions out to the caller.
    /// </summary>
    Task<string?> DownloadAndSaveImageAsync(string imageUrl, Uri? baseUri = null, CancellationToken cancellationToken = default);
}
