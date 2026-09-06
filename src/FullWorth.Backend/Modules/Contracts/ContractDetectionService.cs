using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractCandidate(
    string Counterparty,
    decimal TypicalAmount,
    string Currency,
    string BillingCycle,
    int Interval,
    DateOnly LastPaymentDate,
    DateOnly NextDueDate,
    Guid? CategoryId,
    Guid? AccountId,
    int Samples,
    decimal AmountVariation,
    decimal Confidence);

public sealed class ContractDetectionService(
    FullWorthDbContext db,
    ContractStore contracts,
    IntelligenceFeedbackRecorder intelligenceFeedback)
{
    public async Task<List<ContractCandidate>?> DetectForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var isMember = await db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);
        if (!isMember) return null;

        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-450));
        var rows = await db.Transactions.AsNoTracking()
            .Where(transaction => db.Accounts.Any(account =>
                account.Id == transaction.AccountId &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Owners.Any(owner => owner.UserId == userId)))
            .Where(transaction => transaction.Amount < 0 &&
                                  !transaction.IsIgnored &&
                                  !transaction.IsTransfer &&
                                  transaction.BookingDate >= from &&
                                  transaction.BookingDate != null &&
                                  transaction.NormalizedCounterparty != null)
            .Select(transaction => new
            {
                transaction.NormalizedCounterparty,
                Date = transaction.BookingDate!.Value,
                Amount = -transaction.Amount,
                transaction.Currency,
                transaction.CategoryId,
                transaction.AccountId
            })
            .ToListAsync(ct);

        var result = new List<ContractCandidate>();
        foreach (var group in rows.GroupBy(x => new { x.NormalizedCounterparty, x.Currency }))
        {
            var entries = group.OrderBy(x => x.Date).ToList();
            if (entries.Count < 3) continue;

            var gaps = entries.Zip(entries.Skip(1), (a, b) => b.Date.DayNumber - a.Date.DayNumber).Where(x => x > 0).Order().ToArray();
            if (gaps.Length < 2) continue;
            var medianGap = gaps[gaps.Length / 2];
            var cycle = DetectCycle(medianGap);
            if (cycle is null) continue;

            var amounts = entries.Select(x => x.Amount).Order().ToArray();
            var medianAmount = amounts[amounts.Length / 2];
            if (medianAmount <= 0) continue;
            var maxDeviation = amounts.Max(x => Math.Abs(x - medianAmount));
            var variation = maxDeviation / medianAmount;
            if (variation > .35m) continue;

            var gapDeviation = gaps.Average(x => Math.Abs(x - medianGap));
            var gapScore = Math.Max(0m, 1m - (decimal)gapDeviation / Math.Max(1, medianGap));
            var amountScore = Math.Max(0m, 1m - variation);
            var sampleScore = Math.Min(1m, entries.Count / 6m);
            var confidence = Math.Clamp(gapScore * .45m + amountScore * .35m + sampleScore * .20m, 0m, 1m);
            if (confidence < .68m) continue;

            var last = entries[^1];
            result.Add(new ContractCandidate(
                group.Key.NormalizedCounterparty!,
                medianAmount,
                group.Key.Currency,
                cycle.Value.Cycle,
                cycle.Value.Interval,
                last.Date,
                AddCycle(last.Date, cycle.Value.Cycle, cycle.Value.Interval),
                entries.GroupBy(x => x.CategoryId).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault(),
                entries.GroupBy(x => x.AccountId).OrderByDescending(x => x.Count()).Select(x => (Guid?)x.Key).FirstOrDefault(),
                entries.Count,
                variation,
                confidence));
        }

        // Suppress candidates the owner has explicitly rejected (Wave I6 review state).
        var dismissed = (await db.DismissedContractCandidates.AsNoTracking()
                .Where(entry => entry.FullWorthSpaceId == fullWorthSpaceId)
                .Select(entry => new { entry.Counterparty, entry.Currency })
                .ToListAsync(ct))
            .Select(entry => (entry.Counterparty, entry.Currency))
            .ToHashSet();

        // Accepted auto-detected candidates must not reappear immediately after the owner
        // confirms them. Acceptance persists the provider/currency pair on the contract, so use
        // that stable pair as the suppression key instead of relying on browser-only state.
        var accepted = (await db.Contracts.AsNoTracking()
                .Where(contract => contract.FullWorthSpaceId == fullWorthSpaceId &&
                                   contract.AutoDetected &&
                                   contract.ProviderName != null &&
                                   (contract.AccountId == null || db.AccountOwners.Any(owner =>
                                       owner.AccountId == contract.AccountId.Value &&
                                       owner.UserId == userId)))
                .Select(contract => new { contract.ProviderName, contract.Currency })
                .ToListAsync(ct))
            .Select(contract => (contract.ProviderName!.Trim(), contract.Currency.Trim().ToUpperInvariant()))
            .ToHashSet();

        var deduplicated = result
            .GroupBy(candidate => new
            {
                Provider = CandidateProviderKey(candidate.Counterparty),
                Currency = candidate.Currency.Trim().ToUpperInvariant(),
                candidate.BillingCycle,
                candidate.Interval,
                Amount = Math.Round(candidate.TypicalAmount, 2)
            })
            .Select(group => group
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenByDescending(candidate => candidate.Samples)
                .First());

        return deduplicated
            .Where(candidate => !dismissed.Contains((candidate.Counterparty, candidate.Currency)))
            .Where(candidate => !accepted.Contains((candidate.Counterparty.Trim(), candidate.Currency.Trim().ToUpperInvariant())))
            .OrderByDescending(x => x.Confidence).ThenBy(x => x.NextDueDate).ToList();

    }

    private static string CandidateProviderKey(string value)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var legalSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AG", "GMBH", "KG", "OHG", "SE", "SA", "SAS", "BV", "NV", "INC", "LTD", "LLC", "PLC", "AB"
        };
        while (tokens.Count > 1 && legalSuffixes.Contains(tokens[^1])) tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }

    public async Task<ContractMutationOutcome> AcceptForUserAsync(Guid userId, Guid fullWorthSpaceId, ContractCandidate candidate, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return new(ContractMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(ContractMutationResult.Forbidden);

        if (string.IsNullOrWhiteSpace(candidate.Counterparty) || string.IsNullOrWhiteSpace(candidate.Currency))
            return new(ContractMutationResult.Invalid, Error: "Counterparty and currency are required.");
        var currency = candidate.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            return new(ContractMutationResult.Invalid, Error: "Currency must be a three-letter code.");

        if (candidate.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(category => category.Id == candidate.CategoryId.Value && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return new(ContractMutationResult.NotFound);

        if (candidate.AccountId.HasValue)
        {
            var ownership = await db.Accounts.AsNoTracking()
                .Where(account => account.Id == candidate.AccountId.Value && account.FullWorthSpaceId == fullWorthSpaceId)
                .Join(db.AccountOwners.AsNoTracking().Where(owner => owner.UserId == userId),
                    account => account.Id,
                    owner => owner.AccountId,
                    (account, owner) => owner.OwnershipType)
                .SingleOrDefaultAsync(ct);

            if (ownership == AccountOwnershipTypes.Viewer) return new(ContractMutationResult.Forbidden);
            if (ownership != AccountOwnershipTypes.Owner) return new(ContractMutationResult.NotFound);
        }

        var existing = await db.Contracts
            .Where(contract => contract.FullWorthSpaceId == fullWorthSpaceId &&
                               contract.AutoDetected &&
                               contract.ProviderName == candidate.Counterparty &&
                               contract.Currency == currency)
            .Where(contract => contract.AccountId == null || db.AccountOwners.Any(owner =>
                owner.AccountId == contract.AccountId.Value &&
                owner.UserId == userId &&
                owner.OwnershipType == AccountOwnershipTypes.Owner))
            .OrderBy(contract => contract.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            existing = new RecurringContract
            {
                FullWorthSpaceId = fullWorthSpaceId,
                AutoDetected = true
            };
            db.Contracts.Add(existing);
        }

        existing.Name = candidate.Counterparty.Trim();
        existing.ProviderName = candidate.Counterparty.Trim();
        existing.Kind = "contract";
        existing.CategoryId = candidate.CategoryId;
        existing.AccountId = candidate.AccountId;
        existing.Amount = candidate.TypicalAmount;
        existing.Currency = currency;
        existing.BillingCycle = candidate.BillingCycle.Trim().ToLowerInvariant();
        existing.Interval = Math.Max(1, candidate.Interval);
        existing.NextDueDate = candidate.NextDueDate;
        existing.IsActive = true;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await intelligenceFeedback.RecordContractDecisionAsync(
            fullWorthSpaceId,
            userId,
            candidate.Counterparty,
            currency,
            accepted: true,
            contractId: existing.Id,
            billingCycle: existing.BillingCycle,
            interval: existing.Interval,
            ct);

        var view = await contracts.GetForUserAsync(userId, fullWorthSpaceId, existing.Id, ct);
        return view is null
            ? new(ContractMutationResult.NotFound)
            : new(ContractMutationResult.Success, view);
    }

    private static (string Cycle, int Interval)? DetectCycle(int days) => days switch
    {
        >= 5 and <= 9 => ("weekly", 1),
        >= 12 and <= 16 => ("weekly", 2),
        >= 25 and <= 36 => ("monthly", 1),
        >= 55 and <= 70 => ("monthly", 2),
        >= 80 and <= 100 => ("quarterly", 1),
        >= 170 and <= 195 => ("monthly", 6),
        >= 340 and <= 385 => ("yearly", 1),
        _ => null
    };

    private static DateOnly AddCycle(DateOnly date, string cycle, int interval) => ContractCycle.Next(date, cycle, interval);
}

public static class ContractDetectionEndpoints
{
    public static IEndpointRouteBuilder MapContractDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/detection").WithTags("Contracts");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractDetectionService service, CancellationToken ct) =>
        {
            var candidates = await service.DetectForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        });

        group.MapPost("/accept", async (Guid fullWorthSpaceId, ContractCandidate candidate, CurrentUserContext currentUser, ContractDetectionService service, CancellationToken ct) =>
        {
            var outcome = await service.AcceptForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, candidate, ct);
            return outcome.Result switch
            {
                ContractMutationResult.Success => Results.Ok(outcome.Contract),
                ContractMutationResult.NotFound => Results.NotFound(),
                ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                ContractMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid contract candidate." }),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        return app;
    }
}
