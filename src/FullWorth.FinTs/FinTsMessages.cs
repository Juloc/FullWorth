using System.Globalization;

namespace FullWorth.FinTs;

internal static class FinTsMessages
{
    private static FinTsGroup Header(string type, int number, int version, int? reference = null)
        => FinTsGroup.Of(
            FinTsValue.T(type),
            FinTsValue.T(number.ToString(CultureInfo.InvariantCulture)),
            FinTsValue.T(version.ToString(CultureInfo.InvariantCulture)),
            reference.HasValue ? FinTsValue.T(reference.Value.ToString(CultureInfo.InvariantCulture)) : FinTsValue.E());

    internal static byte[] Build(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        IEnumerable<FinTsSegment> businessSegments,
        string? tan = null)
    {
        var securityReference = Random.Shared.Next(1_000_000, 9_999_999);
        var number = 2;
        var inner = new List<FinTsSegment>
        {
            SignatureHeader(number++, session.Parameters.SecurityFunction, securityReference, bank.Blz, credentials.UserId, session.Parameters.SystemId)
        };
        foreach (var source in businessSegments)
        {
            var groups = source.Groups.ToArray();
            var first = groups[0].Values.ToArray();
            first[1] = FinTsValue.T((number++).ToString(CultureInfo.InvariantCulture));
            groups[0] = new FinTsGroup(first);
            inner.Add(new FinTsSegment(groups));
        }
        inner.Add(SignatureFooter(number++, securityReference, credentials.Pin, tan));
        var innerBytes = FinTsWire.Serialize(inner);

        var outer = new List<FinTsSegment>
        {
            EncryptionHeader(bank.Blz, credentials.UserId, session.Parameters.SystemId, session.Parameters.SecurityFunction),
            new([Header("HNVSD", 999, 1), FinTsGroup.Of(FinTsValue.B(innerBytes))]),
            new([Header("HNHBS", number, 1), FinTsGroup.Of(FinTsValue.T(session.MessageNumber.ToString(CultureInfo.InvariantCulture)))])
        };
        var body = FinTsWire.Serialize(outer);
        var placeholder = MessageHeader(0, session.DialogId, session.MessageNumber);
        var placeholderBytes = FinTsWire.SerializeSegment(placeholder);
        var totalSize = placeholderBytes.Length + body.Length;
        var finalHeader = FinTsWire.SerializeSegment(MessageHeader(totalSize, session.DialogId, session.MessageNumber));
        using var stream = new MemoryStream(finalHeader.Length + body.Length);
        stream.Write(finalHeader);
        stream.Write(body);
        return stream.ToArray();
    }

    internal static FinTsSegment Identify(FinTsBankProfile bank, string userId, string systemId)
        => new([
            Header("HKIDN", 0, 2),
            FinTsGroup.Of(FinTsValue.T("280"), FinTsValue.T(bank.Blz)),
            FinTsGroup.Of(FinTsValue.T(userId)),
            FinTsGroup.Of(FinTsValue.T(systemId)),
            FinTsGroup.Of(FinTsValue.T("1"))
        ]);

