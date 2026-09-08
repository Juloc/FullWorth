using System.Text.Json;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Contracts.PriceChanges;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Intelligence.Signals;

public sealed class FinancialDomainSignalDetectionService(
    TransferDetectionService transfers,
    ContractDetectionService contracts,
    ContractContinuityDetectionService continuity,
    PriceChangeStore priceChanges,
    IOptions<PriceChangeDetectionOptions> priceChangeOptions,
    FinancialSignalStore store)
{
    private const string TransferSource = "detector:transfer-candidate";
    private const string RecurringContractSource = "detector:contract-recurring-candidate";
    private const string ContractContinuitySource = "detector:contract-continuity";
    private const string ContractPriceChangeSource = "detector:contract-price-change";

    public async Task<int> DetectAndPersistAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var total = 0;

        var transferSignals = await DetectTransfersAsync(userId, fullWorthSpaceId, now, ct);
        total += await PersistSourceAsync(userId, fullWorthSpaceId, TransferSource, transferSignals, now, ct);

        var recurringSignals = await DetectRecurringContractsAsync(userId, fullWorthSpaceId, now, ct);
        total += await PersistSourceAsync(userId, fullWorthSpaceId, RecurringContractSource, recurringSignals, now, ct);

        var continuitySignals = await DetectContractContinuityAsync(userId, fullWorthSpaceId, now, ct);
        total += await PersistSourceAsync(userId, fullWorthSpaceId, ContractContinuitySource, continuitySignals, now, ct);

        var priceSignals = await DetectPriceChangesAsync(userId, fullWorthSpaceId, now, ct);
        total += await PersistSourceAsync(userId, fullWorthSpaceId, ContractPriceChangeSource, priceSignals, now, ct);

        return total;
    }

    private async Task<IReadOnlyList<DetectedFinancialSignal>> DetectTransfersAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var outcome = await transfers.CandidatesForUserAsync(userId, fullWorthSpaceId, ct);
        if (outcome.Result != TransferDetectionResult.Success || outcome.Pairs is null) return [];

        return outcome.Pairs
            .Select(pair =>
            {
                var first = pair.First.Id.CompareTo(pair.Second.Id) <= 0 ? pair.First : pair.Second;
                var second = first.Id == pair.First.Id ? pair.Second : pair.First;
                var confidence = pair.Confidence switch
                {
                    "high" => .98m,
                    "medium" => .85m,
                    _ => .70m
                };
                return Create(
                    userId,
                    fullWorthSpaceId,
                    "transfer-candidate",
                    "transaction-pair",
                    $"{first.Id:N}:{second.Id:N}",
                    $"transfer-candidate:{first.Id:N}:{second.Id:N}",
                    TransferSource,
                    FinancialSignalSeverities.Info,
                    confidence,
                    Math.Abs(first.Amount),
                    first.Currency,
                    "insights.transfer.candidate",
                    new
                    {
                        first = new
                        {
                            transactionId = first.Id,
                            first.AccountId,
                            first.Account,
                            first.Amount,
                            first.Currency,
                            first.BookingDate
                        },
                        second = new
                        {
                            transactionId = second.Id,
                            second.AccountId,
                            second.Account,
                            second.Amount,
                            second.Currency,
                            second.BookingDate
                        },
                        pair.Confidence,
                        reasons = pair.Reasons ?? []
                    },
                    now,
                    now.AddDays(14));
            })
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.ImpactAmount)
            .Take(20)
            .ToList();
    }

    private async Task<IReadOnlyList<DetectedFinancialSignal>> DetectRecurringContractsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var candidates = await contracts.DetectForUserAsync(userId, fullWorthSpaceId, ct);
        if (candidates is null) return [];

        return candidates
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.NextDueDate)
            .Take(20)
            .Select(candidate =>
            {
                var providerKey = SpendingShiftSignalDetector.StableKey(ContractIdentity.Normalize(candidate.Counterparty));
                var amountKey = Math.Round(candidate.TypicalAmount, 2, MidpointRounding.AwayFromZero)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Create(
                    userId,
                    fullWorthSpaceId,
                    "contract-recurring-candidate",
                    "merchant",
                    providerKey,
                    $"contract-recurring:{providerKey}:{candidate.Currency.Trim().ToUpperInvariant()}:{candidate.BillingCycle}:{candidate.Interval}:{amountKey}",
                    RecurringContractSource,
                    FinancialSignalSeverities.Info,
                    candidate.Confidence,
                    candidate.TypicalAmount,
                    candidate.Currency,
                    "insights.contract.recurringCandidate",
                    new
                    {
                        candidate.Counterparty,
                        candidate.TypicalAmount,
                        candidate.Currency,
                        candidate.BillingCycle,
                        candidate.Interval,
                        candidate.LastPaymentDate,
                        candidate.NextDueDate,
                        candidate.AccountId,
                        candidate.CategoryId,
                        candidate.Samples,
                        candidate.AmountVariation,
                        candidate.Confidence
                    },
                    now,
                    now.AddDays(45));
            })
            .ToList();
    }

    private async Task<IReadOnlyList<DetectedFinancialSignal>> DetectContractContinuityAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var candidates = await continuity.DetectForUserAsync(userId, fullWorthSpaceId, ct);
        return candidates.Select(candidate =>
        {
            var first = candidate.OlderContractId.CompareTo(candidate.NewerContractId) <= 0
                ? candidate.OlderContractId
                : candidate.NewerContractId;
            var second = first == candidate.OlderContractId
                ? candidate.NewerContractId
                : candidate.OlderContractId;

            return Create(
                userId,
                fullWorthSpaceId,
                "contract-continuity",
                "contract-pair",
                $"{first:N}:{second:N}",
                $"contract-continuity:{first:N}:{second:N}",
                ContractContinuitySource,
                FinancialSignalSeverities.Attention,
                candidate.Confidence,
                null,
                candidate.Currency,
                "insights.contract.possibleDuplicate",
                new
                {
                    candidate.OlderContractId,
                    candidate.NewerContractId,
                    candidate.Provider,
                    candidate.OlderAccountId,
                    candidate.NewerAccountId,
                    candidate.OlderLastPayment,
                    candidate.NewerFirstPayment,
                    candidate.OlderAmount,
                    candidate.NewerAmount,
                    candidate.Currency,
                    candidate.BillingCycle,
                    candidate.Interval,
                    candidate.Confidence
                },
                now,
                now.AddDays(60));
        }).ToList();
    }

    private async Task<IReadOnlyList<DetectedFinancialSignal>> DetectPriceChangesAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var candidates = await priceChanges.PreviewForOwnerAsync(
            userId,
            fullWorthSpaceId,
            priceChangeOptions.Value,
            ct);

        return candidates.Select(candidate =>
        {
            var delta = candidate.NewAmount - candidate.OldAmount;
            var increased = delta > 0m;
            var severity = increased &&
                           (Math.Abs(candidate.PercentChange) >= 10m || Math.Abs(delta) >= 20m)
                ? FinancialSignalSeverities.Attention
                : FinancialSignalSeverities.Info;

            return Create(
                userId,
                fullWorthSpaceId,
                "contract-price-change",
                "contract",
                candidate.ContractId.ToString("N"),
                $"contract-price-change:{candidate.ContractId:N}",
                ContractPriceChangeSource,
                severity,
                1m,
                Math.Abs(delta),
                candidate.Currency,
                increased ? "insights.contract.priceIncreased" : "insights.contract.priceDecreased",
                new
                {
                    candidate.ContractId,
                    candidate.ContractName,
                    candidate.OldAmount,
                    candidate.NewAmount,
                    candidate.PercentChange,
                    candidate.Currency,
                    candidate.EvidenceTransactionId,
                    candidate.EvidenceTransactionDate,
                    candidate.AutoDetected
                },
                now,
                now.AddDays(45));
        }).ToList();
    }

    private async Task<int> PersistSourceAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string source,
        IReadOnlyList<DetectedFinancialSignal> signals,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var deduped = signals
            .Where(signal => string.Equals(signal.Source, source, StringComparison.Ordinal))
            .GroupBy(signal => signal.SemanticKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(signal => signal.RankScore).First())
            .ToList();

        foreach (var signal in deduped)
            await store.UpsertAsync(signal, ct);

        await store.ResolveMissingBySourceAsync(
            userId,
            fullWorthSpaceId,
            source,
            deduped.Select(signal => signal.SemanticKey).ToHashSet(StringComparer.Ordinal),
            now,
            ct);

        return deduped.Count;
    }

    private static DetectedFinancialSignal Create(
        Guid userId,
        Guid fullWorthSpaceId,
        string type,
        string subjectType,
        string subjectId,
        string semanticKey,
        string source,
        string severity,
        decimal confidence,
        decimal? impactAmount,
        string? impactCurrency,
        string titleKey,
        object evidence,
        DateTimeOffset now,
        DateTimeOffset? validUntil)
    {
        var evidenceJson = JsonSerializer.Serialize(evidence);
        var payloadJson = JsonSerializer.Serialize(new { subjectType, subjectId });
        return new DetectedFinancialSignal(
            fullWorthSpaceId,
            userId,
            type,
            subjectType,
            subjectId,
            semanticKey,
            source,
            severity,
            confidence,
            impactAmount,
            impactCurrency,
            titleKey,
            payloadJson,
            evidenceJson,
            FinancialSignalRanker.Score(severity, confidence, impactAmount),
            now,
            validUntil);
    }
}
