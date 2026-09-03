using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>
/// Built-in, deterministic transaction classifier for Germany. It is intentionally conservative:
/// user rules are evaluated before this catalog, merchant/payment intermediaries such as PayPal and
/// Klarna are not treated as merchants, and ambiguous text is left uncategorized rather than guessed.
/// Category keys are semantic/stable, so users can rename or move the default categories without
/// breaking classification. If a detailed category is archived, callers may fall back to its active
/// parent category.
/// </summary>
public static class GermanyCategorizationCatalog
{
    public readonly record struct Match(string CategoryKey, string Reason);

    private sealed record Entry(string CategoryKey, string Direction, string[] Aliases);
    private sealed record TextEntry(string CategoryKey, string Direction, string[] Patterns);

    // More specific brands must come before broader brands (for example ARAL PULSE before ARAL,
    // AMAZON PRIME before AMAZON). Aliases are matched as normalized token phrases against the
    // counterparty only; descriptions are handled separately below.
    private static readonly Entry[] MerchantEntries =
    [
        // Subscriptions / digital services before broad platform merchants.
        new("subscriptions.streaming", "expense", ["NETFLIX", "SPOTIFY", "DISNEY PLUS", "DISNEYPLUS", "WOW TV", "SKY DEUTSCHLAND", "DAZN", "RTL PLUS", "RTL+", "PARAMOUNT PLUS", "APPLE TV PLUS", "YOUTUBE PREMIUM", "AMAZON PRIME"]),
        new("subscriptions.software", "expense", ["ADOBE", "MICROSOFT 365", "DROPBOX", "GITHUB", "OPENAI", "CHATGPT", "JETBRAINS", "CANVA", "NOTION", "1PASSWORD", "BITWARDEN", "NORDVPN", "SURFSHARK", "PROTON", "ICLOUD", "APPLE COM BILL"]),

        // EV charging before fuel brands that also operate chargers.
        new("vehicle.charging", "expense", ["ENBW MOBILITY", "IONITY", "TESLA SUPERCHARGER", "EWE GO", "ARAL PULSE", "SHELL RECHARGE", "FASTNED", "ALLEGO", "MER GERMANY", "MAINGAU AUTOSTROM", "CHARGEPOINT", "ELECTRA", "CITYWATT", "PARKSTROM", "SMATRICS", "ENEL X WAY"]),

        // Grocery / supermarkets.
        new("food.groceries", "expense", ["ALDI", "LIDL", "REWE", "EDEKA", "NETTO MARKEN DISCOUNT", "NETTO", "PENNY", "KAUFLAND", "NORMA", "GLOBUS MARKTHALLE", "MARKTKAUF", "TEGUT", "FAMILA", "COMBI", "DENNS BIOMARKT", "ALNATURA", "BIO COMPANY", "V MARKT", "FENEBERG", "HIT HANDELSGRUPPE", "METRO CASH CARRY", "SELgROS", "PICNIC", "FLINK", "GORILLAS", "GETIR"]),
        new("food.bakery", "expense", ["BACKWERK", "KAMPS", "DITSCH", "YORMAS", "JUNGE DIE BAECKEREI", "JUNGE DIE BÄCKEREI", "SCHAEFERS", "SCHÄFERS", "DER BECK", "BROT HAUS", "SEHNE", "ZEIT FUER BROT", "ZEIT FÜR BROT"]),
        new("food.delivery", "expense", ["LIEFERANDO", "WOLT", "UBER EATS", "DOMINOS", "DOMINO S", "PIZZA HUT"]),
        new("food.restaurants", "expense", ["MCDONALDS", "MC DONALDS", "BURGER KING", "KFC", "KENTUCKY FRIED CHICKEN", "SUBWAY", "NORDSEE", "VAPIANO", "DEAN DAVID", "L OSTERIA", "STARBUCKS", "COFFEE FELLOWS", "HANS IM GLUECK", "HANS IM GLÜCK", "FIVE GUYS", "BLOCK HOUSE", "MARCHÉ", "MARCHE", "BACK FACTORY"]),

        // Drugstores / beauty.
        new("shopping.drugstore", "expense", ["DM DROGERIE", "ROSSMANN", "MUELLER DROGERIE", "MÜLLER DROGERIE", "BUDNI", "BUDNIKOWSKY"]),
        new("shopping.beauty", "expense", ["DOUGLAS", "SEPHORA", "FLACONI", "NOTINO", "PARFUEMERIE PIEPER", "PARFÜMERIE PIEPER"]),

        // Fuel and vehicle.
        new("vehicle.fuel", "expense", ["ARAL", "SHELL", "ESSO", "TOTALENERGIES", "TOTAL ENERGIES", "JET TANKSTELLE", "HEM TANKSTELLE", "AVIA TANKSTELLE", "ORLEN", "STAR TANKSTELLE", "AGIP", "ENI TANKSTELLE", "OIL TANKSTELLE", "Q1 TANKSTELLE", "HOYER TANKSTELLE"]),
        new("vehicle.maintenance", "expense", ["ATU", "A T U", "VERGOELST", "VERGÖLST", "EUROMASTER", "PITSTOP", "PIT STOP", "CARGLASS", "BOSCH CAR SERVICE", "TUEV", "TÜV", "DEKRA", "AUTOGLAS", "KFZ WERKSTATT"]),
        new("vehicle.carwash", "expense", ["MR WASH", "IMO CAR WASH", "BEST CARWASH", "CLEAN CAR"]),
        new("vehicle.parking", "expense", ["EASYPARK", "APCOA", "CONTIPARK", "PARKSTER", "Q PARK", "DB BAHNPARK", "PARK NOW", "PARKNOW"]),

        // Public transport / mobility.
        new("transport.public", "expense", ["DEUTSCHE BAHN", "DB VERTRIEB", "DB FERNVERKEHR", "DB REGIO", "FLIXTRAIN", "FLIXBUS", "BVG", "VBB", "MUNCHNER VERKEHRSGESELLSCHAFT", "MÜNCHNER VERKEHRSGESELLSCHAFT", "MVG", "MVV", "VVS", "SSB STUTTGART", "HVV", "HOCHBAHN", "RMV", "VRS", "KVB KOELN", "KVB KÖLN", "VRR", "RHEINBAHN", "DSW21", "VAG NUERNBERG", "VAG NÜRNBERG", "BSAG", "DVB DRESDEN", "LVB LEIPZIG", "MOBIEL", "METRONOM", "TRANSDEV", "ABELLIO", "NATIONAL EXPRESS"]),
        new("transport.taxi", "expense", ["UBER", "BOLT", "FREE NOW", "FREENOW", "TAXI DEUTSCHLAND"]),

        // Telecom / internet.
        new("housing.internet", "expense", ["DEUTSCHE TELEKOM", "TELEKOM DEUTSCHLAND", "VODAFONE", "TELEFONICA GERMANY", "O2 ONLINE", "1 1 TELECOM", "1UND1", "CONGSTAR", "FREENET", "DRILLISCH", "SIM DE", "WINSIM", "BLAU MOBILFUNK", "ALDI TALK", "LIDL CONNECT", "PYUR", "M NET", "NETCOLOGNE"]),

        // Energy / utilities.
        new("housing.electricity", "expense", ["E ON ENERGIE", "EON ENERGIE", "ENBW ENERGIE", "VATTENFALL", "YELLO STROM", "OCTOPUS ENERGY", "TIBBER", "LICHTBLICK", "NATURSTROM", "GRUENWELT ENERGIE", "GRÜNWELT ENERGIE", "EWE VERTRIEB", "ENTEGA", "SWM VERSORGUNG"]),

        // Insurance providers. Provider-level matching intentionally targets the parent category because
        // one company can sell several insurance products.
        new("insurance", "expense", ["ALLIANZ", "HUK COBURG", "HUK24", "AXA VERSICHERUNG", "R V VERSICHERUNG", "DEVK", "ERGO VERSICHERUNG", "GENERALI", "ZURICH VERSICHERUNG", "HDI VERSICHERUNG", "LVM VERSICHERUNG", "GOTHAER", "DEBEKA", "SIGNAL IDUNA", "WUERTTEMBERGISCHE", "WÜRTTEMBERGISCHE", "PROVINZIAL", "COSMOSDIREKT", "HANSEMERKUR", "BARMENIA", "VHV", "ARAG", "NUERNBERGER VERSICHERUNG", "NÜRNBERGER VERSICHERUNG"]),
        new("insurance.health", "expense", ["TECHNIKER KRANKENKASSE", "AOK", "BARMER", "DAK GESUNDHEIT", "IKK", "BKK", "KKH", "HEK", "HKK KRANKENKASSE"]),

        // Pharmacies / health / optical.
        new("health.pharmacy", "expense", ["SHOP APOTHEKE", "DOCMORRIS", "REDCARE PHARMACY", "EASYAPOTHEKE", "SANICARE", "MEDPEX", "APONEO"]),
        new("health.dental", "expense", ["ALLDENT", "ZAHNARZT", "ZAHNKLINIK"]),
        new("health.optical", "expense", ["FIELMANN", "APOLLO OPTIK", "MISTER SPEX", "EYES MORE", "EYES AND MORE", "PRO OPTIK", "ROTTLER"]),

        // Electronics / online / department stores.
        new("shopping.electronics", "expense", ["MEDIAMARKT", "MEDIA MARKT", "SATURN", "CYBERPORT", "ALTERNATE", "NOTEBOOKSBILLIGER", "CONRAD ELECTRONIC", "EXPERT", "EURONICS", "ELECTRONICPARTNER", "EP ELECTRONICPARTNER", "APPLE STORE", "GOOGLE STORE"]),
        new("shopping.clothing", "expense", ["ZALANDO", "ABOUT YOU", "H M", "H&M", "C A", "C&A", "ZARA", "UNIQLO", "PEEK CLOPPENBURG", "BREUNINGER", "S OLIVER", "TOM TAILOR", "NEW YORKER", "PRIMARK", "SHEIN", "BONPRIX", "ESPRIT", "MANGO", "JACK WOLFSKIN"]),
        new("shopping.furniture", "expense", ["IKEA", "JYSK", "POCO", "XXXLUTZ", "HOEFFNER", "HÖFFNER", "ROLLER", "MOEMAX", "MÖMAX", "SEGMUELLER", "SEGMÜLLER", "DEPOT", "BUTLERS"]),
        new("shopping.hardware", "expense", ["OBI", "BAUHAUS", "HORNBACH", "TOOM BAUMARKT", "HAGEBAU", "GLOBUS BAUMARKT", "BAYWA BAUMARKT", "HELLWEG", "BAUEN LEBEN"]),
        new("shopping.books", "expense", ["THALIA", "HUGENDUBEL", "WELTBILD", "OSIANDER", "MAYERSCHE", "LEHMANNs", "LEHMANN MEDIEN"]),
        new("shopping", "expense", ["AMAZON", "EBAY", "ETSY", "OTTO", "TEMU", "ALIEXPRESS", "WISH", "GALERIA", "KAUFHOF", "KARSTADT", "TCHIBO", "ACTION", "WOOLWORTH", "TEDI", "NKD"]),

        // Leisure / sport / gaming / events.
        new("leisure.sports", "expense", ["MCFIT", "FITX", "CLEVER FIT", "JOHN REED", "FITNESS FIRST", "URBAN SPORTS CLUB", "DECATHLON", "INTERSPORT", "SPORTSCHECK"]),
        new("leisure.gaming", "expense", ["STEAM", "VALVE", "PLAYSTATION NETWORK", "SONY INTERACTIVE", "XBOX", "NINTENDO", "EPIC GAMES", "BLIZZARD", "BATTLE NET", "RIOT GAMES", "ELECTRONIC ARTS", "EA GAMES", "UBISOFT"]),
        new("leisure.events", "expense", ["EVENTIM", "TICKETMASTER", "RESERVIX", "MYTICKET", "CINEMAXX", "CINESTAR", "UCI KINOWELT", "KINOPOLIS", "ASTOR FILMLOUNGE"]),

        // Travel.
        new("travel.flights", "expense", ["LUFTHANSA", "EUROWINGS", "CONDOR", "RYANAIR", "EASYJET", "WIZZ AIR", "TUIFLY", "EMIRATES", "QATAR AIRWAYS", "TURKISH AIRLINES", "KLM", "AIR FRANCE", "SWISS INTERNATIONAL", "AUSTRIAN AIRLINES"]),
        new("travel.accommodation", "expense", ["BOOKING COM", "AIRBNB", "HRS HOTEL", "MOTEL ONE", "B B HOTELS", "IBIS", "ACCOR", "MARRIOTT", "HILTON", "NH HOTELS", "LEONARDO HOTELS", "MEININGER", "PREMIER INN"]),
        new("travel.packages", "expense", ["TUI DEUTSCHLAND", "DERTOUR", "DER TOURISTIK", "AIDA CRUISES", "MEIN SCHIFF", "MSC CRUISES", "CHECK24 REISEN"]),
        new("travel", "expense", ["EXPEDIA", "HOLIDAYCHECK", "GETYOURGUIDE", "OMIO"]),

        // Education.
        new("education", "expense", ["UDEMY", "COURSERA", "BABBEL", "DUOLINGO", "STUDYSMARTER", "SOFATUTOR", "SIMPLECLUB"]),

        // Pets.
        new("pets.food", "expense", ["FRESSNAPF", "ZOOPLUS", "ZOOROYAL", "DAS FUTTERHAUS", "KOELLE ZOO", "KÖLLE ZOO"]),
        new("pets.vet", "expense", ["TIERARZT", "TIERKLINIK", "ANICURA", "EVIDENSIA"]),

        // Investing. Only explicit broker/investment counterparties; normal banks are deliberately not
        // included because transfers to a bank account are not necessarily investments.
        new("savings", "expense", ["TRADE REPUBLIC", "SCALABLE CAPITAL", "FLATEXDEGIRO", "SMARTBROKER", "BITPANDA", "COINBASE", "KRAKEN"]),
    ];

