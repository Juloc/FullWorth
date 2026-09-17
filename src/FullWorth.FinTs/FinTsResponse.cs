using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.FinTs;

internal sealed record FinTsResponseCode(string Code, string? Reference, string Text, IReadOnlyList<string> Parameters, int? SegmentNumber = null)
{
    public bool IsError => Code.Length == 4 && Code[0] == '9';
    public bool TanRequired => Code is "0030" or "3955";
    public bool DecoupledPending => Code is "3955" or "3956";
    public bool ScaExempt => Code == "3076";
    public bool Touchdown => Code == "3040";
}

internal sealed class FinTsResponse
{
    public required IReadOnlyList<FinTsSegment> Segments { get; init; }
    public required IReadOnlyList<FinTsResponseCode> Codes { get; init; }

    public FinTsSegment? Find(string type) => Segments.FirstOrDefault(x => x.Type == type);
    public IEnumerable<FinTsSegment> FindAll(string type) => Segments.Where(x => x.Type == type);
    public bool NeedsTan => Codes.Any(x => x.TanRequired) && !Codes.Any(x => x.ScaExempt);
    public bool DecoupledPending => Codes.Any(x => x.DecoupledPending);
    public string? Touchdown => Codes.FirstOrDefault(x => x.Touchdown)?.Parameters.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// Wirft, wenn die Bank einen Fehler gemeldet hat - und zwar mit ALLEN Codes, die sie geschickt hat
    /// (#130 §9).
    ///
    /// Die erste Meldung ist bei ING regelmaessig 9800 "Der Dialog wurde abgebrochen". Das ist die
    /// Sammelmeldung: sie sagt, dass der Dialog endete, nicht warum. Der Grund steht in den Codes
    /// dahinter - falsche Zugangsdaten, gesperrter Zugang, nicht zugelassene Produkt-ID. Genau die
    /// wurden bisher verworfen, und im Protokoll stand eine Meldung, mit der niemand etwas anfangen
    /// konnte.
    ///
    /// Eingeordnet wird deshalb ueber alle Fehlercodes, und die gesprechendste Einordnung gewinnt:
    /// 9800 allein bleibt "bank_error", 9800 zusammen mit 9942 ist "pin_wrong".
    /// </summary>
    public void ThrowOnError(IReadOnlyList<FinTsSegmentShape>? sentShape = null)
    {
        var errors = Codes.Where(x => x.IsError).ToList();
        if (errors.Count == 0) return;

        var classified = errors.Select(x => (Error: x, Kind: Classify(x.Code)))
            .Where(x => x.Kind != "bank_error")
            .ToList();
        // Der Code, der die Einordnung traegt - sonst der erste. Die Meldung kommt von demselben.
        var leading = classified.Count > 0 ? classified[0].Error : errors[0];
        var kind = classified.Count > 0 ? classified[0].Kind : "bank_error";

        var message = string.IsNullOrWhiteSpace(leading.Text) ? $"FinTS bank error {leading.Code}." : leading.Text;
        // Denselben Vorrang wie bei der Einordnung: die Sammelmeldung steht vorn, aber der Bezug steht
        // bei dem Code, der wirklich auf ein Segment zeigt. "9050 Teilweise fehlerhaft" nennt keines -
        // das tut die Meldung dahinter.
        var segment = errors.Select(x => Reference(x, sentShape)).FirstOrDefault(x => x is not null);
        throw new FinTsException(message, kind,
            bankCode: leading.Code,
            segmentReference: segment,
            bankMessage: string.IsNullOrWhiteSpace(leading.Text) ? null : leading.Text,
            bankCodes: [.. errors.Select(x => new FinTsBankCode(x.Code, Reference(x, sentShape), x.Text))],
            sentShape: sentShape);
    }

