using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Contracts.PriceChanges;

public sealed class PriceChangeSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContractId { get; set; }
    public decimal OldAmount { get; set; }
    public decimal NewAmount { get; set; }
    public decimal PercentChange { get; set; }
    public DateOnly DetectedOn { get; set; }
    public Guid EvidenceTransactionId { get; set; }
    public string Status { get; set; } = PriceChangeSuggestionStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public RecurringContract Contract { get; set; } = null!;
    public FullWorth.Backend.Modules.Transactions.FinanceTransaction EvidenceTransaction { get; set; } = null!;
}

public static class PriceChangeSuggestionStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Ignored = "ignored";
}

public enum PriceChangeAutoRefreshPolicy
{
    Disabled,
    AutoDetectedContracts
}

public sealed class PriceChangeDetectionOptions
{
    public const string SectionName = "PriceChanges";

    public decimal MinimumPercentChange { get; set; } = 5m;
    public decimal? MinimumAbsoluteChange { get; set; }
    public PriceChangeAutoRefreshPolicy AutoRefreshPolicy { get; set; } = PriceChangeAutoRefreshPolicy.AutoDetectedContracts;
}

public sealed record PriceChangeSuggestionView(
    Guid Id,
    Guid ContractId,
    decimal OldAmount,
    decimal NewAmount,
    decimal PercentChange,
    DateOnly DetectedOn,
    Guid EvidenceTransactionId,
    DateOnly EvidenceTransactionDate,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PriceChangeDetectionOutcome(
    PriceChangeMutationResult Result,
    IReadOnlyList<PriceChangeSuggestionView>? Suggestions = null,
    int AutoRefreshedContracts = 0);

public enum PriceChangeMutationResult
{
    Success,
    NotFound,
    Forbidden
}

public sealed record PriceChangeMutationOutcome(PriceChangeMutationResult Result, PriceChangeSuggestionView? Suggestion = null);
public sealed record PriceChangeDetectionRequest(DateOnly DetectedOn);

public sealed class PriceChangeStore(FullWorthDbContext db)
{
    public async Task<List<PriceChangeSuggestionView>?> ListForOwnerAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsOwnerAsync(userId, fullWorthSpaceId, ct)) return null;
        return await Project(OwnerScopedSuggestions(userId, fullWorthSpaceId)
            .OrderByDescending(suggestion => suggestion.DetectedOn)
            .ThenBy(suggestion => suggestion.Id)).ToListAsync(ct);
    }

    public async Task<PriceChangeDetectionOutcome> DetectAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        PriceChangeDetectionOptions options,
        DateOnly detectedOn,
        CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(PriceChangeMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(PriceChangeMutationResult.Forbidden);

        ValidateOptions(options);
        var contracts = await OwnerScopedContracts(userId, fullWorthSpaceId)
            .Where(contract => contract.IsActive && contract.AccountId != null)
            .OrderBy(contract => contract.Id)
            .ToListAsync(ct);
        var contractIds = contracts.Select(contract => contract.Id).ToHashSet();
        if (contractIds.Count == 0) return new(PriceChangeMutationResult.Success, []);

        var accountIds = contracts.Select(contract => contract.AccountId!.Value).Distinct().ToArray();
        var transactions = await db.Transactions.AsNoTracking()
            .Where(transaction => accountIds.Contains(transaction.AccountId) &&
                                  transaction.Amount < 0m &&
                                  !transaction.IsIgnored &&
                                  !transaction.IsTransfer &&
                                  transaction.BookingDate != null)
            .Select(transaction => new PriceChangeEvidence(
                transaction.Id,
                transaction.AccountId,
                transaction.Currency,
                transaction.BookingDate!.Value,
                transaction.Amount))
            .ToListAsync(ct);

        var existingPending = await db.PriceChangeSuggestions
            .Where(suggestion => suggestion.Status == PriceChangeSuggestionStatuses.Pending && contractIds.Contains(suggestion.ContractId))
            .ToDictionaryAsync(suggestion => suggestion.ContractId, ct);
        var suggestions = new List<PriceChangeSuggestionView>();
        var autoRefreshed = 0;

        foreach (var contract in contracts)
        {
            var candidate = FindCandidate(contract, transactions);
            if (candidate is null || !MeetsThreshold(candidate, options)) continue;

            if (contract.AutoDetected && options.AutoRefreshPolicy == PriceChangeAutoRefreshPolicy.AutoDetectedContracts)
            {
                contract.Amount = candidate.NewAmount;
                contract.UpdatedAt = DateTimeOffset.UtcNow;
                autoRefreshed++;
                continue;
            }

            var suggestion = existingPending.GetValueOrDefault(contract.Id);
            if (suggestion is null)
            {
                suggestion = new PriceChangeSuggestion { ContractId = contract.Id };
                db.PriceChangeSuggestions.Add(suggestion);
            }

            suggestion.OldAmount = candidate.OldAmount;
            suggestion.NewAmount = candidate.NewAmount;
            suggestion.PercentChange = candidate.PercentChange;
            suggestion.DetectedOn = detectedOn;
            suggestion.EvidenceTransactionId = candidate.EvidenceTransactionId;
            suggestion.Status = PriceChangeSuggestionStatuses.Pending;
            suggestion.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            suggestions.Add(new(
                suggestion.Id, suggestion.ContractId, suggestion.OldAmount, suggestion.NewAmount,
                suggestion.PercentChange, suggestion.DetectedOn, suggestion.EvidenceTransactionId,
                candidate.EvidenceDate, suggestion.Status, suggestion.CreatedAt, suggestion.UpdatedAt));
        }

        if (autoRefreshed > 0) await db.SaveChangesAsync(ct);
        return new(PriceChangeMutationResult.Success, suggestions, autoRefreshed);
    }

    public Task<PriceChangeMutationOutcome> ConfirmPriceChangeAsync(Guid userId, Guid fullWorthSpaceId, Guid suggestionId, CancellationToken ct) =>
        ResolvePriceChangeAsync(userId, fullWorthSpaceId, suggestionId, PriceChangeSuggestionStatuses.Confirmed, ct);

    public Task<PriceChangeMutationOutcome> IgnorePriceChangeAsync(Guid userId, Guid fullWorthSpaceId, Guid suggestionId, CancellationToken ct) =>
        ResolvePriceChangeAsync(userId, fullWorthSpaceId, suggestionId, PriceChangeSuggestionStatuses.Ignored, ct);

    private async Task<PriceChangeMutationOutcome> ResolvePriceChangeAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid suggestionId,
        string status,
        CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(PriceChangeMutationResult.NotFound);

        var suggestion = await db.PriceChangeSuggestions
            .Include(item => item.Contract)
            .Include(item => item.EvidenceTransaction)
            .SingleOrDefaultAsync(item =>
                item.Id == suggestionId &&
                item.Contract.FullWorthSpaceId == fullWorthSpaceId &&
                item.Contract.MergedIntoContractId == null, ct);
        if (suggestion is null) return new(PriceChangeMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(PriceChangeMutationResult.Forbidden);
        if (!await OwnsContractAccountAsync(userId, suggestion.Contract, ct)) return new(PriceChangeMutationResult.NotFound);

        if (status == PriceChangeSuggestionStatuses.Confirmed)
        {
            suggestion.Contract.Amount = suggestion.NewAmount;
            suggestion.Contract.UpdatedAt = DateTimeOffset.UtcNow;
        }
        suggestion.Status = status;
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new(PriceChangeMutationResult.Success, ToView(suggestion, suggestion.EvidenceTransaction.BookingDate!.Value));
    }

    private IQueryable<RecurringContract> OwnerScopedContracts(Guid userId, Guid fullWorthSpaceId) =>
        db.Contracts.Where(contract =>
            contract.FullWorthSpaceId == fullWorthSpaceId &&
            contract.MergedIntoContractId == null &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner) &&
            contract.AccountId != null &&
            db.AccountOwners.Any(owner => owner.AccountId == contract.AccountId.Value && owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner));

    private IQueryable<PriceChangeSuggestion> OwnerScopedSuggestions(Guid userId, Guid fullWorthSpaceId) =>
        db.PriceChangeSuggestions.AsNoTracking().Where(suggestion =>
            suggestion.Contract.FullWorthSpaceId == fullWorthSpaceId &&
            suggestion.Contract.MergedIntoContractId == null &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner) &&
            suggestion.Contract.AccountId != null &&
            db.AccountOwners.Any(owner => owner.AccountId == suggestion.Contract.AccountId.Value && owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner));

    private IQueryable<PriceChangeSuggestionView> Project(IQueryable<PriceChangeSuggestion> suggestions) =>
        suggestions.Select(suggestion => new PriceChangeSuggestionView(
            suggestion.Id,
            suggestion.ContractId,
            suggestion.OldAmount,
            suggestion.NewAmount,
            suggestion.PercentChange,
            suggestion.DetectedOn,
            suggestion.EvidenceTransactionId,
            suggestion.EvidenceTransaction.BookingDate!.Value,
            suggestion.Status,
            suggestion.CreatedAt,
            suggestion.UpdatedAt));

    private Task<string?> GetSpaceRoleAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);

    private Task<bool> IsOwnerAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner, ct);

    private Task<bool> OwnsContractAccountAsync(Guid userId, RecurringContract contract, CancellationToken ct) =>
        !contract.AccountId.HasValue
            ? Task.FromResult(false)
            : db.AccountOwners.AsNoTracking().AnyAsync(owner =>
                owner.AccountId == contract.AccountId.Value && owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner, ct);

    private static PriceChangeCandidate? FindCandidate(RecurringContract contract, IReadOnlyList<PriceChangeEvidence> transactions)
    {
        if (!contract.AccountId.HasValue) return null;

        var groups = transactions
            .Where(transaction => transaction.AccountId == contract.AccountId.Value && transaction.Currency == contract.Currency)
            .Select(transaction => transaction with { Amount = Round(Math.Abs(transaction.Amount)) })
            .GroupBy(transaction => transaction.Amount)
            .Select(group => new
            {
                Amount = group.Key,
                Evidence = group.OrderByDescending(item => item.BookingDate).ThenByDescending(item => item.Id).First()
            })
            .OrderByDescending(group => group.Evidence.BookingDate)
            .ThenByDescending(group => group.Evidence.Id)
            .ToList();
        if (groups.Count == 0) return null;

        var oldAmount = Round(Math.Abs(contract.Amount));
        var newest = groups[0];
        if (newest.Amount == oldAmount) return null;

        var percent = oldAmount == 0m
            ? 0m
            : Round((newest.Amount - oldAmount) / oldAmount * 100m);
        return new(oldAmount, newest.Amount, percent, newest.Evidence.Id, newest.Evidence.BookingDate);
    }

    private static bool MeetsThreshold(PriceChangeCandidate candidate, PriceChangeDetectionOptions options) =>
        Math.Abs(candidate.PercentChange) >= options.MinimumPercentChange ||
        (options.MinimumAbsoluteChange is { } absolute && Math.Abs(candidate.NewAmount - candidate.OldAmount) >= absolute);

    private static void ValidateOptions(PriceChangeDetectionOptions options)
    {
        if (options.MinimumPercentChange < 0m) throw new ArgumentOutOfRangeException(nameof(options.MinimumPercentChange));
        if (options.MinimumAbsoluteChange < 0m) throw new ArgumentOutOfRangeException(nameof(options.MinimumAbsoluteChange));
    }

    private static PriceChangeSuggestionView ToView(PriceChangeSuggestion suggestion, DateOnly evidenceDate) => new(
        suggestion.Id, suggestion.ContractId, suggestion.OldAmount, suggestion.NewAmount, suggestion.PercentChange,
        suggestion.DetectedOn, suggestion.EvidenceTransactionId, evidenceDate, suggestion.Status,
        suggestion.CreatedAt, suggestion.UpdatedAt);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed record PriceChangeEvidence(Guid Id, Guid AccountId, string Currency, DateOnly BookingDate, decimal Amount);
    private sealed record PriceChangeCandidate(decimal OldAmount, decimal NewAmount, decimal PercentChange, Guid EvidenceTransactionId, DateOnly EvidenceDate);
}