    // Description-only signals. These are intentionally narrow and direction-aware.
    private static readonly TextEntry[] TextEntries =
    [
        new("income.salary", "income", ["GEHALT", "LOHN GEHALT", "LOHNZAHLUNG", "BEZUEGE", "BEZÜGE", "ENTGELTABRECHNUNG", "SALARY"]),
        new("income.benefits", "income", ["FAMILIENKASSE", "KINDERGELD", "ELTERNGELD", "ARBEITSLOSENGELD", "BUNDESAGENTUR FUER ARBEIT", "BUNDESAGENTUR FÜR ARBEIT", "BAFOEG", "BAFÖG"]),
        new("income.interest", "income", ["ZINSGUTSCHRIFT", "HABENZINS", "ZINSERTRAG"]),
        new("income.refunds", "income", ["RUECKERSTATTUNG", "RÜCKERSTATTUNG", "ERSTATTUNG", "REFUND", "CASHBACK", "GUTSCHRIFT RETOURE"]),

        new("housing.rent", "expense", ["MIETZAHLUNG", "WOHNUNGSMIETE", "KALTMIETE", "WARMMIETE", "HAUSVERWALTUNG"]),
        new("housing.mortgage", "expense", ["BAUFINANZIERUNG", "IMMOBILIENDARLEHEN", "DARLEHENSRATE IMMOBILIE", "BAUSPARDARLEHEN"]),
        new("housing.heating", "expense", ["GASABSCHLAG", "FERNWAERME", "FERNWÄRME", "HEIZKOSTEN"]),
        new("housing.water", "expense", ["WASSERGELD", "WASSER ABWASSER", "ABWASSERGEBUEHR", "ABWASSERGEBÜHR"]),
        new("housing.utilities", "expense", ["NEBENKOSTEN", "BETRIEBSKOSTEN", "STADTWERKE"]),

        new("family.childcare", "expense", ["KINDERGARTEN", "KITA GEBUEHR", "KITA GEBÜHR", "KINDERBETREUUNG"]),

        new("cash", "expense", ["BARGELDAUSZAHLUNG", "GELDAUTOMAT", "ATM AUSZAHLUNG", "CASH WITHDRAWAL"]),
        new("fees", "expense", ["KONTOFUEHRUNG", "KONTOFÜHRUNG", "KONTOFUEHRUNGSGEBUEHR", "KONTOFÜHRUNGSGEBÜHR", "KARTENENTGELT", "FREMDWAEHRUNGSENTGELT", "FREMDWÄHRUNGSENTGELT", "AUSLANDSEINSATZENTGELT", "ENTGELTABSCHLUSS"]),
        new("taxes", "expense", ["FINANZAMT", "STEUERVORAUSZAHLUNG", "EINKOMMENSTEUER", "KFZ STEUER", "KRAFTFAHRZEUGSTEUER", "GRUNDSTEUER"]),
        new("donations", "expense", ["SPENDE", "SPENDENBEITRAG"]),
    ];

