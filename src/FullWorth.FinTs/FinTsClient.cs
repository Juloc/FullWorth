using System.Globalization;

namespace FullWorth.FinTs;

public sealed class FinTsClient(IFinTsTransport transport)
{
    public async Task<FinTsBankParameters> SynchronizeAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        credentials.Validate();
        ValidateBank(bank);
        var parameters = EmptyParameters();
        var session = new FinTsSessionState("0", 1, parameters);
        var response = await SendAsync(bank, credentials, session,
            [FinTsMessages.Identify(bank, credentials.UserId, "0"), FinTsMessages.ProcessPrep(parameters, credentials.ProductId), FinTsMessages.Sync()],
            null, cancellationToken);
        response.ThrowOnError(lastSentShape);
        var merged = FinTsResponseParser.MergeParameters(parameters, response);
        var dialogId = FinTsResponseParser.DialogId(response);
        var syncedSession = new FinTsSessionState(dialogId, 2, merged);
        if (dialogId != "0")
        {
            try { await SendAsync(bank, credentials, syncedSession, [FinTsMessages.End(dialogId)], null, cancellationToken); }
            catch { }
        }
        return merged;
    }

    public async Task<FinTsOpenResult> OpenAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsBankParameters parameters,
        CancellationToken cancellationToken = default)
    {
        credentials.Validate();
        ValidateBank(bank);
        var session = new FinTsSessionState("0", 1, parameters);
        var segments = new List<FinTsSegment>
        {
            FinTsMessages.Identify(bank, credentials.UserId, parameters.SystemId),
            FinTsMessages.ProcessPrep(parameters, credentials.ProductId)
        };
        if (TanVersion(parameters) is { } tanVersion)
            segments.Add(FinTsMessages.TanProcess4("HKIDN", tanVersion, parameters.TanMedium));

        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var merged = FinTsResponseParser.MergeParameters(parameters, response);
        var opened = new FinTsSessionState(FinTsResponseParser.DialogId(response), 2, merged);
        if (response.NeedsTan)
        {
            var challenge = FinTsResponseParser.Challenge(response, merged)
                            ?? throw new FinTsException("Bank requires TAN but returned no HITAN challenge.", "tan_challenge_missing");
            return new FinTsOpenResult(challenge.IsDecoupled ? FinTsResultKind.TanPending : FinTsResultKind.TanRequired, opened, challenge);
        }
        response.ThrowOnError(lastSentShape);
        return new FinTsOpenResult(FinTsResultKind.Success, opened);
    }

    public Task<FinTsOpenResult> SubmitDialogTanAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsTanChallenge challenge,
        string tan,
        CancellationToken cancellationToken = default)
        => ContinueDialogAsync(bank, credentials, session, challenge, tan, false, cancellationToken);

    public Task<FinTsOpenResult> PollDialogTanAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsTanChallenge challenge,
        CancellationToken cancellationToken = default)
        => ContinueDialogAsync(bank, credentials, session, challenge, string.Empty, true, cancellationToken);

    public async Task<FinTsResult<FinTsBalance>> GetBalanceAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsAccount account,
        CancellationToken cancellationToken = default)
    {
        var version = AccountVersion(session.Parameters, "HISALS", 7, 5, account);
        var segments = BusinessWithTan(session.Parameters, "HKSAL", FinTsMessages.Balance(account, version));
        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var next = Advance(session, response);
        if (response.NeedsTan) return TanResult<FinTsBalance>(response, next);
        response.ThrowOnError(lastSentShape);
        return FinTsResponseParser.Balance(response) is { } balance ? FinTsResult<FinTsBalance>.Success(balance, next) : FinTsResult<FinTsBalance>.Empty(next);
    }

    public async Task<FinTsResult<IReadOnlyList<FinTsTransaction>>> GetTransactionsAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsAccount account,
        DateOnly from,
        DateOnly to,
        string? touchdown = null,
        CancellationToken cancellationToken = default)
    {
        if (to < from) throw new ArgumentException("FinTS transaction end date must be >= start date.");
        var version = AccountVersion(session.Parameters, "HIKAZS", 7, 5, account);
        var request = FinTsMessages.Transactions(account, version, from, to, touchdown);
        IReadOnlyList<FinTsSegment> segments = touchdown is null ? BusinessWithTan(session.Parameters, "HKKAZ", request) : [request];
        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var next = Advance(session, response);
        if (response.NeedsTan) return TanResult<IReadOnlyList<FinTsTransaction>>(response, next);
        response.ThrowOnError(lastSentShape);
        return FinTsResult<IReadOnlyList<FinTsTransaction>>.Success(FinTsResponseParser.Transactions(response), next, response.Touchdown);
    }

    public async Task<FinTsResult<IReadOnlyList<FinTsHolding>>> GetPortfolioAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsAccount depot,
        string? touchdown = null,
        CancellationToken cancellationToken = default)
    {
        // Nennt die Bank keine Version, wird 6 angenommen und nicht 7 (#130 §4): HKWPD gibt es in
        // den Versionen 5 und 6. Eine 7 zu schicken heisst, eine Nachricht zu behaupten, die es nicht
        // gibt - die Bank lehnt sie ab, und der Fehler sieht aus, als koenne sie keine Depots.
        var version = AccountVersion(session.Parameters, "HIWPDS", 6, 5, depot);
        var request = FinTsMessages.Portfolio(depot, version, touchdown);
        IReadOnlyList<FinTsSegment> segments = touchdown is null ? BusinessWithTan(session.Parameters, "HKWPD", request) : [request];
        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var next = Advance(session, response);
        if (response.NeedsTan) return TanResult<IReadOnlyList<FinTsHolding>>(response, next);
        response.ThrowOnError(lastSentShape);
        var holdings = FinTsResponseParser.Holdings(response);
        LastPortfolioShape = FinTsResponseParser.HoldingsShape(response, holdings.Count);
        return holdings.Count == 0 && response.Touchdown is null
            ? FinTsResult<IReadOnlyList<FinTsHolding>>.Empty(next)
            : FinTsResult<IReadOnlyList<FinTsHolding>>.Success(holdings, next, response.Touchdown);
    }

    public Task<FinTsResult<FinTsBalance>> SubmitBalanceTanAsync(FinTsBankProfile bank, FinTsCredentials credentials, FinTsSessionState session, FinTsTanChallenge challenge, string tan, CancellationToken cancellationToken = default)
        => ContinueBusinessAsync(bank, credentials, session, challenge, tan, false, FinTsResponseParser.Balance, (r, s) => FinTsResult<FinTsBalance>.Empty(s), cancellationToken);

    public Task<FinTsResult<IReadOnlyList<FinTsTransaction>>> SubmitTransactionsTanAsync(FinTsBankProfile bank, FinTsCredentials credentials, FinTsSessionState session, FinTsTanChallenge challenge, string tan, CancellationToken cancellationToken = default)
        => ContinueBusinessAsync(bank, credentials, session, challenge, tan, false, r => (IReadOnlyList<FinTsTransaction>)FinTsResponseParser.Transactions(r), (r, s) => FinTsResult<IReadOnlyList<FinTsTransaction>>.Success([], s, r.Touchdown), cancellationToken);

    public Task<FinTsResult<IReadOnlyList<FinTsHolding>>> SubmitPortfolioTanAsync(FinTsBankProfile bank, FinTsCredentials credentials, FinTsSessionState session, FinTsTanChallenge challenge, string tan, CancellationToken cancellationToken = default)
        => ContinueBusinessAsync(bank, credentials, session, challenge, tan, false, r => (IReadOnlyList<FinTsHolding>)FinTsResponseParser.Holdings(r), (r, s) => FinTsResult<IReadOnlyList<FinTsHolding>>.Empty(s), cancellationToken);

    public async Task EndAsync(FinTsBankProfile bank, FinTsCredentials credentials, FinTsSessionState session, CancellationToken cancellationToken = default)
    {
        if (session.DialogId == "0") return;
        try { await SendAsync(bank, credentials, session, [FinTsMessages.End(session.DialogId)], null, cancellationToken); }
        catch { }
    }

    private async Task<FinTsOpenResult> ContinueDialogAsync(FinTsBankProfile bank, FinTsCredentials credentials, FinTsSessionState session, FinTsTanChallenge challenge, string tan, bool poll, CancellationToken cancellationToken)
    {
        var segment = poll
            ? FinTsMessages.TanPoll(challenge.TaskReference, TanVersion(session.Parameters) ?? LowestTanVersion, session.Parameters.TanMedium)
            : FinTsMessages.TanProcess2(challenge.TaskReference, TanVersion(session.Parameters) ?? LowestTanVersion, session.Parameters.TanMedium);
        var response = await SendAsync(bank, credentials, session, [segment], poll ? string.Empty : tan, cancellationToken);
        var next = Advance(session, response);
        if (response.DecoupledPending)
            return new FinTsOpenResult(FinTsResultKind.TanPending, next, FinTsResponseParser.Challenge(response, next.Parameters) ?? challenge);
        if (response.NeedsTan)
            return new FinTsOpenResult(FinTsResultKind.TanRequired, next, FinTsResponseParser.Challenge(response, next.Parameters) ?? challenge);
        response.ThrowOnError(lastSentShape);
        return new FinTsOpenResult(FinTsResultKind.Success, next);
    }

    private async Task<FinTsResult<T>> ContinueBusinessAsync<T>(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsTanChallenge challenge,
        string tan,
        bool poll,
        Func<FinTsResponse, T?> parser,
        Func<FinTsResponse, FinTsSessionState, FinTsResult<T>> empty,
        CancellationToken cancellationToken)
    {
        var segment = poll
            ? FinTsMessages.TanPoll(challenge.TaskReference, TanVersion(session.Parameters) ?? LowestTanVersion, session.Parameters.TanMedium)
            : FinTsMessages.TanProcess2(challenge.TaskReference, TanVersion(session.Parameters) ?? LowestTanVersion, session.Parameters.TanMedium);
        var response = await SendAsync(bank, credentials, session, [segment], poll ? string.Empty : tan, cancellationToken);
        var next = Advance(session, response);
        if (response.DecoupledPending || response.NeedsTan) return TanResult<T>(response, next, challenge);
        response.ThrowOnError(lastSentShape);
        var value = parser(response);
        return value is null ? empty(response, next) : FinTsResult<T>.Success(value, next, response.Touchdown);
    }

    /// <summary>
    /// Der Bauplan der zuletzt gesendeten Nachricht (#130 §11): Segmentart, Version, Anzahl der
    /// Datenelemente. Keine Werte - die Nachricht selbst traegt im PIN/TAN-Verfahren die PIN.
    /// </summary>
    /// <summary>
    /// Wie die Antwort auf den letzten Depotabruf AUSSAH - Segmente, erkanntes MT535, Zahl der
    /// Bestaende. Keine Namen, keine Kennungen, keine Betraege.
    ///
    /// Die gesendete Form (<c>SentShape</c>) hat drei Protokollfehler hintereinander erklaert. Fuer
    /// die Antwort gab es nichts Vergleichbares, und deshalb war "null Bestaende" nicht von "die
    /// Bank hat nichts geschickt" zu unterscheiden - bei einem Depot mit vier ETF-Positionen.
    ///
    /// FinTS-Antworten werden bewusst nie im Klartext protokolliert; ein Depotauszug ist der Inhalt
    /// eines Vermoegens. Die FORM zu beschreiben verraet davon nichts.
    /// </summary>
    public string LastPortfolioShape { get; private set; } = string.Empty;

    private IReadOnlyList<FinTsSegmentShape> lastSentShape = [];

    private async Task<FinTsResponse> SendAsync(FinTsBankProfile bank, FinTsCredentials credentials, FinTsSessionState session, IEnumerable<FinTsSegment> segments, string? tan, CancellationToken cancellationToken)
    {
        var sent = segments as IReadOnlyList<FinTsSegment> ?? [.. segments];
        var message = FinTsMessages.Build(bank, credentials, session, sent, tan);
        // Der Bauplan kommt aus der Nachricht, die wirklich rausgeht - samt Umschlag und
        // Signaturblock. Vorher standen nur die Geschaeftssegmente darin, und bei
        // "9010 Ungueltiger Signaturaufbau" zeigte er auf alles ausser die Stelle, um die es ging.
        // Werte stehen nicht drin: Art, Version und Zahl der Datenelemente, sonst nichts - im
        // PIN/TAN-Verfahren traegt der Signaturabschluss die PIN.
        lastSentShape = [.. FinTsResponseParser.Parse(message).Segments.Select(Shape)];
        var bytes = await transport.SendAsync(bank.Endpoint, message, cancellationToken);
        return FinTsResponseParser.Parse(bytes);
    }

    /// <summary>
    /// Art, Nummer, Version und Zahl der Datenelemente eines Segments - alles aus dem Kopf, kein Inhalt.
    ///
    /// Die Nummer ist der Schluessel zur Fehlermeldung: die Bank bezieht sich in HIRMS auf genau sie.
    /// </summary>
    private static FinTsSegmentShape Shape(FinTsSegment segment)
    {
        var header = segment.Groups.Count > 0 ? segment.Groups[0] : null;
        var type = HeaderText(header, 0) ?? "?";
        return new FinTsSegmentShape(type, HeaderNumber(header, 2), segment.Groups.Count - 1, HeaderNumber(header, 1));
    }

    private static string? HeaderText(FinTsGroup? header, int index)
        => header is not null && header.Values.Count > index && header.Values[index] is FinTsValue.Text value ? value.Value : null;

    private static int HeaderNumber(FinTsGroup? header, int index)
        => int.TryParse(HeaderText(header, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static FinTsSessionState Advance(FinTsSessionState session, FinTsResponse response)
    {
        var parameters = FinTsResponseParser.MergeParameters(session.Parameters, response);
        var dialog = FinTsResponseParser.DialogId(response);
        if (string.IsNullOrWhiteSpace(dialog) || dialog == "0") dialog = session.DialogId;
        return new FinTsSessionState(dialog, session.MessageNumber + 1, parameters);
    }

    /// <summary>
    /// Der Auftrag, und daneben die TAN-Ankuendigung - wenn HIPINS eine verlangt UND die Bank ein
    /// HKTAN ankuendigt, das dieser Code schreiben kann.
    ///
    /// Kann er es nicht, geht der Auftrag allein hinaus. Ein HKTAN in einer Version, die die Bank
    /// nicht kennt, macht aus einem Auftrag, der vielleicht durchgegangen waere, sicher einen
    /// abgelehnten.
    /// </summary>
    /// <summary>
    /// Die Version eines kontobezogenen Geschaeftsvorfalls - begrenzt auf das, was das Konto hergibt.
    ///
    /// Erst ab Version 7 traegt die Nachricht die internationale Kontoverbindung, in der IBAN und BIC
    /// stehen. Ein Depot hat keine IBAN, es wird ueber seine Depotnummer angesprochen - eine Version 7
    /// waere fuer es eine Nachricht ohne Konto. Bis Version 6 steht dort die klassische
    /// Kontoverbindung aus Kontonummer und Bankleitzahl, und die hat es.
    ///
    /// Die Grenze stand hier bei 5, weil sie in FinTsMessages.AccountGroup auch bei 6 stand. Sie war
    /// an beiden Stellen um eins zu tief.
    /// </summary>
    private static int AccountVersion(FinTsBankParameters parameters, string parameterSegment, int fallback, int minimum, FinTsAccount account)
    {
        var announced = parameters.VersionFor(parameterSegment, fallback, minimum);
        return string.IsNullOrWhiteSpace(account.Iban) ? Math.Min(announced, 6) : announced;
    }

    private static IReadOnlyList<FinTsSegment> BusinessWithTan(FinTsBankParameters parameters, string requestType, FinTsSegment business)
        => parameters.RequiresTan(requestType) && TanVersion(parameters) is { } version
            ? [business, FinTsMessages.TanProcess4(requestType, version, parameters.TanMedium)]
            : [business];

    private static FinTsResult<T> TanResult<T>(FinTsResponse response, FinTsSessionState session, FinTsTanChallenge? fallback = null)
    {
        var challenge = FinTsResponseParser.Challenge(response, session.Parameters) ?? fallback
                        ?? throw new FinTsException("Bank requires TAN but returned no challenge.", "tan_challenge_missing");
        return challenge.IsDecoupled ? FinTsResult<T>.TanPending(session, challenge) : FinTsResult<T>.TanRequired(session, challenge);
    }

    /// <summary>
    /// Die aelteste Segmentversion, in der es die starke Kundenauthentifizierung bei der
    /// Dialoginitialisierung ueberhaupt gibt.
    ///
    /// FinTS 3.0, Security - Sicherheitsverfahren PIN/TAN, B.4.2: "Unterstuetzt ein Kreditinstitut die
    /// starke Kundenauthentifizierung mithilfe von HKTAN ab #6, so sollte ein Kundenprodukt in die
    /// Segmentfolge der Dialoginitialisierung grundlegend ein HKTAN-Segment ab #6 einstellen."
    ///
    /// Die Bedingung steht im ersten Halbsatz und wurde bisher ueberlesen: kuendigt die Bank HITANS
    /// nur in einer aelteren Version an, gibt es diesen Ablauf bei ihr nicht. Und dieselbe Grenze gilt
    /// technisch: <see cref="FinTsMessages.TanProcess4"/> baut den Aufbau ab Version 6 - Segmentkennung
    /// an Stelle 2, TAN-Medium an Stelle 11. Darunter steht an Stelle 2 der Auftrags-Hashwert.
    /// </summary>
    private const int LowestTanVersion = 6;

    /// <summary>
    /// Die Segmentversion, in der HKTAN zu bauen ist - oder <c>null</c>, wenn es nicht mitgeschickt
    /// werden darf.
    ///
    /// Genommen wird, was die Bank in HITANS ankuendigt: die Version des gewaehlten Verfahrens, sonst
    /// die hoechste angekuendigte HITANS-Version ueberhaupt.
    ///
    /// Hier stand eine feste Untergrenze, die aus JEDER Ankuendigung eine 6 machte. ING kuendigt eine
    /// aeltere Version an und kennt HKTAN #6 nicht; die Bank antwortete entsprechend
    /// "9050 Nachricht teilweise fehlerhaft" mit "9010@5 HKTAN Der gewuenschte Geschaeftsvorfall wird
    /// nicht unterstuetzt" - und sie hatte recht. Wer keine starke Authentifizierung bei der
    /// Anmeldung anbietet, bekommt auch kein HKTAN.
    /// </summary>
    private static int? TanVersion(FinTsBankParameters parameters)
    {
        var announced = parameters.TanMethods
            .Where(x => x.SecurityFunction == parameters.SecurityFunction)
            .Select(x => x.SegmentVersion)
            .DefaultIfEmpty(parameters.SegmentVersions.TryGetValue("HITANS", out var value) ? value : 0)
            .Max();
        return announced >= LowestTanVersion ? announced : null;
    }

    private static FinTsBankParameters EmptyParameters()
        => new(0, 0, "0", FinTsMessages.OneStepSecurityFunction, null,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            [], []);

    private static void ValidateBank(FinTsBankProfile bank)
    {
        if (bank.Endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("FinTS bank endpoint must use HTTPS.");
        if (string.IsNullOrWhiteSpace(bank.Blz)) throw new ArgumentException("FinTS bank BLZ is required.");
    }
}
