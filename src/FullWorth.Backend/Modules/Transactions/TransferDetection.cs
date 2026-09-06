using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

public enum TransferDetectionResult { Success, NotFound, Forbidden }
public sealed record TransferDetectionSummary(int Evaluated, int PairsLinked, bool Applied);
public sealed record TransferDetectionOutcome(TransferDetectionResult Result, TransferDetectionSummary? Summary = null);

public sealed record TransferCandidate(
    Guid Id,
    Guid AccountId,
    decimal Amount,
    string Currency,
    DateOnly? BookingDate,
    string? AccountIdentifierLookup = null,
    string? CounterpartyAccountLookup = null,
    string? AccountIbanLast4 = null,
    string? CounterpartyAccountLast4 = null);

public sealed record TransferCandidateLeg(Guid Id, Guid AccountId, string Account, decimal Amount, string Currency, DateOnly? BookingDate, string? Counterparty);
public sealed record TransferCandidatePair(
    TransferCandidateLeg First,
    TransferCandidateLeg Second,
    string Confidence = "suggested",
    IReadOnlyList<string>? Reasons = null);
public sealed record TransferCandidatesOutcome(TransferDetectionResult Result, List<TransferCandidatePair>? Pairs);

public sealed class TransferDetectionService(FullWorthDbContext db, FieldCipher? fieldCipher = null)
{
    private const int WindowDays = 3;
    private readonly FieldCipher cipher = fieldCipher ?? FieldCipher.Null;

    public async Task<TransferDetectionOutcome> DetectForUserAsync(Guid userId, Guid fullWorthSpaceId, bool apply, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => x.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return new(TransferDetectionResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(TransferDetectionResult.Forbidden);

        return await DetectForSpaceAsync(fullWorthSpaceId, apply, ct);
    }

    public async Task<TransferDetectionOutcome> DetectForSpaceAsync(Guid fullWorthSpaceId, bool apply, CancellationToken ct)
    {
        var candidates = await LoadCandidatesAsync(fullWorthSpaceId, ct);
        var pairs = FindAutomaticPairs(candidates, WindowDays);

        if (apply && pairs.Count > 0)
            await LinkPairsAsync(pairs, ct);

        return new(TransferDetectionResult.Success, new TransferDetectionSummary(candidates.Count, pairs.Count, apply));
    }

    public async Task<TransferCandidatesOutcome> CandidatesForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => x.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return new(TransferDetectionResult.NotFound, null);
        if (role != FullWorthSpaceRoles.Owner) return new(TransferDetectionResult.Forbidden, null);

        var candidates = await LoadCandidatesAsync(fullWorthSpaceId, ct);
        var pairs = FindMutualUniquePairs(candidates, WindowDays);
        if (pairs.Count == 0) return new(TransferDetectionResult.Success, []);

        var ids = pairs.SelectMany(p => new[] { p.First, p.Second }).ToHashSet();
        var details = await db.Transactions.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Select(t => new TransferCandidateLeg(
                t.Id,
                t.AccountId,
                db.Accounts.Where(a => a.Id == t.AccountId).Select(a => a.DisplayName).FirstOrDefault() ?? "",
                t.Amount,
                t.Currency,
                t.BookingDate,
                t.Counterparty))
            .ToDictionaryAsync(t => t.Id, ct);
        var byId = candidates.ToDictionary(x => x.Id);

        var result = pairs.Select(pair =>
        {
            var first = byId[pair.First];
            var second = byId[pair.Second];
            var exact = IdentifierRelationshipScore(first, second) > 0;
            var last4 = !exact && HasDirectionalLast4Match(first, second);
            var confidence = exact ? "high" : last4 ? "medium" : "suggested";
            var reasons = new List<string> { "opposite_amount", "same_currency", "date_window" };
            if (exact) reasons.Add("owned_account_identifier");
            else if (last4) reasons.Add("account_last4");
            return new TransferCandidatePair(details[pair.First], details[pair.Second], confidence, reasons);
        }).ToList();

        return new(TransferDetectionResult.Success, result);
    }