    /// <summary>
    /// Worauf sich eine Rueckmeldung bezieht - aufgeloest zu dem Segment, das wir geschickt haben.
    ///
    /// "9050 Teilweise fehlerhaft" heisst laut Spezifikation: in der Nachricht ist mindestens ein
    /// fehlerhafter AUFTRAG enthalten. Welcher, sagt das Bezugssegment im Kopf von HIRMS - eine blosse
    /// Nummer. Die Nummer gegen den Bauplan der gesendeten Nachricht zu halten ist der ganze Trick:
    /// aus "5" wird "5 HKTAN", und damit steht im Protokoll, welches Segment die Bank ablehnt, statt
    /// dass jemand es raten muss.
    /// </summary>
    private static string? Reference(FinTsResponseCode code, IReadOnlyList<FinTsSegmentShape>? sentShape)
    {
        if (code.SegmentNumber is not { } number)
            // "-" ist der Platzhalter der Banken fuer "kein Bezug" und war als Bezug im Protokoll
            // genauso wenig wert wie nichts - nur sah es aus wie eine Angabe.
            return string.IsNullOrWhiteSpace(code.Reference) || code.Reference == "-" ? null : code.Reference;
        var sent = sentShape?.FirstOrDefault(x => x.Number == number);
        return sent is null
            ? number.ToString(CultureInfo.InvariantCulture)
            : $"{number} {sent.Type}";
    }

    /// <summary>
    /// Die Einordnung - und nur fuer die Codes, deren Bedeutung belegt ist.
    ///
    /// Die uebrigen Faelle aus #130 §9 (Produkt-ID nicht zugelassen, Segmentversion nicht
    /// unterstuetzt, Dialog abgelaufen, Bank nicht erreichbar) fehlen hier mit Absicht: ich kenne die
    /// zugehoerigen DK-Codes nicht sicher, und eine falsche Einordnung ist schlimmer als keine - sie
    /// zeigt dem Benutzer eine Ursache, die nicht stimmt. Sichtbar sind sie trotzdem, weil seit #130 §9
    /// ALLE Codes mitgefuehrt und protokolliert werden. Wer eine echte Bankantwort mit einem dieser
    /// Codes hat, kann die Zeile hier belegt ergaenzen.
    /// </summary>
    private static string Classify(string code) => code switch
    {
        "9340" or "9942" => "pin_wrong",       // Zugangsdaten falsch
        "9930" or "9931" => "access_locked",   // Zugang gesperrt
        _ => "bank_error"
    };
}

