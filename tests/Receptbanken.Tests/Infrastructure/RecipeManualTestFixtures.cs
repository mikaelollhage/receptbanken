namespace Receptbanken.Tests.Infrastructure;

/// <summary>
/// Representative offline test fixtures modelled on the eight manual test cases
/// observed in the user interface (Del G).
/// </summary>
public static class RecipeManualTestFixtures
{
    /// <summary>
    /// Test 1: Bloggbrus runt ett recept (Ugnsbakad lax med citron)
    /// </summary>
    public const string Test1_BlogNoise = """
        Välkommen till min blogg! Idag vill jag dela med mig av en fantastisk middag.
        Jag älskar lax, och det här receptet har vi lagat många fredagskvällar här hemma.
        Mina barn brukar alltid be om påbackning när jag serverar denna lax.

        Kolla även in mina andra goda fiskrecept:
        - Torskrygg med vitvinssås
        - Laxsoppa med saffran

        Annons: Handla fisk online med snabb hemleverans hos HavsGott!

        Ugnsbakad lax med citron, 4 portioner

        Ingredienser:
        600 g laxfilé
        1 st citron
        2 dl crème fraiche
        1 msk dijonsenap
        1 knippe dill
        1 tsk salt

        Gör så här:
        1. Sätt ugnen på 200 grader.
        2. Lägg laxfilén i en smord ugnsfast form och salta.
        3. Blanda crème fraiche, dijonsenap, finhackad dill och rivet citronskal.
        4. Bred såsen över laxen och baka i ugnen i cirka 20 minuter.

        Tips: Servera med kokt potatis och sockerärtor.

        Tack för att ni läser min blogg! Kram på er alla!
        Dela på Facebook | Spara recept
        """;

    /// <summary>
    /// Test 2: Flera ingrediensgrupper (Kycklingtacos med mangosalsa)
    /// </summary>
    public const string Test2_MultipleGroups = """
        ## Kycklingtacos med mangosalsa, 4 portioner

        Kyckling:
        500 g kycklingfilé
        1 msk tacokrydda
        1 msk olja

        Mangosalsa:
        1 mango
        1 rödlök
        1 kruka koriander
        1 lime
        1 tsk sambal oelek

        Till servering:
        8 små tortillabröd
        2 dl crème fraiche
        1 avokado
        Salladsblad

        Gör så här:
        1. Strimla kycklingen och bryn i olja tillsammans med tacokryddan.
        2. Tärna mangon, finhacka rödlök och koriander. Blanda i en skål, pressa över lime och tillsätt sambal oelek.
        3. Värm tortillabröden i en torr stekpanna.
        4. Fyll bröden med salladsblad, stekt kyckling, mangosalsa, skivad avokado och en klick crème fraiche.
        """;

    /// <summary>
    /// Test 3: Ingredienser utan mängd (Enkel tomatsallad)
    /// </summary>
    public const string Test3_IngredientsWithoutQuantity = """
        ## Enkel tomatsallad, 2 portioner

        Ingredienser för 2 personer:
        3 stora tomater
        1 rödlök
        Färsk basilika
        Olivolja
        Flingsalt
        Nymalen svartpeppar
        En skvätt balsamvinäger

        Gör så här:
        1. Skiva tomaterna tunt och lägg upp på ett fint serveringsfat.
        2. Skala och skiva rödlöken i tunna ringar.
        3. Plocka bladen från basilikan och fördela över tomaterna.
        4. Ringla över olivolja och en skvätt balsamvinäger.
        5. Avsluta med att toppa med flingsalt och nymalen svartpeppar.
        """;

    /// <summary>
    /// Test 4: Onumrerade instruktioner (Krämig svamppasta)
    /// </summary>
    public const string Test4_UnnumberedInstructions = """
        ## Krämig svamppasta, 3 port

        Du behöver:
        300 g pasta
        250 g champinjoner
        2 dl matlagningsgrädde
        1 gul lök
        2 klyftor vitlök
        1 msk smör
        Parmesan till servering

        Gör så här:
        Koka pastan i rikligt saltat vatten enligt anvisning på förpackningen.
        Skiva champinjonerna och hacka löken och vitlöken fint.
        Fräs svampen och löken i smör tills vätskan kokat in och svampen fått fin färg.
        Häll på matlagningsgrädden och låt såsen sjuda i några minuter.
        Blanda såsen med nykokt pasta och toppa med rikligt med parmesan till servering.
        """;

