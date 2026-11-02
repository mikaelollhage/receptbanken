using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Receptbanken.Infrastructure.Importers;

public class HtmlExtractionResult
{
    public bool Success { get; set; }
    public string ExtractedText { get; set; } = string.Empty;
    public string? RemoteImageUrl { get; set; }
    public string? SourceUrl { get; set; }
}

/// <summary>
/// Extracts clean, structured plain text from untrusted HTML fragments or documents,
/// preserving heading levels, paragraphs, and list structures while discarding scripts,
/// stylesheets, navigation, footers, forms, hidden elements, and prominent ad blocks.
/// </summary>
public class HtmlRecipeTextExtractor
{
    private static readonly Regex HtmlDetectionRegex = new(
        @"<\s*(?:!DOCTYPE|html|head|body|h[1-6]|p|div|ul|ol|li|table|section|article|span|br\b|header|footer)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AdTokenRegex = new(
        @"(^|[_-])(ad|ads|advert|advertisement|banner|sponsor|sponsored|commercial|promo)([_-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsHtml(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        return HtmlDetectionRegex.IsMatch(input);
    }

    public HtmlExtractionResult Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new HtmlExtractionResult { Success = false };
        }

        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(html);

        var remoteImageUrl = ExtractImage(doc);
        var sourceUrl = ExtractSourceUrl(doc);

        var root = (INode?)doc.Body ?? doc.DocumentElement;
        if (root == null)
        {
            return new HtmlExtractionResult { Success = false };
        }

        // 1. Remove unwanted tags
        var unwantedNodes = doc.QuerySelectorAll("script, style, noscript, svg, canvas, iframe, form, nav, footer, header, aside, menu, dialog, button, input, select, textarea, audio, video").ToList();
        foreach (var node in unwantedNodes)
        {
            node.Remove();
        }

        // 1b. Remove noise containers (sidebars, comments, social sharing, newsletters, cookies, breadcrumbs)
        var noiseContainers = doc.QuerySelectorAll("[class*='sidebar'], [id*='sidebar'], [class*='comment'], [id*='comment'], [class*='social'], [id*='social'], [class*='share'], [id*='share'], [class*='newsletter'], [class*='breadcrumb'], [class*='cookie'], [id*='cookie'], [class*='widget-ad'], [class*='ad-banner'], [class*='commercial'], [class*='author-box'], [class*='entry-meta']").ToList();
        foreach (var node in noiseContainers)
        {
            node.Remove();
        }

        // 2. Remove hidden elements
        var hiddenNodes = doc.QuerySelectorAll("[hidden], [aria-hidden='true']").ToList();
        foreach (var node in hiddenNodes)
        {
            node.Remove();
        }

        var styledHidden = doc.QuerySelectorAll("[style]").Where(el =>
        {
            var style = el.GetAttribute("style") ?? string.Empty;
            return style.Contains("display:none", StringComparison.OrdinalIgnoreCase) ||
                   style.Contains("display: none", StringComparison.OrdinalIgnoreCase) ||
                   style.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase) ||
                   style.Contains("visibility: hidden", StringComparison.OrdinalIgnoreCase);
        }).ToList();

        foreach (var node in styledHidden)
        {
            node.Remove();
        }

        // 3. Remove prominent ad/commercial blocks
        var adCandidates = doc.QuerySelectorAll("[class], [id]").Where(el =>
        {
            var cls = el.GetAttribute("class") ?? string.Empty;
            var id = el.GetAttribute("id") ?? string.Empty;
            return AdTokenRegex.IsMatch(cls) || AdTokenRegex.IsMatch(id);
        }).ToList();

        foreach (var node in adCandidates)
        {
            node.Remove();
        }

        // 4. Walk DOM and produce structured plain text
        var sb = new StringBuilder();
        SerializeNode(root, sb);

        var structuredText = NormalizeOutputText(sb.ToString());

        return new HtmlExtractionResult
        {
            Success = !string.IsNullOrWhiteSpace(structuredText),
            ExtractedText = structuredText,
            RemoteImageUrl = remoteImageUrl,
            SourceUrl = sourceUrl
        };
    }

    private static void SerializeNode(INode node, StringBuilder sb)
    {
        if (node is IText textNode)
        {
            var text = WebUtility.HtmlDecode(textNode.TextContent);
            if (!string.IsNullOrEmpty(text))
            {
                // Collapse whitespace inside text nodes but preserve leading/trailing if between inline tags
                var cleaned = Regex.Replace(text, @"[ \t\r\n]+", " ");
                sb.Append(cleaned);
            }
            return;
        }

        if (node is IElement element)
        {
            var tag = element.TagName.ToLowerInvariant();

            if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                var headingText = WebUtility.HtmlDecode(element.TextContent)?.Trim();
                if (!string.IsNullOrWhiteSpace(headingText))
                {
                    sb.Append("\n\n## ").Append(headingText).Append("\n\n");
                }
                return;
            }

            if (tag is "br")
            {
                sb.Append('\n');
                return;
            }

            if (tag is "p")
            {
                sb.Append('\n');
                foreach (var child in element.ChildNodes)
                {
                    SerializeNode(child, sb);
                }
                sb.Append('\n');
                return;
            }

            if (tag is "ul")
            {
                sb.Append('\n');
                foreach (var child in element.Children)
                {
                    if (child.TagName.Equals("li", StringComparison.OrdinalIgnoreCase))
                    {
                        var liText = WebUtility.HtmlDecode(child.TextContent)?.Trim();
                        if (!string.IsNullOrWhiteSpace(liText))
                        {
                            if (!liText.StartsWith('-') && !liText.StartsWith('•') && !liText.StartsWith('*'))
                            {
                                sb.Append("- ").Append(liText).Append('\n');
                            }
                            else
                            {
                                sb.Append(liText).Append('\n');
                            }
                        }
                    }
                    else
                    {
                        SerializeNode(child, sb);
                    }
                }
                sb.Append('\n');
                return;
            }

            if (tag is "ol")
            {
                sb.Append('\n');
                int index = 1;
                foreach (var child in element.Children)
                {
                    if (child.TagName.Equals("li", StringComparison.OrdinalIgnoreCase))
                    {
                        var liText = WebUtility.HtmlDecode(child.TextContent)?.Trim();
                        if (!string.IsNullOrWhiteSpace(liText))
                        {
                            if (!Regex.IsMatch(liText, @"^\d+[\.\)\:]"))
                            {
                                sb.Append(index++).Append(". ").Append(liText).Append('\n');
                            }
                            else
                            {
                                sb.Append(liText).Append('\n');
                            }
                        }
                    }
                    else
                    {
                        SerializeNode(child, sb);
                    }
                }
                sb.Append('\n');
                return;
            }

            if (tag is "li")
            {
                sb.Append("\n- ");
                foreach (var child in element.ChildNodes)
                {
                    SerializeNode(child, sb);
                }
                sb.Append('\n');
                return;
            }

            if (tag is "div" or "section" or "article" or "main" or "tr")
            {
                sb.Append('\n');
                foreach (var child in element.ChildNodes)
                {
                    SerializeNode(child, sb);
                }
                sb.Append('\n');
                return;
            }

            // Inline or container elements: recurse
            foreach (var child in element.ChildNodes)
            {
                SerializeNode(child, sb);
            }
        }
    }

    private static string NormalizeOutputText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        var lines = rawText
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n');

        var sb = new StringBuilder();
        int consecutiveEmptyLines = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                consecutiveEmptyLines++;
                if (consecutiveEmptyLines <= 1)
                {
                    sb.AppendLine();
                }
            }
            else
            {
                consecutiveEmptyLines = 0;
                sb.AppendLine(trimmed);
            }
        }

        return sb.ToString().Trim();
    }

    private static string? ExtractImage(IHtmlDocument doc)
    {
        // 1. Check og:image or twitter:image metadata
        var ogImage = doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                   ?? doc.QuerySelector("meta[name='twitter:image']")?.GetAttribute("content");

        if (!string.IsNullOrWhiteSpace(ogImage) &&
            Uri.TryCreate(ogImage.Trim(), UriKind.Absolute, out var ogUri) &&
            (ogUri.Scheme == Uri.UriSchemeHttp || ogUri.Scheme == Uri.UriSchemeHttps) &&
            SsrfValidator.IsSafeUri(ogUri, out _))
        {
            return ogUri.AbsoluteUri;
        }

        // 2. Check img tags within the document body, skipping icons, tracking pixels, avatars
        foreach (var img in doc.QuerySelectorAll("img"))
        {
            var src = img.GetAttribute("src") ?? img.GetAttribute("data-src") ?? img.GetAttribute("data-lazy-src");
            if (string.IsNullOrWhiteSpace(src))
                continue;

            var cls = img.GetAttribute("class") ?? string.Empty;
            if (cls.Contains("avatar", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("gravatar.com", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("tracking", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("1x1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(src.Trim(), UriKind.Absolute, out var imgUri) &&
                (imgUri.Scheme == Uri.UriSchemeHttp || imgUri.Scheme == Uri.UriSchemeHttps) &&
                SsrfValidator.IsSafeUri(imgUri, out _))
            {
                return imgUri.AbsoluteUri;
            }
        }

        return null;
    }

    private static string? ExtractSourceUrl(IHtmlDocument doc)
    {
        var canonical = doc.QuerySelector("link[rel='canonical']")?.GetAttribute("href")
                     ?? doc.QuerySelector("meta[property='og:url']")?.GetAttribute("content");

        if (!string.IsNullOrWhiteSpace(canonical) &&
            Uri.TryCreate(canonical.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            SsrfValidator.IsSafeUri(uri, out _))
        {
            return uri.AbsoluteUri;
        }

        return null;
    }
}