    public static int MerchantAliasCount => MerchantEntries.Sum(x => x.Aliases.Length);
    public static int TextPatternCount => TextEntries.Sum(x => x.Patterns.Length);

    /// <summary>Returns a semantic category-key match, or null when the transaction is ambiguous.</summary>
    public static Match? Classify(FinanceTransaction transaction)
    {
        var counterparty = MerchantNormalization.Normalize(transaction.NormalizedCounterparty ?? transaction.Counterparty);
        if (!string.IsNullOrWhiteSpace(counterparty))
        {
            foreach (var entry in MerchantEntries)
            {
                if (!DirectionMatches(transaction.Amount, entry.Direction)) continue;
                foreach (var alias in entry.Aliases)
                {
                    if (ContainsPhrase(counterparty, MerchantNormalization.Normalize(alias)))
                        return new Match(entry.CategoryKey, $"merchant:{alias}");
                }
            }
        }

        var description = MerchantNormalization.Normalize(transaction.Description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            foreach (var entry in TextEntries)
            {
                if (!DirectionMatches(transaction.Amount, entry.Direction)) continue;
                foreach (var pattern in entry.Patterns)
                {
                    if (ContainsPhrase(description, MerchantNormalization.Normalize(pattern)))
                        return new Match(entry.CategoryKey, $"text:{pattern}");
                }
            }
        }

        var mccKey = CategoryKeyForMcc(transaction.MerchantCategoryCode);
        return mccKey is null ? null : new Match(mccKey, $"mcc:{transaction.MerchantCategoryCode}");
    }

