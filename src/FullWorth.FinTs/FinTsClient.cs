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
        response.ThrowOnError();
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
        if (parameters.TanMethods.Count > 0 || parameters.SecurityFunction != "999")
            segments.Add(FinTsMessages.TanProcess4("HKIDN", TanVersion(parameters), parameters.TanMedium));

        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var merged = FinTsResponseParser.MergeParameters(parameters, response);
        var opened = new FinTsSessionState(FinTsResponseParser.DialogId(response), 2, merged);
        if (response.NeedsTan)
        {
            var challenge = FinTsResponseParser.Challenge(response, merged)
                            ?? throw new FinTsException("Bank requires TAN but returned no HITAN challenge.", "tan_challenge_missing");
            return new FinTsOpenResult(challenge.IsDecoupled ? FinTsResultKind.TanPending : FinTsResultKind.TanRequired, opened, challenge);
        }
        response.ThrowOnError();
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
        var version = session.Parameters.VersionFor("HISALS", 7, 5);
        var segments = BusinessWithTan(session.Parameters, "HKSAL", FinTsMessages.Balance(account, version));
        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var next = Advance(session, response);
        if (response.NeedsTan) return TanResult<FinTsBalance>(response, next);
        response.ThrowOnError();
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
        var version = session.Parameters.VersionFor("HIKAZS", 7, 5);
        var request = FinTsMessages.Transactions(account, version, from, to, touchdown);
        IReadOnlyList<FinTsSegment> segments = touchdown is null ? BusinessWithTan(session.Parameters, "HKKAZ", request) : [request];
        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var next = Advance(session, response);
        if (response.NeedsTan) return TanResult<IReadOnlyList<FinTsTransaction>>(response, next);
        response.ThrowOnError();
        return FinTsResult<IReadOnlyList<FinTsTransaction>>.Success(FinTsResponseParser.Transactions(response), next, response.Touchdown);
    }

    public async Task<FinTsResult<IReadOnlyList<FinTsHolding>>> GetPortfolioAsync(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        FinTsAccount depot,
        string? currency = null,
        string? touchdown = null,
        CancellationToken cancellationToken = default)
    {
        var version = session.Parameters.VersionFor("HIWPDS", 7, 1);
        var request = FinTsMessages.Portfolio(depot, version, currency, touchdown);
        IReadOnlyList<FinTsSegment> segments = touchdown is null ? BusinessWithTan(session.Parameters, "HKWPD", request) : [request];
        var response = await SendAsync(bank, credentials, session, segments, null, cancellationToken);
        var next = Advance(session, response);
        if (response.NeedsTan) return TanResult<IReadOnlyList<FinTsHolding>>(response, next);
        response.ThrowOnError();
        var holdings = FinTsResponseParser.Holdings(response);
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
            ? FinTsMessages.TanPoll(challenge.TaskReference, TanVersion(session.Parameters), session.Parameters.TanMedium)
            : FinTsMessages.TanProcess2(challenge.TaskReference, TanVersion(session.Parameters), session.Parameters.TanMedium);
        var response = await SendAsync(bank, credentials, session, [segment], poll ? string.Empty : tan, cancellationToken);
        var next = Advance(session, response);
        if (response.DecoupledPending)
            return new FinTsOpenResult(FinTsResultKind.TanPending, next, FinTsResponseParser.Challenge(response, next.Parameters) ?? challenge);
        if (response.NeedsTan)
            return new FinTsOpenResult(FinTsResultKind.TanRequired, next, FinTsResponseParser.Challenge(response, next.Parameters) ?? challenge);
        response.ThrowOnError();
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
            ? FinTsMessages.TanPoll(challenge.TaskReference, TanVersion(session.Parameters), session.Parameters.TanMedium)
            : FinTsMessages.TanProcess2(challenge.TaskReference, TanVersion(session.Parameters), session.Parameters.TanMedium);
        var response = await SendAsync(bank, credentials, session, [segment], poll ? string.Empty : tan, cancellationToken);
        var next = Advance(session, response);
        if (response.DecoupledPending || response.NeedsTan) return TanResult<T>(response, next, challenge);
        response.ThrowOnError();
        var value = parser(response);
        return value is null ? empty(response, next) : FinTsResult<T>.Success(value, next, response.Touchdown);
    }

    private async Task<FinTsResponse> SendAsync(FinTsBankProfile bank, FinTsCredentials credentials, FinTsSessionState session, IEnumerable<FinTsSegment> segments, string? tan, CancellationToken cancellationToken)
    {
        var message = FinTsMessages.Build(bank, credentials, session, segments, tan);
        var bytes = await transport.SendAsync(bank.Endpoint, message, cancellationToken);
        return FinTsResponseParser.Parse(bytes);
    }

    private static FinTsSessionState Advance(FinTsSessionState session, FinTsResponse response)
    {
        var parameters = FinTsResponseParser.MergeParameters(session.Parameters, response);
        var dialog = FinTsResponseParser.DialogId(response);
        if (string.IsNullOrWhiteSpace(dialog) || dialog == "0") dialog = session.DialogId;
        return new FinTsSessionState(dialog, session.MessageNumber + 1, parameters);
    }

    private static IReadOnlyList<FinTsSegment> BusinessWithTan(FinTsBankParameters parameters, string requestType, FinTsSegment business)
        => parameters.RequiresTan(requestType)
            ? [business, FinTsMessages.TanProcess4(requestType, TanVersion(parameters), parameters.TanMedium)]
            : [business];

    private static FinTsResult<T> TanResult<T>(FinTsResponse response, FinTsSessionState session, FinTsTanChallenge? fallback = null)
    {
        var challenge = FinTsResponseParser.Challenge(response, session.Parameters) ?? fallback
                        ?? throw new FinTsException("Bank requires TAN but returned no challenge.", "tan_challenge_missing");
        return challenge.IsDecoupled ? FinTsResult<T>.TanPending(session, challenge) : FinTsResult<T>.TanRequired(session, challenge);
    }

    private static int TanVersion(FinTsBankParameters parameters)
        => Math.Max(4, parameters.TanMethods.Where(x => x.SecurityFunction == parameters.SecurityFunction).Select(x => x.SegmentVersion).DefaultIfEmpty(parameters.TanMethods.Select(x => x.SegmentVersion).DefaultIfEmpty(7).Max()).Max());

    private static FinTsBankParameters EmptyParameters()
        => new(0, 0, "0", "999", null,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            [], []);

    private static void ValidateBank(FinTsBankProfile bank)
    {
        if (bank.Endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("FinTS bank endpoint must use HTTPS.");
        if (string.IsNullOrWhiteSpace(bank.Blz)) throw new ArgumentException("FinTS bank BLZ is required.");
    }
}
