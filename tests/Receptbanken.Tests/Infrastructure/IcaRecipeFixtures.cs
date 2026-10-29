namespace Receptbanken.Tests.Infrastructure;

/// <summary>
/// Offline, representative fixtures modelled on ICA's "Marry me chicken" (730140) page structure:
/// a Recipe JSON-LD block with id="recipeSchemaId" (18 ingredient lines, 8 steps) alongside
/// unrelated JSON-LD types. Used as one of several test cases for the generic JSON-LD import.
/// </summary>
public static class IcaRecipeFixtures
{
    public const string PageUrl = "https://www.ica.se/recept/marry-me-chicken-730140/";
    public const string ImageUrl = "https://assets.icanet.se/e_sharpen:80,q_auto,dpr_1.25,w_718,h_718,c_lfill/imagevaultfiles/id_254100/cf_259/marry_me_chicken.jpg";
    public const int IngredientCount = 18;
    public const int StepCount = 8;

    public const string RecipeJson = """
        {
          "@context": "https://schema.org",
          "@type": "Recipe",
          "name": "Marry me chicken",
          "description": "Krämig kyckling med soltorkade tomater, vitlök och parmesan – en rätt som sägs vara så god att man får ett frieri.",
          "image": "https://assets.icanet.se/e_sharpen:80,q_auto,dpr_1.25,w_718,h_718,c_lfill/imagevaultfiles/id_254100/cf_259/marry_me_chicken.jpg",
          "recipeYield": "4 portioner",
          "totalTime": "PT45M",
          "recipeCategory": ["Middag", "Kyckling"],
          "author": { "@type": "Organization", "name": "ICA Provkök" },
          "url": "https://www.ica.se/recept/marry-me-chicken-730140/",
          "recipeIngredient": [
            "600 g kycklinglårfilé",
            "1 tsk salt",
            "1 krm svartpeppar",
            "2 msk smör",
            "1 gul lök",
            "3 vitlöksklyftor",
            "1 msk tomatpuré",
            "2 tsk torkad oregano",
            "1 tsk paprikapulver",
            "0,5 tsk chiliflakes",
            "3 dl matlagningsgrädde",
            "1 dl kycklingbuljong",
            "1 dl riven parmesanost",
            "1 dl soltorkade tomater, strimlade",
            "1 dl färsk basilika",
            "400 g pasta",
            "1 msk olivolja",
            "1 citron"
          ],
          "recipeInstructions": [
            { "@type": "HowToStep", "text": "Krydda kycklingen med salt och peppar." },
            { "@type": "HowToStep", "text": "Bryn kycklingen i smör i en stekpanna på medelhög värme, ca 3 minuter per sida. Lägg upp." },
            { "@type": "HowToStep", "text": "Skala och hacka lök och vitlök. Fräs i samma panna tills löken mjuknat." },
            { "@type": "HowToStep", "text": "Rör ner tomatpuré, oregano, paprikapulver och chiliflakes och fräs ytterligare en minut." },
            { "@type": "HowToStep", "text": "Tillsätt grädde och buljong och låt koka upp." },
            { "@type": "HowToStep", "text": "Rör ner parmesan och soltorkade tomater. Lägg tillbaka kycklingen och låt sjuda i ca 10 minuter." },
            { "@type": "HowToStep", "text": "Koka pastan enligt anvisningen på förpackningen." },
            { "@type": "HowToStep", "text": "Toppa med färsk basilika och riven citronskal och servera med pastan." }
          ]
        }
        """;

    /// <summary>Output shape of the generic browser console snippet: a JSON array of all JSON-LD blocks.</summary>
    public static readonly string ConsoleArrayJson = $$"""
        [
          { "@context": "https://schema.org", "@type": "Organization", "name": "ICA", "url": "https://www.ica.se/" },
          { "@context": "https://schema.org", "@type": "WebSite", "name": "ICA.se", "url": "https://www.ica.se/" },
          {
            "@context": "https://schema.org",
            "@type": "BreadcrumbList",
            "itemListElement": [
              { "@type": "ListItem", "position": 1, "name": "Recept", "item": "https://www.ica.se/recept/" }
            ]
          },
          {{RecipeJson}}
        ]
        """;

    public static readonly string Html = $$"""
        <!DOCTYPE html>
        <html lang="sv">
        <head>
            <title>Marry me chicken | Recept ICA.se</title>
            <script type="application/ld+json">{ "@context": "https://schema.org", "@type": "Organization", "name": "ICA" }</script>
            <script type="application/ld+json" id="recipeSchemaId">{{RecipeJson}}</script>
        </head>
        <body></body>
        </html>
        """;

    public const string Akamai403Body = """
        <HTML><HEAD>
        <TITLE>Access Denied</TITLE>
        </HEAD><BODY>
        <H1>Access Denied</H1>
        You don't have permission to access "http&#58;&#47;&#47;www&#46;ica&#46;se&#47;recept&#47;marry&#45;me&#45;chicken&#45;730140&#47;" on this server.<P>
        Reference&#32;&#35;18&#46;85d4f748&#46;1776715743&#46;39647bc6
        <P>https&#58;&#47;&#47;errors&#46;edgesuite&#46;net&#47;18&#46;85d4f748&#46;1776715743&#46;39647bc6</P>
        </BODY>
        </HTML>
        """;
}

/// <summary>Reusable fake HTTP handler that records the number of outgoing requests.</summary>
public sealed class TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _requestCount;

    public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((req, _) => Task.FromResult(responder(req)))
    {
    }

    public int RequestCount => _requestCount;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        return responder(request, cancellationToken);
    }
}