public static class PriceChangeEndpoints
{
    public static IEndpointRouteBuilder MapPriceChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/price-changes").WithTags("Contract price changes");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, PriceChangeStore store, CancellationToken ct) =>
        {
            var suggestions = await store.ListForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return suggestions is null ? Results.NotFound() : Results.Ok(suggestions);
        });

        group.MapPost("/detect", async (Guid fullWorthSpaceId, PriceChangeDetectionRequest request, CurrentUserContext currentUser, PriceChangeStore store, IOptions<PriceChangeDetectionOptions> options, CancellationToken ct) =>
            ToResult(await store.DetectAsync(currentUser.RequireUserId(), fullWorthSpaceId, options.Value, request.DetectedOn, ct)));

        group.MapPost("/{id:guid}/confirm", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, PriceChangeStore store, CancellationToken ct) =>
            ToResult(await store.ConfirmPriceChangeAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        group.MapPost("/{id:guid}/ignore", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, PriceChangeStore store, CancellationToken ct) =>
            ToResult(await store.IgnorePriceChangeAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(PriceChangeDetectionOutcome outcome) => outcome.Result switch
    {
        PriceChangeMutationResult.Success => Results.Ok(new { suggestions = outcome.Suggestions ?? [], autoRefreshedContracts = outcome.AutoRefreshedContracts }),
        PriceChangeMutationResult.NotFound => Results.NotFound(),
        PriceChangeMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(PriceChangeMutationOutcome outcome) => outcome.Result switch
    {
        PriceChangeMutationResult.Success => Results.Ok(outcome.Suggestion),
        PriceChangeMutationResult.NotFound => Results.NotFound(),
        PriceChangeMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