    /// <summary>
    /// Test 5: Mått och kommentarer (Mustig vegetarisk gryta)
    /// </summary>
    public const string Test5_MeasurementsAndRanges = """
        ## Mustig vegetarisk gryta, 6 portioner

        Ingredienser:
        2 burkar krossade tomater (à 400 g)
        1 förp kikärtor (à 380 g)
        1 gul lök
        2 klyftor vitlök
        ca 1–2 msk olivolja
        1–2 tsk spiskummin
        ½–1 tsk chiliflakes
        1 tsk paprikapulver
        1 1/2 dl grädde
        Salt och nymalen peppar

        Gör så här:
        1. Hacka lök och vitlök fint.
        2. Fräs löken i olivolja tillsammans med kryddorna tills det doftar gott.
        3. Häll i krossade tomater och sköljda kikärtor.
        4. Låt sjuda under lock på svag värme i 20 minuter.
        5. Rör i grädden och smaka av med salt och nymalen peppar.
        """;

    /// <summary>
    /// Test 6: Två fullständiga recept i samma inklistrade text
    /// </summary>
    public const string Test6_TwoRecipesInOneText = """
        Två goda frukostar för helgen!

        ## Amerikanska pannkakor, 4 portioner

        Ingredienser:
        2 ägg
        3 dl vetemjöl
        2 dl mjölk
        2 tsk bakpulver
        1 msk smör

        Gör så här:
        1. Vispa ihop ägg, mjöl och mjölk.
        2. Blanda i bakpulvret.
        3. Stek små pannkakor i smör.

        ## Bananpannkakor, 2 portioner

        Ingredienser:
        2 bananer
        3 ägg
        1 dl havregryn
        1 tsk kanel

        Gör så här:
        1. Mosa bananerna.
        2. Blanda med ägg, havregryn och kanel.
        3. Stek små pannkakor på medelvärme.

        Fler recept hittar du på min blogg!
        """;

    /// <summary>
    /// Test 7: Reklam och rabattkod mitt i ett instruktionssteg
    /// </summary>
    public const string Test7_AdvertInsideInstruction = """
        ## Saftig kyckling i ugn, 4 portioner

        Ingredienser:
        800 g kycklinglårfilé
        2 msk smör
        1 msk soja
        1 tsk rosmarin
        Salt
        Svartpeppar

        Gör så här:
        1. Sätt ugnen på 200 grader.
        2. Bryn kycklingen i smör i stekpannan tills den har fin färg, glöm inte att använda min rabattkod KOCK20 hos KitchenWare för 20% rabatt på stekpannor, och lägg sedan över kycklingen i en ugnsfast form.
        3. Pensla kycklingen med soja och krydda med rosmarin, salt och peppar.
        4. Tillaga i ugnen i cirka 25 minuter tills innertemperaturen är 72 grader.
        """;

    /// <summary>
    /// Test 8: Text utan recept (allmänna råd och relaterade länkar)
    /// </summary>
    public const string Test8_NonRecipeText = """
        Välkommen till min matblogg!

        Här är mina fem bästa tips för en lyckad middagsbjudning.

        1. Planera inköpen i god tid.
        2. Duka bordet kvällen innan.
        3. Välj gärna säsongens råvaror.
        4. Förbered efterrätten dagen före.
        5. Glöm inte att njuta av kvällen!

        Mina favoritingredienser är tomater, basilika, parmesan och olivolja.

        Läs också:
        Krämig pasta
        Enkel lasagne
        Godaste chokladkakan

        Prenumerera på mitt nyhetsbrev för fler tips!
        """;

    /// <summary>
    /// Test 9: Bloggbrus och mängdlösa ingredienser (Citronkyckling med örter)
    /// </summary>
    public const string Test9_CitronkycklingMedOrter = """
        Välkommen tillbaka till köket! Det här receptet har blivit en riktig familjefavorit.
        Missa inte mina andra recept:
        - Krämig kycklinggryta
        - Snabb pasta pesto

        Annons: Köp kryddor med 15% rabatt hos Kryddriket!

        Citronkyckling med örter, 4 portioner

        Ingredienser:
        800 g kycklingfilé
        1 citron
        2 msk olivolja
        2 klyftor vitlök
        1 msk färsk timjan
        1 msk färsk rosmarin
        Flingsalt
        Svartpeppar

        Gör så här:
        1. Sätt ugnen på 200 grader.
        2. Lägg kycklingfiléerna i en ugnsfast form.
        3. Riv skalet av citronen och pressa över saften, blanda med olivolja, pressad vitlök och örterna.
        4. Häll marinaden över kycklingen, toppa med flingsalt och peppar, och tillaga i ugnen i 20 minuter.

        Tips: Servera med ugnsrostad potatis och en krispig grönsallad.

        Hoppas ni gillar receptet! Kram!
        """;