    private static bool DirectionMatches(decimal amount, string direction) => direction switch
    {
        "income" => amount > 0,
        "expense" => amount < 0,
        _ => true
    };

    private static bool ContainsPhrase(string? normalizedText, string? normalizedPhrase)
    {
        if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedPhrase)) return false;
        // Token-boundary matching avoids false positives such as JET inside another word or DM inside
        // an unrelated merchant name while still matching bank suffixes like "REWE MARKT 1234 BERLIN".
        var haystack = $" {normalizedText} ";
        var needle = $" {normalizedPhrase} ";
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CategoryKeyForMcc(string? merchantCategoryCode)
    {
        if (!int.TryParse(merchantCategoryCode?.Trim(), out var mcc)) return null;

        // Airline and hotel MCC ranges are allocated by individual carrier/hotel chains.
        if (mcc is >= 3000 and <= 3299) return "travel.flights";
        if (mcc is >= 3501 and <= 3999) return "travel.accommodation";

        return mcc switch
        {
            // Transport / travel.
            4111 or 4112 or 4131 => "transport.public",
            4121 => "transport.taxi",
            4511 => "travel.flights",
            4722 => "travel",
            4789 => "transport.public",
            7011 => "travel.accommodation",
            7512 => "travel",
            7523 => "vehicle.parking",

            // Vehicle.
            5511 or 5521 or 5532 or 5533 or 7531 or 7534 or 7535 or 7538 => "vehicle.maintenance",
            5541 or 5542 => "vehicle.fuel",
            5552 => "vehicle.charging",
            7542 => "vehicle.carwash",

            // Retail / food.
            5200 or 5211 or 5231 or 5251 => "shopping.hardware",
            5311 or 5331 or 5399 => "shopping",
            5411 or 5422 or 5441 or 5451 or 5462 or 5499 => "food.groceries",
            5651 or 5655 or 5661 or 5691 or 5699 => "shopping.clothing",
            5712 or 5713 or 5714 or 5719 => "shopping.furniture",
            5732 or 5734 => "shopping.electronics",
            5812 => "food.restaurants",
            5814 => "food.restaurants",
            5815 or 5816 or 5817 or 5818 => "subscriptions",
            5912 => "health.pharmacy",
            5942 => "shopping.books",
            5945 => "leisure.gaming",
            5977 => "shopping.beauty",
            5995 => "pets.food",

            // Health / insurance.
            6300 => "insurance",
            8011 or 8031 or 8041 or 8049 or 8099 => "health.doctor",
            8021 => "health.dental",
            8042 or 8043 => "health.optical",

            // Education / leisure.
            7832 => "leisure.events",
            7911 or 7922 or 7929 or 7933 or 7941 or 7991 or 7992 or 7994 or 7996 or 7997 or 7999 => "leisure",
            8211 or 8220 or 8241 or 8244 or 8249 or 8299 => "education",
            0742 => "pets.vet",
            8398 or 8661 => "donations",

            // Banking / government.
            6011 => "cash",
            9311 => "taxes",
            _ => null
        };
    }
}
