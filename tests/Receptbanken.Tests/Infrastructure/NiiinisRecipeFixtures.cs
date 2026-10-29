namespace Receptbanken.Tests.Infrastructure;

/// <summary>
/// Offline, representative fixtures modelled on the structure of the Niiinis / Expressen blog post
/// (https://alltommat.expressen.se/blogg/niiinis/min-godaste-kottfarssas).
/// 
/// Note: This is an offline, representative test example created from known structure and contents,
/// not an exact copy of the original remote webpage.
/// Characteristics:
/// - No JSON-LD data
/// - Surrounding blog article context with narrative text and promotional links (~3 360 chars)
/// - Ad banners and commercial markup
/// - Recipe title with portions: "Ninas Köttfärssås, 4 p"
/// - 13 ingredients represented as paragraph &lt;p&gt; elements (rather than &lt;ul&gt;&lt;li&gt;)
/// - Instruction heading "Gör såhär:"
/// - 4 numbered steps: "1) ...", "2) ...", "3) ...", "4) ..."
/// - Serving tip: "Tips: Riv över parmesan innan servering."
/// </summary>
public static class NiiinisRecipeFixtures
{
    public const string PageUrl = "https://alltommat.expressen.se/blogg/niiinis/min-godaste-kottfarssas";
    public const string RecipeTitle = "Ninas Köttfärssås";
    public const int Servings = 4;
    public const int IngredientCount = 13;
    public const int StepCount = 4;
    public const string ServingTip = "Tips: Riv över parmesan innan servering.";

    public const string Html = """
        <!DOCTYPE html>
        <html lang="sv">
        <head>
            <meta charset="utf-8" />
            <title>Min godaste köttfärssås någonsin | Niiinis blogg</title>
            <meta name="description" content="Det här receptet på köttfärssås har jag gjort i så många år nu och det slår aldrig fel." />
            <link rel="canonical" href="https://alltommat.expressen.se/blogg/niiinis/min-godaste-kottfarssas" />
            <meta property="og:image" content="https://images.expressen.se/images/niiinis-kottfarssas.jpg" />
        </head>
        <body>
            <header>
                <nav class="main-nav">
                    <a href="/">Hem</a>
                    <a href="/bloggar">Bloggar</a>
                    <a href="/recept">Recept</a>
                </nav>
            </header>

            <div class="ad-banner top-banner" id="banner-top-ad">
                <p>Annons: Köp köksredskap med 20% rabatt hos KitchenStore!</p>
            </div>

            <article class="blog-entry">
                <h1>Min godaste köttfärssås någonsin</h1>
                <div class="entry-meta">Publicerad: 28 oktober 2026 | Kategori: Vardagsmat</div>

                <p>Hej alla fina läsare! Hoppas att ni har haft en alldeles underbar vecka så här långt. Idag tänkte jag dela med mig av ett recept som ligger mig otroligt varmt om hjärtat. Jag vet inte hur många gånger jag har fått frågan om hur jag lagar min köttfärssås, och nu kände jag att det verkligen var dags att skriva ner hela receptet ordentligt.</p>

                <p>Det här är en sån där köttfärssås som bara blir godare och godare ju längre den får stå och puttra på spisen. Hemligheten ligger i kombinationen av tomatpuré, kalvfond och en skvätt vispgrädde som rundar av smakerna helt perfekt. Mina barn fullkomligt älskar den och brukar alltid ta två portioner varje gång vi serverar den till middag här hemma.</p>

                <div class="commercial-box" style="display:block;">
                    <p>Sponsrat inlägg: Glöm inte att använda rabattkoden NINA20 när ni handlar hos Kryddboden under hela november månad.</p>
                </div>

                <p>Jag brukar alltid servera köttfärssåsen med nykokt spaghetti eller tagliatelle, en krispig grönsallad och massor av riven parmesanost på toppen. Ibland rostar jag även lite vitlöksbröd till om det är helg eller fredagsmys. Prova receptet och berätta gärna vad ni tyckte i kommentarsfältet!</p>

                <h2>Ninas Köttfärssås, 4 p</h2>

                <p>600 g nötfärs</p>
                <p>1 lök</p>
                <p>2 klyftor vitlök</p>
                <p>1 msk smör till stekning</p>
                <p>1 burk krossade tomater</p>
                <p>2 msk tomatpuré</p>
                <p>1 msk kalvfond</p>
                <p>1 tsk torkad oregano</p>
                <p>1 tsk torkad basilika</p>
                <p>1 dl vispgrädde</p>
                <p>1 tsk kinesisk soja</p>
                <p>salt och svartpeppar</p>
                <p>1 nypa socker</p>

                <h2>Gör såhär:</h2>

                <p>1) Börja med att steka köttfärsen i smör i en rymlig stekpanna tills den fått fin färg.</p>
                <p>2) Hacka löken och finhacka vitlöken, låt steka med köttfärsen de sista minuterna.</p>
                <p>3) Häll i krossade tomater, tomatpuré, kalvfond, oregano, basilika och soja. Låt puttra under lock i minst 20 minuter.</p>
                <p>4) Häll i grädde och smaka av med salt, peppar och en nypa socker. Låt sjuda ytterligare 5 minuter.</p>

                <p>Tips: Riv över parmesan innan servering.</p>

                <p>Nu önskar jag er alla en underbar och mysig middag! Lämna jättegärna en liten kommentar om vad ni tyckte om receptet, det värmer alltid i hjärtat att läsa era hälsningar. Kram på er alla!</p>
            </article>

            <aside class="sidebar-promos">
                <div class="widget-ad">
                    <h3>Populärt i veckan</h3>
                    <p>Missa inte mina saftiga kanelbullar och fluffiga pannkakor.</p>
                </div>
            </aside>

            <footer>
                <p>&copy; 2026 Allt om Mat / Expressen Bloggar. Alla rättigheter förbehållna.</p>
            </footer>
        </body>
        </html>
        """;

    public const string PlainText = """
        Min godaste köttfärssås någonsin

        Det här receptet på köttfärssås har jag gjort i så många år nu och det slår aldrig fel.
        Mina barn fullkomligt älskar den och brukar alltid ta två portioner varje gång.

        Ninas Köttfärssås, 4 p

        600 g nötfärs
        1 lök
        2 klyftor vitlök
        1 msk smör till stekning
        1 burk krossade tomater
        2 msk tomatpuré
        1 msk kalvfond
        1 tsk torkad oregano
        1 tsk torkad basilika
        1 dl vispgrädde
        1 tsk kinesisk soja
        salt och svartpeppar
        1 nypa socker

        Gör såhär:

        1) Börja med att steka köttfärsen i smör i en rymlig stekpanna tills den fått fin färg.
        2) Hacka löken och finhacka vitlöken, låt steka med köttfärsen de sista minuterna.
        3) Häll i krossade tomater, tomatpuré, kalvfond, oregano, basilika och soja. Låt puttra under lock i minst 20 minuter.
        4) Häll i grädde och smaka av med salt, peppar och en nypa socker. Låt sjuda ytterligare 5 minuter.

        Tips: Riv över parmesan innan servering.

        Lämna gärna en kommentar om vad ni tyckte!
        """;
}