    /// <summary>
    /// Test 10: Ingrediensgrupper och onumrerade steg (Tacobowl med avokadodressing)
    /// </summary>
    public const string Test10_TacobowlMedAvokadodressing = """
        ## Tacobowl med avokadodressing, 2 portioner

        Bowl:
        200 g blandfärs
        1 msk tacokrydda
        2 dl kokt ris
        1 dl majs
        1 dl svarta bönor
        1 tomat

        Dressing:
        1 avokado
        1 dl gräddfil
        1 lime
        Salt

        Gör så här:
        Stek färsen med tacokrydda i lite olja tills den är genomstekt.
        Mosa avokadon och blanda med gräddfil, limesaft och lite salt till en slät dressing.
        Fördela ris, bönor, majs, hackad tomat och stekt färs i två djupa skålar.
        Ringla avokadodressingen över skålarna och servera direkt.
        """;

    /// <summary>
    /// Test 11: Måttintervall och kommentarer (Kryddig linsgryta)
    /// </summary>
    public const string Test11_KryddigLinsgryta = """
        ## Kryddig linsgryta, 4 portioner

        Ingredienser:
        2 dl röda linser
        1 förp kikärtor (à 380 g, avrunna)
        1 burk krossade tomater
        1 gul lök
        2 klyftor vitlök
        ca 1–2 msk olivolja
        1–2 tsk spiskummin
        ½–1 tsk chiliflakes

        Gör så här:
        1. Hacka lök och vitlök fint och fräs i olivolja med spiskummin och chiliflakes.
        2. Tillsätt sköljda linser, krossade tomater och kikärtor.
        3. Låt puttra på medelvärme i cirka 15 minuter tills linserna är mjuka.
        """;

    /// <summary>
    /// Test 12: Två separata recept (Chokladmuffins – 12 stycken & Saftigt bananbröd – 1 limpa)
    /// </summary>
    public const string Test12_TwoRecipesWithStycken = """
        Veckans bästa bakverk!

        ## Chokladmuffins – 12 stycken

        Ingredienser:
        100 g smör
        2 ägg
        1 1/2 dl socker
        2 1/2 dl vetemjöl
        3 msk kakao

        Gör så här:
        1. Smält smöret och låt det svalna något.
        2. Vispa ägg och socker pösigt och vänd ner mjöl, kakao och det smälta smöret.
        3. Fördela smeten i muffinsformar och grädda i 200 grader i cirka 12 minuter.

        ## Saftigt bananbröd – 1 limpa

        Ingredienser:
        3 mogna bananer
        50 g smör
        1 dl socker
        1 ägg
        3 dl vetemjöl
        1 tsk bikarbonat

        Gör så här:
        1. Mosa bananerna och blanda med smält smör, socker och ägg.
        2. Rör ner vetemjöl och bikarbonat.
        3. Grädda i en brödform i 175 grader i 50 minuter.
        """;

    /// <summary>
    /// Test 13: Text utan recept (Fem tips för ett bättre kök)
    /// </summary>
    public const string Test13_FemTipsForEttBattreKok = """
        Fem tips för ett bättre kök

        Här kommer mina bästa tips för att få ett mer funktionellt kök hemma.

        1. Rensa i skåpen
        Gå igenom dina torrvaror regelbundet så du vet vad du har hemma.

        2. Förvara rätt
        Lök och potatis trivs bäst mörkt och svalt, inte i plastpåsar.

        3. Håll knivarna vassa
        En vass kniv är både säkrare och roligare att använda vid matlagning.

        4. Använd glasburkar
        Torrvaror som pasta och ris håller längre och syns bättre i glasburkar.

        5. Ha en ren arbetsyta
        Torka alltid av bänkarna innan du sätter igång.
        """;

    /// <summary>
    /// Test 14: Minimalt recept utan sektionsrubriker (Snabb tomatpasta)
    /// </summary>
    public const string Test14_SnabbTomatpasta = """
        Snabb tomatpasta

        200 g spaghetti
        2 tomater
        1 msk olivolja
        Färsk basilika

        Koka pastan i saltat vatten.
        Hacka tomaterna och fräs dem i oljan.
        Blanda med pastan och toppa med basilika.
        """;

    /// <summary>
    /// Del A Regression test: Enkel vitlöksdipp
    /// </summary>
    public const string Test14_Regression_EnkelVitloksdipp = """
        Enkel vitlöksdipp

        2 dl crème fraiche
        1 vitlöksklyfta
        Salt
        Svartpeppar

        Pressa vitlöken.
        Blanda med crème fraiche.
        Smaka av med salt och peppar.
        """;

    /// <summary>
    /// Del A Negative test 1: Bloggtext med rubrik, matord och allmänna råd
    /// </summary>
    public const string NegativeTest1_BlogAdviceWithFoodWords = """
        Mina tankar kring god mat och råvaror

        När man lagar mat är det viktigt med bra råvaror.
        Jag älskar tomater, färsk basilika, vitlök och en god olivolja.
        Det gör all skillnad i världen.
        Ta dig tid i köket och njut av dofterna när du lagar middag.
        Bjud in vänner och familj att dela måltiden med dig.
        """;