internal static class FinTsResponseParser
{
    private static readonly Regex IbanRegex = new("^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BicRegex = new("^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsinRegex = new("^[A-Z]{2}[A-Z0-9]{9}[0-9]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FinTsResponse Parse(byte[] bytes)
    {
        var outer = FinTsWire.Parse(bytes).ToList();
        var expanded = new List<FinTsSegment>();
        foreach (var segment in outer)
        {
            expanded.Add(segment);
            if (segment.Type == "HNVSD" && segment.GetBinary(1, 0) is { Length: > 0 } inner)
                expanded.AddRange(FinTsWire.Parse(inner));
        }
        return new FinTsResponse { Segments = expanded, Codes = ParseCodes(expanded) };
    }

    public static string DialogId(FinTsResponse response)
        => response.Find("HNHBK")?.GetText(3, 0) is { Length: > 0 } value ? value : "0";

    public static FinTsBankParameters MergeParameters(FinTsBankParameters current, FinTsResponse response)
    {
        var bpd = current.BpdVersion;
        var upd = current.UpdVersion;
        var systemId = current.SystemId;
        var versions = new Dictionary<string, int>(current.SegmentVersions, StringComparer.OrdinalIgnoreCase);
        var tanRequired = new Dictionary<string, bool>(current.TanRequired, StringComparer.OrdinalIgnoreCase);
        var methods = current.TanMethods.ToList();
        var accounts = current.Accounts.ToList();
        var allowedSecurityFunctions = new List<string>();

        foreach (var segment in response.Segments)
        {
            switch (segment.Type)
            {
                case "HIBPA":
                    if (int.TryParse(segment.GetText(1, 0), out var b)) bpd = b;
                    break;
                case "HIUPA":
                    if (int.TryParse(segment.GetText(1, 0), out var u)) upd = u;
                    break;
                case "HISYN":
                    var sid = segment.GetText(1, 0);
                    if (!string.IsNullOrWhiteSpace(sid)) systemId = sid;
                    break;
                case "HIUPD":
                    var account = ParseAccount(segment);
                    if (account is not null && !accounts.Any(x => SameAccount(x, account))) accounts.Add(account);
                    break;
                case "HIPINS":
                    foreach (var pair in ParsePinTanRules(segment)) tanRequired[pair.Key] = pair.Value;
                    break;
                case "HITANS":
                    // Die Version wird MITGEFUEHRT, auch wenn kein Verfahren daraus lesbar ist: nach
                    // ihr entscheidet sich, ob HKTAN ueberhaupt in die Anmeldung gehoert.
                    if (segment.Version > 0)
                        versions["HITANS"] = Math.Max(versions.GetValueOrDefault("HITANS"), segment.Version);
                    // Eine Bank kuendigt dasselbe Verfahren in MEHREREN Segmentversionen an - ING
                    // schickt HITANS:4 und HITANS:6 nebeneinander. Vorher gewann das zuerst gesehene,
                    // und das ist regelmaessig das aelteste: fuer Sicherheitsfunktion 942 blieb
                    // Version 4 stehen, die Version 6 daneben wurde weggeworfen. Gebraucht wird die
                    // hoechste, denn nach ihr wird HKTAN gebaut (#130 §8).
                    foreach (var method in ParseTanMethods(segment))
                    {
                        var existing = methods.FindIndex(x => x.SecurityFunction == method.SecurityFunction);
                        if (existing < 0) methods.Add(method);
                        else if (methods[existing].SegmentVersion < method.SegmentVersion) methods[existing] = method;
                    }
                    break;
                default:
                    if (segment.Type.StartsWith("HI", StringComparison.Ordinal) && segment.Type.EndsWith("S", StringComparison.Ordinal) && segment.Version > 0)
                        versions[segment.Type] = Math.Max(versions.GetValueOrDefault(segment.Type), segment.Version);
                    break;
            }
        }

        foreach (var responseCode in response.Codes.Where(x => x.Code == "3920"))
            allowedSecurityFunctions.AddRange(responseCode.Parameters.Where(x => x.All(char.IsDigit)));

        var security = current.SecurityFunction;
        if (allowedSecurityFunctions.Count > 0)
        {
            var allowed = allowedSecurityFunctions.Distinct().ToArray();
            var decoupled = methods.FirstOrDefault(x => x.IsDecoupled && allowed.Contains(x.SecurityFunction));
            security = decoupled?.SecurityFunction ?? methods.FirstOrDefault(x => allowed.Contains(x.SecurityFunction))?.SecurityFunction ?? allowed[0];
        }
        else if (security == FinTsMessages.OneStepSecurityFunction && methods.Count > 0)
        {
            security = methods.FirstOrDefault(x => x.IsDecoupled)?.SecurityFunction ?? methods[0].SecurityFunction;
        }

        return new FinTsBankParameters(bpd, upd, systemId, security, current.TanMedium, versions, tanRequired, methods, accounts);
    }

    public static FinTsTanChallenge? Challenge(FinTsResponse response, FinTsBankParameters parameters)
    {
        var hitan = response.Find("HITAN");
        if (hitan is null) return null;
        var task = hitan.Version >= 6 ? NonEmpty(hitan.GetText(2, 0), hitan.GetText(3, 0)) : hitan.GetText(2, 0);
        var challenge = hitan.Version >= 6 ? NonEmpty(hitan.GetText(4, 0), hitan.GetText(3, 0)) : hitan.GetText(3, 0);
        if (string.IsNullOrWhiteSpace(task) && string.IsNullOrWhiteSpace(challenge)) return null;
        var hhd = hitan.Version >= 6 ? hitan.GetBinary(5, 0) : null;
        var decoupled = response.Codes.Any(x => x.Code is "3955" or "3956") || parameters.TanMethods.Any(x => x.SecurityFunction == parameters.SecurityFunction && x.IsDecoupled);
        return new FinTsTanChallenge(task, challenge, decoupled, hhd);
    }

    public static FinTsBalance? Balance(FinTsResponse response)
    {
        var segment = response.Find("HISAL");
        if (segment is null) return null;
        var balance = segment.Groups.Count > 4 ? segment.Groups[4] : null;
        if (balance is null || balance.Values.Count < 2) return null;
        var dc = Text(balance, 0);
        if (!TryDecimal(Text(balance, 1), out var amount)) return null;
        if (dc == "D") amount = -amount;
        var currency = NonEmpty(Text(balance, 2), segment.GetText(3, 0), "EUR");
        var date = ParseDate(Text(balance, 3)) ?? DateOnly.FromDateTime(DateTime.UtcNow);
        decimal? credit = segment.Groups.Count > 6 && TryDecimal(Text(segment.Groups[6], 0), out var c) ? c : null;
        decimal? available = segment.Groups.Count > 7 && TryDecimal(Text(segment.Groups[7], 0), out var a) ? a : null;
        return new FinTsBalance(amount, currency, date, available, credit);
    }

    public static IReadOnlyList<FinTsTransaction> Transactions(FinTsResponse response)
    {
        var items = new List<FinTsTransaction>();
        foreach (var segment in response.FindAll("HIKAZ"))
        {
            if (segment.GetBinary(1, 0) is { Length: > 0 } booked) items.AddRange(ParseMt940(booked, false));
            if (segment.GetBinary(2, 0) is { Length: > 0 } pending) items.AddRange(ParseMt940(pending, true));
        }
        return items;
    }

    /// <summary>
    /// Der Depotbestand aus HIWPD.
    ///
    /// HIWPD traegt die Aufstellung als MT535 in einem Binaerfeld - das ist das Format, und es wird
    /// jetzt gelesen (#130 §5). Vorher wurde ausschliesslich geraten: die erste Zahl des Segments galt
    /// als Stueckzahl, die zweite als Kurs, die dritte als Wert. Das stimmt, solange die Bank genau
    /// diese drei in genau dieser Reihenfolge schickt, und sonst nie - auffallen wuerde es erst am
    /// Vermoegen.
    ///
    /// Die alte Auslegung bleibt als Rueckfall fuer Antworten OHNE MT535 stehen. Sie raet weiterhin,
    /// aber sie ist dann das Einzige, was da ist; und sie kommt nicht mehr zum Zug, sobald eine
    /// richtige Aufstellung vorliegt.
    /// </summary>
    public static IReadOnlyList<FinTsHolding> Holdings(FinTsResponse response)
    {
        var result = new List<FinTsHolding>();
        foreach (var segment in response.FindAll("HIWPD"))
        {
            var statement = Mt535Text(segment);
            if (statement is not null)
            {
                result.AddRange(Mt535Parser.Parse(statement));
                continue;
            }

            for (var i = 2; i < segment.Groups.Count; i++)
            {
                var holding = ParseHolding(segment.Groups[i]);
                if (holding is not null) result.Add(holding);
            }
        }
        return result;
    }

    /// <summary>
    /// Was in der Antwort auf einen Depotabruf stand - der Bauplan, nicht der Inhalt.
    ///
    /// Genau die Unterscheidung, die gefehlt hat: kein HIWPD (die Bank hat nichts geliefert), HIWPD
    /// ohne erkanntes MT535 (das Format sieht anders aus als erwartet), MT535 erkannt aber nichts
    /// gelesen (der Parser kommt damit nicht zurecht). Alle drei sahen vorher gleich aus - naemlich
    /// nach einem leeren Depot.
    ///
    /// Protokolliert werden nur Anzahl und Laenge. Namen, Kennungen und Betraege bleiben drin.
    /// </summary>
    public static string HoldingsShape(FinTsResponse response, int parsed)
    {
        var segments = response.FindAll("HIWPD").ToArray();
        var kinds = response.Segments.Select(segment => segment.Type).Distinct().ToArray();
        var parts = new List<string> { "Segments=" + string.Join("+", kinds), "HIWPD=" + segments.Length };
        foreach (var segment in segments)
        {
            var text = Mt535Text(segment);
            parts.Add(text is null
                ? $"v{segment.Version}:kein-MT535:{segment.Groups.Count - 1}-Felder"
                : $"v{segment.Version}:MT535:{text.Length}-Zeichen:[{string.Join("|", Mt535Parser.FieldShape(text))}]:Text=[{string.Join("|", Mt535Parser.NarrativeShape(text))}]");
        }
        parts.Add($"Bestaende={parsed}");
        return string.Join(", ", parts);
    }

    /// <summary>Die MT535-Aufstellung des Segments - als Binaerfeld oder, seltener, als Text.</summary>
    private static string? Mt535Text(FinTsSegment segment)
    {
        foreach (var group in segment.Groups)
        {
            foreach (var value in group.Values)
            {
                var text = value switch
                {
                    FinTsValue.Binary binary => System.Text.Encoding.Latin1.GetString(binary.Value),
                    FinTsValue.Text plain => plain.Value,
                    _ => null
                };
                if (Mt535Parser.LooksLikeStatement(text)) return text;
            }
        }
        return null;
    }

    private static IReadOnlyList<FinTsResponseCode> ParseCodes(IReadOnlyList<FinTsSegment> segments)
    {
        var result = new List<FinTsResponseCode>();
        foreach (var segment in segments.Where(x => x.Type is "HIRMG" or "HIRMS"))
        {
            // Das Bezugssegment steht im SEGMENTKOPF an Stelle 4, nicht bei der Rueckmeldung selbst -
            // und nur in der Kreditinstitutsnachricht. Genau darueber ordnet die Bank zu, welcher
            // Auftrag falsch war: HIRMG gilt fuer die ganze Nachricht, HIRMS fuer ein Segment.
            var reference = int.TryParse(segment.GetText(0, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
                ? number
                : (int?)null;
            for (var i = 1; i < segment.Groups.Count; i++)
            {
                var group = segment.Groups[i];
                var code = Text(group, 0);
                if (code.Length != 4 || !code.All(char.IsDigit)) continue;
                var element = Text(group, 1);
                var text = Text(group, Math.Min(2, group.Values.Count - 1));
                var parameters = group.Values.Skip(3).OfType<FinTsValue.Text>().Select(x => x.Value).ToArray();
                result.Add(new FinTsResponseCode(code, element, text, parameters, reference));
            }
        }
        return result;
    }

    /// <summary>
    /// Ein Konto aus HIUPD (FinTS 3.0 Formals, E "Kontoinformation").
    ///
    /// Die Stellen verschieben sich mit der Segmentversion, weil #6 die IBAN an Stelle 3 EINSCHIEBT:
    ///
    /// <code>
    ///           bis #5                      ab #6
    /// 2  Kontoverbindung             2  Kontoverbindung
    /// 3  Kunden-ID                   3  IBAN
    /// 4  Kontoart                    4  Kunden-ID
    /// 5  Kontowaehrung               5  Kontoart
    /// 6  Name Kontoinhaber 1         6  Kontowaehrung
    /// 7  Name Kontoinhaber 2         7  Name Kontoinhaber 1
    /// 8  Kontoproduktbezeichnung     8  Name Kontoinhaber 2
    ///                                9  Kontoproduktbezeichnung
    /// </code>
    ///
    /// Gelesen wurden bisher fest die Stellen von #6. Bei einer Bank, die #5 schickt - ING tut es -
    /// stand damit der zweite Kontoinhaber im Feld des ersten und das Kontolimit in der
    /// Produktbezeichnung.
    /// </summary>
    private static FinTsAccount? ParseAccount(FinTsSegment segment)
    {
        if (segment.Groups.Count < 2) return null;
        var accountGroup = segment.Groups[1];
        var all = segment.Groups.SelectMany(x => x.Values).OfType<FinTsValue.Text>().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var shift = segment.Version >= 6 ? 1 : 0;

        var iban = all.FirstOrDefault(x => IbanRegex.IsMatch(x)) ?? string.Empty;
        var bic = all.FirstOrDefault(x => BicRegex.IsMatch(x)) ?? string.Empty;
        var accountNumber = Text(accountGroup, 0);
        var sub = Text(accountGroup, 1);
        var currency = FirstUseful(segment, 4 + shift) is { } declared && IsCurrency(declared)
            ? declared
            : all.FirstOrDefault(IsCurrency) ?? "EUR";
        var owner = FirstUseful(segment, 5 + shift, 6 + shift);
        var product = FirstUseful(segment, 7 + shift);
        var kind = FirstUseful(segment, 3 + shift);

        // Stelle 4 der klassischen Kontoverbindung ist der Kreditinstitutscode. Nennt die Bank ihn,
        // wird er uebernommen; sonst steht er in der IBAN (#130 §3).
        var bankCode = Text(accountGroup, 3);
        if (string.IsNullOrWhiteSpace(iban) && string.IsNullOrWhiteSpace(accountNumber)) return null;
        return new FinTsAccount(iban, bic, accountNumber, sub, owner, product, currency, IsDepot(kind, product),
            string.IsNullOrWhiteSpace(bankCode) ? null : bankCode);
    }

    /// <summary>
    /// Ob ein Konto ein Depot ist - an der Kontoart, nicht am Namen.
    ///
    /// FinTS 3.0 Formals, Data Dictionary "Kontoart": 30-39 Wertpapierdepot, 60-69 Fonds-Depot bei
    /// einer Kapitalanlagegesellschaft.
    ///
    /// Bisher galt als Depot, was "Depot" in der Produktbezeichnung stehen hatte. Das ist geraten und
    /// nicht gelesen: ein Depot, das die Bank anders nennt, war einfach ein Girokonto - und der
    /// Bestand wurde nie abgerufen. Die Kontoart ist optional, deshalb bleibt der Name als Rueckfall -
    /// aber nur, wenn die Bank die Kontoart gar nicht nennt.
    /// </summary>
    private static bool IsDepot(string? accountKind, string? product)
        => int.TryParse(accountKind, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind)
            ? kind is (>= 30 and <= 39) or (>= 60 and <= 69)
            : (product ?? string.Empty).Contains("Depot", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, bool> ParsePinTanRules(FinTsSegment segment)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in segment.Groups.Skip(3))
        {
            var values = group.Values.OfType<FinTsValue.Text>().Select(x => x.Value).ToArray();
            for (var i = 0; i + 1 < values.Length; i++)
            {
                if (values[i].StartsWith("HK", StringComparison.Ordinal) && values[i].Length is >= 5 and <= 8)
                    result[values[i]] = values[i + 1] == "J";
            }
        }
        return result;
    }

    /// <summary>
    /// Zahl der Datenelemente EINES Verfahrens, nach Elementversion - FinTS 3.0, Security -
    /// Sicherheitsverfahren PIN/TAN, Data-Dictionary "Verfahrensparameter Zwei-Schritt-Verfahren".
    ///
    /// Aeltere Versionen als 4 stehen hier nicht. Nicht weil es sie nicht gibt, sondern weil ich ihre
    /// Feldfolge nicht belegt habe - und eine falsche Aufteilung erzeugt genau den Müll, der diesen
    /// Umbau ausgeloest hat. Gebraucht werden sie auch nicht: die starke Authentifizierung bei der
    /// Dialoginitialisierung gibt es laut Spezifikation erst ab #6.
    /// </summary>
    private static int MethodStride(int version) => version switch
    {
        4 or 5 => 22,
        6 => 21,
        7 => 26,
        _ => 0
    };

    /// <summary>
    /// Die TAN-Verfahren aus HITANS (#130 §8).
    ///
    /// Stelle 5 von HITANS ist EIN Parameterblock, keine Gruppe je Verfahren:
    ///
    /// <code>
    /// 1 Einschritt-Verfahren erlaubt (J/N)
    /// 2 Mehr als ein TAN-pflichtiger Auftrag pro Nachricht erlaubt (J/N)
    /// 3 Auftrags-Hashwertverfahren
    /// 4 Verfahrensparameter Zwei-Schritt-Verfahren - 1..98 WIEDERHOLUNGEN
    /// </code>
    ///
    /// Vorher galt jede Gruppe ab Nummer 4 als ein Verfahren und ihr erstes Feld als
    /// Sicherheitsfunktion. Damit wurde das Ja/Nein-Kennzeichen "Einschritt-Verfahren erlaubt" zur
    /// Sicherheitsfunktion: im Protokoll stand bei ING <c>TanMethods=J:v1:0</c>. Eine
    /// Sicherheitsfunktion ist dreistellig und liegt zwischen 900 und 997 - "J" ist keine, und alle
    /// echten Verfahren waren verloren.
    ///
    /// Gelesen wird nur, was belegt ist: Sicherheitsfunktion und TAN-Prozess stehen in jeder Version
    /// an derselben Stelle, der Name ab #5, alles Weitere ab #6.
    /// </summary>
    private static IEnumerable<FinTsTanMethod> ParseTanMethods(FinTsSegment segment)
    {
        var stride = MethodStride(segment.Version);
        if (stride == 0 || segment.Groups.Count <= 4) yield break;

        var values = segment.Groups[4].Values
            .Select(x => x is FinTsValue.Text text ? text.Value : string.Empty)
            .ToArray();

        // Die ersten drei Stellen gehoeren dem Parameterblock selbst, nicht dem ersten Verfahren.
        for (var start = 3; start < values.Length; start += stride)
        {
            // Das LETZTE Verfahren darf kuerzer sein als die Feldzahl: leere Felder am Ende duerfen
            // weggelassen werden, und die Draht-Darstellung traegt sie ohnehin nicht. Wer hier eine
            // volle Feldzahl verlangt, verliert genau das Verfahren, dessen optionale Endfelder die
            // Bank nicht belegt hat.
            var available = Math.Min(stride, values.Length - start);
            string Field(int index) => index < available ? values[start + index] : string.Empty;

            // Was nicht wie eine Sicherheitsfunktion aussieht, ist keine - sie ist dreistellig und
            // liegt zwischen 900 und 997. Lieber ein Verfahren weniger als eines, das die
            // Anmeldenachricht falsch macht.
            var security = Field(0);
            if (security.Length != 3 || !security.All(char.IsDigit)) continue;

            var name = segment.Version >= 5 ? Field(5) : string.Empty;
            var isDecoupled = segment.Version >= 7
                && Field(3).Contains("Decoupled", StringComparison.OrdinalIgnoreCase);
            // HKTAN Stelle 12: das TAN-Medium ist zu benennen, wenn die Bank es verlangt UND mehr als
            // ein aktives Medium kennt.
            var needsMedium = segment.Version >= 6
                && Field(18) == "2"
                && Number(Field(20), 0) > 1;

            yield return new FinTsTanMethod(
                security,
                string.IsNullOrWhiteSpace(name) ? $"TAN-{security}" : name,
                Field(1),
                needsMedium,
                isDecoupled,
                isDecoupled ? Number(Field(21), -1) : -1,
                isDecoupled ? Number(Field(22), 0) : 0,
                isDecoupled ? Number(Field(23), 0) : 0,
                segment.Version);
        }
    }

    private static int Number(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static IReadOnlyList<FinTsTransaction> ParseMt940(byte[] data, bool pending)
    {
        var text = Encoding.Latin1.GetString(data).Replace("\r\n", "\n");
        var result = new List<FinTsTransaction>();
        string currency = "EUR";
        var opening = Regex.Match(text, @":60[FM]:[CD][0-9]{6}([A-Z]{3})");
        if (opening.Success) currency = opening.Groups[1].Value;
        var matches = Regex.Matches(text, @":61:(?<date>[0-9]{6})(?<value>[0-9]{4})?(?<dc>[CD])(?<funds>[A-Z])?(?<amount>[0-9,]+)(?<rest>[^\n]*)(?:\n:86:(?<desc>.*?))?(?=\n:61:|\n:62[FM]:|\z)", RegexOptions.Singleline);
        foreach (Match match in matches)
        {
            var booking = ParseShortDate(match.Groups["date"].Value);
            var valueDate = match.Groups["value"].Success && booking.HasValue ? ParseMonthDay(booking.Value, match.Groups["value"].Value) : booking;
            if (!TryDecimal(match.Groups["amount"].Value, out var amount)) continue;
            if (match.Groups["dc"].Value == "D") amount = -amount;
            var desc = match.Groups["desc"].Value.Replace("\n", " ").Trim();
            var counterparty = ExtractMt940Field(desc, "32") ?? ExtractMt940Field(desc, "33");
            var keyMaterial = $"{booking:yyyyMMdd}|{valueDate:yyyyMMdd}|{amount}|{currency}|{match.Groups["rest"].Value}|{desc}|{pending}";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)));
            result.Add(new FinTsTransaction(key, booking, valueDate, amount, currency, counterparty, desc, match.Value.Trim(), pending));
        }
        return result;
    }

    private static FinTsHolding? ParseHolding(FinTsGroup group)
    {
        var values = group.Values.OfType<FinTsValue.Text>().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (values.Length < 3) return null;
        string? isin = values.FirstOrDefault(x => IsinRegex.IsMatch(x));
        string? wkn = values.FirstOrDefault(x => x.Length == 6 && x.All(char.IsLetterOrDigit) && x != isin);
        var decimals = values.Select((x, i) => (x, i)).Where(x => TryDecimal(x.x, out _)).ToArray();
        if (decimals.Length == 0) return null;
        TryDecimal(decimals[0].x, out var quantity);
        decimal? price = decimals.Length > 1 && TryDecimal(decimals[1].x, out var p) ? p : null;
        decimal? market = decimals.Length > 2 && TryDecimal(decimals[2].x, out var m) ? m : null;
        var currencies = values.Where(IsCurrency).ToArray();
        var date = values.Select(ParseDate).FirstOrDefault(x => x.HasValue);
        var name = values.FirstOrDefault(x => x.Length > 3 && !IsCurrency(x) && !IbanRegex.IsMatch(x) && !BicRegex.IsMatch(x) && !IsinRegex.IsMatch(x) && !TryDecimal(x, out _) && ParseDate(x) is null) ?? isin ?? wkn ?? "Wertpapier";
        var exchange = values.FirstOrDefault(x => x.Length is >= 2 and <= 10 && x.All(char.IsLetter) && !IsCurrency(x) && !string.Equals(x, name, StringComparison.Ordinal));
        return new FinTsHolding(isin, wkn, name, quantity, price, currencies.ElementAtOrDefault(0), date, market, currencies.ElementAtOrDefault(1) ?? currencies.ElementAtOrDefault(0), exchange);
    }

    private static string? ExtractMt940Field(string text, string id)
    {
        var match = Regex.Match(text, $@"\?{Regex.Escape(id)}(?<v>.*?)(?=\?[0-9]{{2}}|$)");
        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    private static bool SameAccount(FinTsAccount a, FinTsAccount b)
        => !string.IsNullOrWhiteSpace(a.Iban) && string.Equals(a.Iban, b.Iban, StringComparison.OrdinalIgnoreCase)
           || string.Equals(a.AccountNumber, b.AccountNumber, StringComparison.OrdinalIgnoreCase) && string.Equals(a.SubAccount, b.SubAccount, StringComparison.OrdinalIgnoreCase);

    private static string Text(FinTsGroup group, int index)
        => index >= 0 && group.Values.Count > index && group.Values[index] is FinTsValue.Text t ? t.Value : string.Empty;
    private static int ParseInt(FinTsGroup group, int index, int fallback)
        => int.TryParse(Text(group, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static string? FirstUseful(FinTsSegment segment, params int[] groups)
        => groups.Where(i => i < segment.Groups.Count).Select(i => Text(segment.Groups[i], 0)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    private static string NonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    private static bool IsCurrency(string value) => value.Length == 3 && value.All(char.IsUpper);
    private static bool TryDecimal(string? value, out decimal result)
        => decimal.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static DateOnly? ParseShortDate(string value)
        => DateOnly.TryParseExact(value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static DateOnly ParseMonthDay(DateOnly booking, string mmdd)
    {
        if (mmdd.Length != 4 || !int.TryParse(mmdd[..2], out var month) || !int.TryParse(mmdd[2..], out var day) || month is < 1 or > 12 || day < 1) return booking;
        var year = booking.Year;
        var candidate = new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
        if (candidate.DayNumber - booking.DayNumber > 180) candidate = candidate.AddYears(-1);
        if (booking.DayNumber - candidate.DayNumber > 180) candidate = candidate.AddYears(1);
        return candidate;
    }
}