    public static List<(Guid First, Guid Second)> FindAutomaticPairs(IReadOnlyList<TransferCandidate> candidates, int windowDays)
    {
        var ordered = candidates
            .OrderBy(c => Math.Abs(c.Amount)).ThenBy(c => c.BookingDate).ThenBy(c => c.Id)
            .ToList();

        var options = ordered.ToDictionary(c => c.Id, _ => new List<(Guid Id, int Score)>());
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (!IsCounterpart(ordered[i], ordered[j], windowDays)) continue;
                var score = IdentifierRelationshipScore(ordered[i], ordered[j]);
                if (score <= 0) continue;
                options[ordered[i].Id].Add((ordered[j].Id, score));
                options[ordered[j].Id].Add((ordered[i].Id, score));
            }
        }

        var pairs = new List<(Guid First, Guid Second)>();
        var used = new HashSet<Guid>();
        foreach (var candidate in ordered)
        {
            if (used.Contains(candidate.Id)) continue;
            var best = BestUnused(options[candidate.Id], used);
            if (best.Count != 1) continue;

            var other = best[0];
            var reciprocal = BestUnused(options[other], used);
            if (reciprocal.Count != 1 || reciprocal[0] != candidate.Id) continue;

            pairs.Add((candidate.Id, other));
            used.Add(candidate.Id);
            used.Add(other);
        }
        return pairs;
    }

    public static List<(Guid First, Guid Second)> FindMutualUniquePairs(IReadOnlyList<TransferCandidate> candidates, int windowDays)
    {
        var ordered = candidates
            .OrderBy(c => Math.Abs(c.Amount)).ThenBy(c => c.BookingDate).ThenBy(c => c.Id)
            .ToList();

        var counterparts = ordered.ToDictionary(c => c.Id, _ => new List<Guid>());
        for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < ordered.Count; j++)
                if (IsCounterpart(ordered[i], ordered[j], windowDays))
                {
                    counterparts[ordered[i].Id].Add(ordered[j].Id);
                    counterparts[ordered[j].Id].Add(ordered[i].Id);
                }

        var pairs = new List<(Guid First, Guid Second)>();
        var used = new HashSet<Guid>();
        foreach (var candidate in ordered)
        {
            if (used.Contains(candidate.Id)) continue;
            var options = counterparts[candidate.Id].Where(id => !used.Contains(id)).ToList();
            if (options.Count != 1) continue;
            var other = options[0];
            var otherOptions = counterparts[other].Where(id => !used.Contains(id)).ToList();
            if (otherOptions.Count != 1) continue;
            pairs.Add((candidate.Id, other));
            used.Add(candidate.Id);
            used.Add(other);
        }
        return pairs;
    }

    private async Task<List<TransferCandidate>> LoadCandidatesAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.TransferGroupId == null && !t.IsIgnored && t.Amount != 0m && t.BookingDate != null)
            .Join(
                db.Accounts.AsNoTracking().Where(a => a.FullWorthSpaceId == fullWorthSpaceId),
                transaction => transaction.AccountId,
                account => account.Id,
                (transaction, account) => new CandidateRow(
                    transaction.Id,
                    transaction.AccountId,
                    transaction.Amount,
                    transaction.Currency,
                    transaction.BookingDate,
                    transaction.CounterpartyAccountLookup,
                    transaction.RawJson,
                    account.IbanLookup,
                    account.IbanLast4))
            .ToListAsync(ct);

        return rows.Select(row =>
        {
            var identifier = TryExtractCounterpartyIdentifier(row.RawJson, row.Amount);
            var lookup = row.CounterpartyAccountLookup ?? AccountIdentifierLookup.Create(identifier, cipher);
            return new TransferCandidate(
                row.Id,
                row.AccountId,
                row.Amount,
                row.Currency,
                row.BookingDate,
                row.AccountIdentifierLookup,
                lookup,
                row.AccountIbanLast4,
                AccountIdentifierLookup.Last4(identifier));
        }).ToList();
    }

    private async Task LinkPairsAsync(IReadOnlyList<(Guid First, Guid Second)> pairs, CancellationToken ct)
    {
        var ids = pairs.SelectMany(p => new[] { p.First, p.Second }).ToHashSet();
        var tracked = await db.Transactions.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var (first, second) in pairs)
        {
            if (!tracked.TryGetValue(first, out var firstTx) || !tracked.TryGetValue(second, out var secondTx))
                continue;
            if (firstTx.TransferGroupId is not null || secondTx.TransferGroupId is not null)
                continue;

            var groupId = Guid.NewGuid();
            foreach (var tx in new[] { firstTx, secondTx })
            {
                tx.TransferGroupId = groupId;
                tx.IsTransfer = true;
                tx.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static List<Guid> BestUnused(IReadOnlyList<(Guid Id, int Score)> options, HashSet<Guid> used)
    {
        var available = options.Where(x => !used.Contains(x.Id)).ToList();
        if (available.Count == 0) return [];
        var bestScore = available.Max(x => x.Score);
        return available.Where(x => x.Score == bestScore).Select(x => x.Id).Distinct().ToList();
    }

    private static int IdentifierRelationshipScore(TransferCandidate a, TransferCandidate b)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(a.CounterpartyAccountLookup) &&
            !string.IsNullOrWhiteSpace(b.AccountIdentifierLookup) &&
            string.Equals(a.CounterpartyAccountLookup, b.AccountIdentifierLookup, StringComparison.Ordinal))
            score++;
        if (!string.IsNullOrWhiteSpace(b.CounterpartyAccountLookup) &&
            !string.IsNullOrWhiteSpace(a.AccountIdentifierLookup) &&
            string.Equals(b.CounterpartyAccountLookup, a.AccountIdentifierLookup, StringComparison.Ordinal))
            score++;
        return score;
    }

    private static bool HasDirectionalLast4Match(TransferCandidate a, TransferCandidate b) =>
        Last4Matches(a.CounterpartyAccountLast4, b.AccountIbanLast4) ||
        Last4Matches(b.CounterpartyAccountLast4, a.AccountIbanLast4);

    private static bool Last4Matches(string? counterparty, string? account) =>
        counterparty is { Length: 4 } && account is { Length: 4 } &&
        string.Equals(counterparty, account, StringComparison.OrdinalIgnoreCase);

    private static bool IsCounterpart(TransferCandidate a, TransferCandidate b, int windowDays) =>
        a.AccountId != b.AccountId &&
        string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase) &&
        a.Amount == -b.Amount &&
        a.BookingDate.HasValue && b.BookingDate.HasValue &&
        Math.Abs(a.BookingDate.Value.DayNumber - b.BookingDate.Value.DayNumber) <= windowDays;

    private string? TryExtractCounterpartyIdentifier(string? storedRawJson, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(storedRawJson)) return null;
        try
        {
            var raw = cipher.Unprotect(storedRawJson);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var debit = root.TryGetProperty("credit_debit_indicator", out var indicator) &&
                        indicator.ValueKind == JsonValueKind.String
                ? string.Equals(indicator.GetString(), "DBIT", StringComparison.OrdinalIgnoreCase)
                : amount < 0m;
            return ReadAccountIdentifier(root, debit ? "creditor_account" : "debtor_account");
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static string? ReadAccountIdentifier(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(property, out var account) ||
            account.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "iban", "bban", "masked_pan", "pan" })
            if (account.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
    }

    private sealed record CandidateRow(
        Guid Id,
        Guid AccountId,
        decimal Amount,
        string Currency,
        DateOnly? BookingDate,
        string? CounterpartyAccountLookup,
        string RawJson,
        string? AccountIdentifierLookup,
        string? AccountIbanLast4);
}

public static class AccountIdentifierLookup
{
    public static string? Create(string? identifier, FieldCipher cipher)
    {
        var normalized = Normalize(identifier);
        if (normalized is null) return null;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return cipher.BlindIndex(digest);
    }

    public static string? Last4(string? identifier)
    {
        var normalized = Normalize(identifier);
        return normalized is { Length: >= 4 } ? normalized[^4..] : normalized;
    }

    private static string? Normalize(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        var normalized = new string(identifier.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}

public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/transfers/detect", async (
            Guid fullWorthSpaceId,
            bool? apply,
            CurrentUserContext currentUser,
            TransferDetectionService service,
            CancellationToken ct) =>
        {
            var outcome = await service.DetectForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, apply ?? false, ct);
            return outcome.Result switch
            {
                TransferDetectionResult.Success => Results.Ok(outcome.Summary),
                TransferDetectionResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        }).WithTags("Transfers");

        app.MapGet("/api/transfers/candidates", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            TransferDetectionService service,
            CancellationToken ct) =>
        {
            var outcome = await service.CandidatesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return outcome.Result switch
            {
                TransferDetectionResult.Success => Results.Ok(outcome.Pairs),
                TransferDetectionResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        }).WithTags("Transfers");
        return app;
    }
}