    internal static FinTsSegment ProcessPrep(FinTsBankParameters parameters, string productId)
        => new([
            Header("HKVVB", 0, 3),
            FinTsGroup.Of(FinTsValue.T(parameters.BpdVersion.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T(parameters.UpdVersion.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T(productId)),
            FinTsGroup.Of(FinTsValue.T("1.0"))
        ]);

    internal static FinTsSegment Sync()
        => new([Header("HKSYN", 0, 3), FinTsGroup.Of(FinTsValue.T("0"))]);

    internal static FinTsSegment End(string dialogId)
        => new([Header("HKEND", 0, 1), FinTsGroup.Of(FinTsValue.T(dialogId))]);

    internal static FinTsSegment Balance(FinTsAccount account, int version, string? touchdown = null)
    {
        var groups = new List<FinTsGroup> { Header("HKSAL", 0, version), AccountGroup(account, version), FinTsGroup.Of(FinTsValue.T("N")), FinTsGroup.Of(FinTsValue.E()) };
        if (!string.IsNullOrWhiteSpace(touchdown)) groups.Add(FinTsGroup.Of(FinTsValue.T(touchdown)));
        return new(groups);
    }

    internal static FinTsSegment Transactions(FinTsAccount account, int version, DateOnly from, DateOnly to, string? touchdown = null)
    {
        var groups = new List<FinTsGroup>
        {
            Header("HKKAZ", 0, version), AccountGroup(account, version), FinTsGroup.Of(FinTsValue.T("N")),
            FinTsGroup.Of(FinTsValue.T(from.ToString("yyyyMMdd", CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T(to.ToString("yyyyMMdd", CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.E())
        };
        if (!string.IsNullOrWhiteSpace(touchdown)) groups.Add(FinTsGroup.Of(FinTsValue.T(touchdown)));
        return new(groups);
    }

    /// <summary>
    /// HKWPD, die Depotaufstellung. Nach Stellen: 1 Depot, 2 Waehrung der Depotaufstellung,
    /// 3 Kursqualitaet, 4 maximale Anzahl Eintraege, 5 Aufsetzpunkt. Alles ausser dem Depot ist
    /// optional, und leer heisst "nicht angegeben".
    ///
    /// An Stelle 2 stand die Waehrung des Depots. Sie dort hinzuschreiben ist die Bitte, in dieser
    /// Waehrung auszugeben - und die ING beantwortet sie mit "9210 Die Angabe einer Ausgabewaehrung
    /// ist nicht zulaessig". Gebraucht wurde sie nie: die Bestaende kommen in MT535 mit ihrer eigenen
    /// Waehrung, und genau die wird uebernommen. Deshalb gibt es den Parameter nicht mehr, statt ihn
    /// nur nicht mehr zu belegen.
    /// </summary>
    internal static FinTsSegment Portfolio(FinTsAccount account, int version, string? touchdown = null)
    {
        var groups = new List<FinTsGroup>
        {
            Header("HKWPD", 0, version), AccountGroup(account, version),
            FinTsGroup.Of(FinTsValue.E()), FinTsGroup.Of(FinTsValue.E()), FinTsGroup.Of(FinTsValue.E())
        };
        if (!string.IsNullOrWhiteSpace(touchdown)) groups.Add(FinTsGroup.Of(FinTsValue.T(touchdown)));
        return new(groups);
    }

    /// <summary>
    /// HKTAN mit TAN-Prozess 4: der Auftrag wird angekuendigt, seine Daten stehen im Segment, das
    /// <paramref name="referencedSegment"/> benennt.
    ///
    /// Der Aufbau ab Segmentversion 6, nach Stellen:
    /// 1 TAN-Prozess, 2 Segmentkennung, 3 Kontoverbindung, 4 Auftrags-Hashwert, 5 Auftragsreferenz,
    /// 6 weitere TAN folgt, 7 Auftrag storniert, 8 SMS-Abbuchungskonto, 9 Challenge-Klasse,
    /// 10 Parameter Challenge-Klasse, 11 Bezeichnung des TAN-Mediums.
    ///
    /// Die Segmentkennung an Stelle 2 gibt es erst ab dieser Version; davor steht dort der
    /// Auftrags-Hashwert. Es gab hier einen Zweig fuer aeltere Versionen, der die Segmentkennung
    /// trotzdem schrieb und alles Weitere wegliess - also den Namen "HKIDN" in ein Hashfeld. Der
    /// Zweig ist weg: <paramref name="version"/> kommt aus <c>FinTsClient.TanVersion</c> und ist
    /// nie kleiner als 6.
    /// </summary>
    internal static FinTsSegment TanProcess4(string referencedSegment, int version, string? medium)
    {
        var groups = new List<FinTsGroup>
        {
            Header("HKTAN", 0, version),
            FinTsGroup.Of(FinTsValue.T("4")),                    // 1 TAN-Prozess
            FinTsGroup.Of(FinTsValue.T(referencedSegment)),      // 2 Segmentkennung
            FinTsGroup.Of(FinTsValue.E()),                       // 3 Kontoverbindung
            FinTsGroup.Of(FinTsValue.E()),                       // 4 Auftrags-Hashwert
            FinTsGroup.Of(FinTsValue.E()),                       // 5 Auftragsreferenz
            FinTsGroup.Of(FinTsValue.E()),                       // 6 weitere TAN folgt
            FinTsGroup.Of(FinTsValue.E())                        // 7 Auftrag storniert
        };
        if (!string.IsNullOrWhiteSpace(medium))
        {
            groups.Add(FinTsGroup.Of(FinTsValue.E()));           // 8 SMS-Abbuchungskonto
            groups.Add(FinTsGroup.Of(FinTsValue.E()));           // 9 Challenge-Klasse
            groups.Add(FinTsGroup.Of(FinTsValue.E()));           // 10 Parameter Challenge-Klasse
            groups.Add(FinTsGroup.Of(FinTsValue.T(medium)));     // 11 Bezeichnung des TAN-Mediums
        }
        return new(groups);
    }

    internal static FinTsSegment TanProcess2(string taskReference, int version, string? medium)
        => TanContinuation("2", taskReference, version, medium);

    internal static FinTsSegment TanPoll(string taskReference, int version, string? medium)
        => TanContinuation("S", taskReference, version, medium);

    /// <summary>
    /// HKTAN mit TAN-Prozess 2 (TAN einreichen) oder S (nachfragen, ob der Benutzer in der App
    /// freigegeben hat). Stellen wie in <see cref="TanProcess4"/>; die Auftragsreferenz steht an 5,
    /// "weitere TAN folgt" an 6.
    /// </summary>
    private static FinTsSegment TanContinuation(string process, string taskReference, int version, string? medium)
    {
        var groups = new List<FinTsGroup>
        {
            Header("HKTAN", 0, version),
            FinTsGroup.Of(FinTsValue.T(process)),                // 1 TAN-Prozess
            FinTsGroup.Of(FinTsValue.E()),                       // 2 Segmentkennung
            FinTsGroup.Of(FinTsValue.E()),                       // 3 Kontoverbindung
            FinTsGroup.Of(FinTsValue.E()),                       // 4 Auftrags-Hashwert
            FinTsGroup.Of(FinTsValue.T(taskReference)),          // 5 Auftragsreferenz
            FinTsGroup.Of(FinTsValue.T("N")),                    // 6 weitere TAN folgt
            FinTsGroup.Of(FinTsValue.E())                        // 7 Auftrag storniert
        };
        if (!string.IsNullOrWhiteSpace(medium))
        {
            groups.Add(FinTsGroup.Of(FinTsValue.E()));           // 8 SMS-Abbuchungskonto
            groups.Add(FinTsGroup.Of(FinTsValue.E()));           // 9 Challenge-Klasse
            groups.Add(FinTsGroup.Of(FinTsValue.E()));           // 10 Parameter Challenge-Klasse
            groups.Add(FinTsGroup.Of(FinTsValue.T(medium)));     // 11 Bezeichnung des TAN-Mediums
        }
        return new(groups);
    }

    private static FinTsSegment MessageHeader(int size, string dialogId, int messageNumber)
        => new([
            Header("HNHBK", 1, 3),
            FinTsGroup.Of(FinTsValue.T(size.ToString("D12", CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T("300")),
            FinTsGroup.Of(FinTsValue.T(dialogId)),
            FinTsGroup.Of(FinTsValue.T(messageNumber.ToString(CultureInfo.InvariantCulture)))
        ]);

    private static FinTsSegment EncryptionHeader(string blz, string userId, string systemId, string securityFunction)
    {
        var now = DateTime.Now;
        return new([
            Header("HNVSK", 998, 3),
            FinTsGroup.Of(FinTsValue.T("PIN"), FinTsValue.T(ProfileVersion(securityFunction))),
            FinTsGroup.Of(FinTsValue.T("998")),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("2"), FinTsValue.E(), FinTsValue.T(systemId)),
            FinTsGroup.Of(FinTsValue.T("1"), FinTsValue.T(now.ToString("yyyyMMdd")), FinTsValue.T(now.ToString("HHmmss"))),
            FinTsGroup.Of(FinTsValue.T("2"), FinTsValue.T("2"), FinTsValue.T("13"), FinTsValue.B(new byte[8]), FinTsValue.T("5"), FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("280"), FinTsValue.T(blz), FinTsValue.T(userId), FinTsValue.T("V"), FinTsValue.T("0"), FinTsValue.T("0")),
            FinTsGroup.Of(FinTsValue.T("0"))
        ]);
    }

    /// <summary>
    /// Die Sicherheitsfunktion des Einschritt-Verfahrens. Alles andere ist ein Zwei-Schritt-Verfahren.
    /// </summary>
    internal const string OneStepSecurityFunction = "999";

    /// <summary>
    /// Die Version des Sicherheitsverfahrens im Sicherheitsprofil: 1 fuer das Einschritt-, 2 fuer das
    /// Zwei-Schritt-Verfahren.
    ///
    /// Verschluesselungskopf und Signaturkopf beschreiben die Sicherheit DERSELBEN Nachricht. Sagen
    /// sie Verschiedenes, widerspricht sich die Nachricht in sich selbst. Genau das war der zweite
    /// Stand: HNSHK trug schon die 2, HNVSK noch die fest verdrahtete 1 - und ING blieb bei
    /// "9010 Ungueltiger Signaturaufbau". Deshalb steht die Ableitung hier einmal und nicht zweimal.
    /// </summary>
    private static string ProfileVersion(string securityFunction)
        => securityFunction == OneStepSecurityFunction ? "1" : "2";

    /// <summary>
    /// Der Signaturkopf.
    ///
    /// Das Sicherheitsprofil an Stelle 1 hat zwei Teile: das Verfahren ("PIN") und SEINE VERSION.
    /// Hier stand fest die 1, auch wenn die Sicherheitsfunktion daneben ein Zwei-Schritt-Verfahren
    /// benannte. Aufgefallen ist es lange nicht, weil der Synchronisationsdialog mit
    /// Sicherheitsfunktion 999 laeuft - dort IST es das Einschritt-Verfahren, und die 1 stimmt. Erst
    /// der Anmeldedialog schickt die echte Sicherheitsfunktion.
    /// </summary>
    private static FinTsSegment SignatureHeader(int number, string securityFunction, int reference, string blz, string userId, string systemId)
    {
        var now = DateTime.Now;
        return new([
            Header("HNSHK", number, 4),
            FinTsGroup.Of(FinTsValue.T("PIN"), FinTsValue.T(ProfileVersion(securityFunction))),
            FinTsGroup.Of(FinTsValue.T(securityFunction)),
            FinTsGroup.Of(FinTsValue.T(reference.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T("1")), FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("2"), FinTsValue.E(), FinTsValue.T(systemId)),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("1"), FinTsValue.T(now.ToString("yyyyMMdd")), FinTsValue.T(now.ToString("HHmmss"))),
            FinTsGroup.Of(FinTsValue.T("1"), FinTsValue.T("999"), FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("6"), FinTsValue.T("10"), FinTsValue.T("16")),
            FinTsGroup.Of(FinTsValue.T("280"), FinTsValue.T(blz), FinTsValue.T(userId), FinTsValue.T("S"), FinTsValue.T("0"), FinTsValue.T("0"))
        ]);
    }

    private static FinTsSegment SignatureFooter(int number, int reference, string pin, string? tan)
    {
        var signature = new List<FinTsValue> { FinTsValue.T(pin) };
        if (tan is not null) signature.Add(new FinTsValue.Text(tan));
        return new([
            Header("HNSHA", number, 2),
            FinTsGroup.Of(FinTsValue.T(reference.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.E()),
            new FinTsGroup(signature)
        ]);
    }

    /// <summary>
    /// Die Kontoverbindung. Ab Version 6 ist es die internationale (IBAN + BIC), davor die klassische:
    /// Kontonummer, Unterkontomerkmal, Laenderkennzeichen, Kreditinstitutscode.
    ///
    /// Das letzte Feld ist die BANKLEITZAHL. Hier stand der BIC (#130 §3) - ein anderes Feld mit einer
    /// anderen Laenge, das der Server bei Laenderkennzeichen 280 nicht annimmt.
    /// </summary>
    /// <summary>
    /// Die Kontoverbindung eines Auftrags - und welche Datenelementgruppe das ist, haengt an der
    /// SEGMENTVERSION. Drei Gruppen, nicht zwei:
    ///
    /// <list type="bullet">
    /// <item>bis 5: <c>Kontoverbindung</c> (Account2) - Kontonummer, Unterkontomerkmal,
    ///   Laenderkennzeichen, Kreditinstitutscode. Alle vier Pflicht.</item>
    /// <item>6: <c>Kontoverbindung</c> (Account3) - dieselben Angaben, nur sind Laenderkennzeichen
    ///   und Kreditinstitutscode zu einer Untergruppe zusammengefasst. Auf der Leitung steht
    ///   dasselbe. Ebenfalls alle Pflicht, und eine IBAN kommt darin nicht vor.</item>
    /// <item>ab 7: <c>Kontoverbindung international</c> (KTI) - IBAN, BIC, Kontonummer,
    ///   Unterkontomerkmal, Kreditinstitutskennung. Alle KANN, deshalb genuegt IBAN und BIC.</item>
    /// </list>
    ///
    /// Hier stand die Grenze bei 6 statt bei 7, und damit ging in eine Version-6-Nachricht eine
    /// IBAN, wo die Kontonummer hingehoert, ein BIC, wo das Unterkontomerkmal hingehoert - und die
    /// beiden Pflichtangaben dahinter gar nicht. Bei Giro- und Extra-Konten fiel es nicht auf, weil
    /// die ING fuer HKSAL und HKKAZ Version 7 ankuendigt und dort jedes Element optional ist: eine
    /// abgeschnittene KTI ist gueltig.
    ///
    /// HKWPD gibt es nur bis Version 6. Ein Depot lief also IMMER in die falsche Gruppe, und die ING
    /// antwortete, wie sie muss:
    ///
    /// <code>
    /// 9050 Nachricht teilweise fehlerhaft.; 9160@3 HKWPD Ein erforderliches Datenelement fehlt.
    /// SentShape=... HKWPD#3:v6:4 ...
    /// </code>
    /// </summary>
    private static FinTsGroup AccountGroup(FinTsAccount account, int version)
        => version >= 7
            ? FinTsGroup.Of(FinTsValue.T(account.Iban), FinTsValue.T(account.Bic))
            : FinTsGroup.Of(FinTsValue.T(account.AccountNumber ?? account.Iban), FinTsValue.T(account.SubAccount), FinTsValue.T("280"), FinTsValue.T(account.ResolvedBankCode));
}