    /// <summary>
    /// Del A Negative test 2: Numrerad lista med hushållstips och matrelaterade ord
    /// </summary>
    public const string NegativeTest2_HouseholdTipsNumberedList = """
        Tre smarta tips för matrester

        1. Frys in överbliven grädde och mjölk i iskuber för framtida såser.
        2. Torka brödkanter i ugnen och mixa till eget ströbröd.
        3. Koka grönsaksbuljong på skal och blast från morötter och lök.
        """;

    /// <summary>
    /// Test 20: Ugnsrostade morötter (Original med "Sätt ugnen" först)
    /// </summary>
    public const string Test20_UgnsrostadeMorotter = """
        Ugnsrostade morötter, 4 portioner

        500 g morötter
        1–2 msk olivolja
        ½ tsk flingsalt
        1 tsk honung
        Färsk timjan
        Svartpeppar

        Sätt ugnen på 225 grader.
        Skala morötterna och dela dem på längden.
        Blanda morötterna med olja, honung och kryddor.
        Rosta i ugnen i 20 minuter.
        """;

    /// <summary>
    /// Test 20B: Ugnsrostade morötter (Samma recept, "Skala morötterna" först)
    /// </summary>
    public const string Test20B_UgnsrostadeMorotter_Reordered = """
        Ugnsrostade morötter, 4 portioner

        500 g morötter
        1–2 msk olivolja
        ½ tsk flingsalt
        1 tsk honung
        Färsk timjan
        Svartpeppar

        Skala morötterna och dela dem på längden.
        Sätt ugnen på 225 grader.
        Blanda morötterna med olja, honung och kryddor.
        Rosta i ugnen i 20 minuter.
        """;

    /// <summary>
    /// Test 21: Ingrediensrubrik utan instruktionsrubrik (Enkel tomatsoppa)
    /// </summary>
    public const string Test21_EnkelTomatsoppa = """
        Enkel tomatsoppa

        Ingredienser:
        400 g krossade tomater
        2 dl vatten
        Salt

        Koka upp tomater och vatten.
        Låt sjuda i 10 minuter.
        Smaka av med salt.
        """;

    /// <summary>
    /// Test 22: Beskrivande instruktioner (Enkel potatisgratäng)
    /// </summary>
    public const string Test22_EnkelPotatisgratang = """
        Enkel potatisgratäng

        800 g potatis
        3 dl grädde
        2 vitlöksklyftor
        Salt
        Svartpeppar

        Potatisen ska skivas tunt.
        Grädden blandas med pressad vitlök.
        Allt läggs i en ugnsform.
        Gratängen gräddas i 200 grader i 45 minuter.
        """;

    /// <summary>
    /// Test 23: Instruktion som börjar med en siffra (Krämig svamppasta)
    /// </summary>
    public const string Test23_KramigSvamppasta = """
        Krämig svamppasta, 2 portioner

        Ingredienser:
        200 g pasta
        150 g champinjoner
        1 dl grädde

        Gör så här:
        Koka pastan enligt anvisningen.
        2 minuter senare tillsätter du grädden.
        Blanda allt och servera.
        """;

    /// <summary>
    /// Test 24: Icke-recept med portionsantal (Middagstips för 4 portioner)
    /// </summary>
    public const string Test24_MiddagstipsFor4Portioner = """
        Middagstips för 4 portioner

        Ingredienser:
        Planera dina måltider
        Handla gärna på söndagar
        Förvara maten i kylen

        Kom ihåg att välja varierade råvaror.
        """;

    /// <summary>
    /// Test 25: Instruktionsrubrik utan ingrediensrubrik (Snabb yoghurtsås)
    /// </summary>
    public const string Test25_SnabbYoghurtsas = """
        Snabb yoghurtsås

        2 dl turkisk yoghurt
        1 vitlöksklyfta
        Färsk dill
        Salt och peppar

        Gör så här:
        Pressa vitlöken.
        Blanda alla ingredienser.
        Smaka av med salt och peppar.
        """;

    /// <summary>
    /// Test 26: Instruktion som börjar med en mängd (Enkel risotto)
    /// </summary>
    public const string Test26_EnkelRisotto = """
        Enkel risotto, 2 portioner

        Ingredienser:
        2 dl risottoris
        5 dl grönsaksbuljong
        1 msk smör

        Gör så här:
        Smält smöret i en kastrull.
        1 dl vatten tillsätts om riset blir för torrt.
        Rör om tills riset är krämigt.
        """;
}

