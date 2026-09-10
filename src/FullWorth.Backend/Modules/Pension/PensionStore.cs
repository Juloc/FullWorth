using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The bAV area's one write and read path. Authorization follows the same shape as the rest of the
/// space-scoped modules (see <c>AssetValuationStore</c> and <c>SetManualBalanceAsync</c>): reads need
/// space membership, writes additionally need the owner role, and the ordering is
/// not-found → forbidden → conflict/invalid so a non-member never learns whether a contract exists.
///
/// Three rules from docs/PENSION.md live here rather than only in the database, so both the Postgres
/// and the SQLite test path behave identically and a caller gets a 400/409 instead of a 500:
///
/// 1. the same policy number at the same provider is the same contract — a create for it is a conflict
///    that names the existing contract, so the caller adds a snapshot;
/// 2. history is added to, never rewritten — a snapshot for a date that already has one is a conflict;
/// 3. a tax or social-insurance effect needs the source that stated it, and a projection needs its
///    basis and return assumption, or neither can be told apart from a fact.
///
/// Nothing here writes a policy number, a document's contents or a person's name to a log line.
/// </summary>
public sealed class PensionStore(
    FullWorthDbContext db,
    FieldCipher cipher,
    CurrencyConverter converter,
    AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);

    // ---- authorization ----

    private Task<bool> IsMemberAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == spaceId && member.UserId == userId, ct);

    private Task<bool> IsOwnerAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == spaceId
                                && member.UserId == userId
                                && member.Role == FullWorthSpaceRoles.Owner, ct);

    private IQueryable<BavContract> Visible(Guid userId, Guid spaceId) =>
        db.BavContracts.AsNoTracking().Where(contract =>
            contract.FullWorthSpaceId == spaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == spaceId && member.UserId == userId));

    // ---- reads ----

    public async Task<IReadOnlyList<BavContractView>?> ListAsync(Guid userId, Guid spaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;

        var contracts = await Visible(userId, spaceId)
            .OrderBy(contract => contract.ProviderName)
            .ThenBy(contract => contract.CreatedAt)
            .ToListAsync(ct);
        if (contracts.Count == 0) return Array.Empty<BavContractView>();

        var ids = contracts.Select(contract => contract.Id).ToList();
        var snapshots = await db.BavSnapshots.AsNoTracking()
            .Where(snapshot => ids.Contains(snapshot.BavContractId))
            .ToListAsync(ct);
        var contributions = await db.BavContributions.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId))
            .ToListAsync(ct);
        var netWorthFlags = await NetWorthFlagsAsync(contracts, ct);

        return contracts
            .Select(contract => Project(contract, snapshots, contributions, netWorthFlags))
            .ToList();
    }

    public async Task<BavContractDetailView?> GetAsync(Guid userId, Guid spaceId, Guid contractId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;

        var contract = await Visible(userId, spaceId).SingleOrDefaultAsync(row => row.Id == contractId, ct);
        if (contract is null) return null;

        var snapshots = await db.BavSnapshots.AsNoTracking()
            .Where(row => row.BavContractId == contractId)
            .OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.CreatedAt)
            .ToListAsync(ct);
        var contributions = await db.BavContributions.AsNoTracking()
            .Where(row => row.BavContractId == contractId)
            .OrderByDescending(row => row.ValidFrom).ThenByDescending(row => row.CreatedAt)
            .ToListAsync(ct);
        var costs = await db.BavCosts.AsNoTracking()
            .Where(row => row.BavContractId == contractId)
            .OrderByDescending(row => row.EffectiveDate).ThenBy(row => row.Kind)
            .ToListAsync(ct);
        var allocations = await db.BavInvestmentAllocations.AsNoTracking()
            .Where(row => row.BavContractId == contractId)
            .OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.WeightPercent)
            .ToListAsync(ct);
        var netWorthFlags = await NetWorthFlagsAsync([contract], ct);

        return new BavContractDetailView(
            Project(contract, snapshots, contributions, netWorthFlags),
            snapshots.Select(ToView).ToList(),
            contributions.Select(ToView).ToList(),
            costs.Select(ToView).ToList(),
            allocations.Select(ToView).ToList());
    }

    public async Task<IReadOnlyList<BavSnapshotView>?> SnapshotsAsync(Guid userId, Guid spaceId, Guid contractId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;
        if (!await Visible(userId, spaceId).AnyAsync(row => row.Id == contractId, ct)) return null;

        var rows = await db.BavSnapshots.AsNoTracking()
            .Where(row => row.BavContractId == contractId)
            .OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    /// <summary>
    /// Existing-contract detection, in the order docs/PENSION.md fixes: policy number, then provider +
    /// tariff + employer, then provider + retirement date. The answer names which rule fired, because a
    /// weaker match has to be confirmed by a person rather than applied silently.
    /// </summary>
    public async Task<BavContractMatchResult?> MatchAsync(
        Guid userId, Guid spaceId, BavContractMatchRequest request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;

        var providerKey = PensionIdentity.ProviderKey(request.ProviderName);
        var lookup = PensionIdentity.PolicyNumberLookup(request.PolicyNumber, cipher);

        if (lookup is not null)
        {
            var byPolicy = await Visible(userId, spaceId)
                .Where(row => row.PolicyNumberLookup == lookup
                              && (providerKey.Length == 0 || row.ProviderKey == providerKey))
                .OrderBy(row => row.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (byPolicy is not null) return await MatchedAsync(byPolicy, "policy_number", ct);
        }

        if (providerKey.Length > 0)
        {
            var tariff = Trim(request.TariffName);
            var employer = Trim(request.EmployerName);
            if (tariff is not null || employer is not null)
            {
                var byTariff = await Visible(userId, spaceId)
                    .Where(row => row.ProviderKey == providerKey
                                  && (tariff == null || row.TariffName == tariff)
                                  && (employer == null || row.EmployerName == employer))
                    .OrderBy(row => row.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (byTariff is not null) return await MatchedAsync(byTariff, "provider_tariff_employer", ct);
            }

            if (request.RetirementDate.HasValue)
            {
                var byRetirement = await Visible(userId, spaceId)
                    .Where(row => row.ProviderKey == providerKey && row.RetirementDate == request.RetirementDate)
                    .OrderBy(row => row.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (byRetirement is not null) return await MatchedAsync(byRetirement, "provider_retirement_date", ct);
            }
        }

        return new BavContractMatchResult(false, null, null, null);
    }

    private async Task<BavContractMatchResult> MatchedAsync(BavContract contract, string rule, CancellationToken ct)
    {
        var snapshots = await db.BavSnapshots.AsNoTracking().Where(row => row.BavContractId == contract.Id).ToListAsync(ct);
        var contributions = await db.BavContributions.AsNoTracking().Where(row => row.BavContractId == contract.Id).ToListAsync(ct);
        var flags = await NetWorthFlagsAsync([contract], ct);
        return new BavContractMatchResult(true, contract.Id, rule, Project(contract, snapshots, contributions, flags));
    }

    /// <summary>
    /// The Übersicht totals. Balances are converted to the space's base currency at the rate of the
    /// snapshot's own date, and a currency without a rate is reported as missing instead of being added
    /// at 1:1 — the original amount stays untouched and is listed separately.
    /// </summary>
    public async Task<BavOverviewView?> OverviewAsync(Guid userId, Guid spaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;

        var baseCurrency = await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == spaceId)
            .Select(space => space.BaseCurrency)
            .SingleOrDefaultAsync(ct) ?? "EUR";
        baseCurrency = FxSnapshot.Normalize(baseCurrency);

        var contracts = await Visible(userId, spaceId).ToListAsync(ct);
        var ids = contracts.Select(contract => contract.Id).ToList();
        var current = await db.BavSnapshots.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId) && row.IsCurrent)
            .ToListAsync(ct);
        var contributions = await db.BavContributions.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId))
            .ToListAsync(ct);
        var estimatedCostContracts = await db.BavCosts.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId) && row.IsEstimated)
            .Select(row => row.BavContractId)
            .Distinct()
            .CountAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var oldest = current.Count == 0 ? today : current.Min(row => row.EffectiveDate);
        var rates = await converter.PrepareAsync(baseCurrency, oldest, today, ct);

        var total = 0m;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var unconverted = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var guaranteedAnnuity = 0m;
        var projectedAnnuity = 0m;

        foreach (var snapshot in current)
        {
            if (snapshot.Balance is { } balance)
            {
                // A historical value keeps the rate of its own date; today's rate never rewrites the past.
                var converted = rates.ToBaseOn(balance, snapshot.Currency, snapshot.EffectiveDate);
                if (converted is null)
                {
                    missing.Add(FxSnapshot.Normalize(snapshot.Currency));
                    unconverted[FxSnapshot.Normalize(snapshot.Currency)] =
                        unconverted.GetValueOrDefault(FxSnapshot.Normalize(snapshot.Currency)) + balance;
                }
                else total += converted.Value;
            }

            if (snapshot.GuaranteedMonthlyAnnuity is { } guaranteed)
            {
                var converted = rates.ToBaseOn(guaranteed, snapshot.Currency, snapshot.EffectiveDate);
                if (converted is null) missing.Add(FxSnapshot.Normalize(snapshot.Currency));
                else guaranteedAnnuity += converted.Value;
            }

            if (snapshot.ProjectedMonthlyAnnuity is { } projected)
            {
                var converted = rates.ToBaseOn(projected, snapshot.Currency, snapshot.EffectiveDate);
                if (converted is null) missing.Add(FxSnapshot.Normalize(snapshot.Currency));
                else projectedAnnuity += converted.Value;
            }
        }

        var employee = 0m;
        var employer = 0m;
        foreach (var contract in contracts)
        {
            var running = Running(contributions, contract.Id, today);
            if (running is null) continue;
            var perYear = BavContributionCycles.PaymentsPerYear(running.Cycle);
            if (perYear == 0) continue;
            var monthlyFactor = perYear / 12m;
            var employeeMonthly = running.EmployeeAmount * monthlyFactor;
            var employerMonthly = (running.EmployerAmount + running.EmployerSubsidyAmount) * monthlyFactor;
            var convertedEmployee = rates.ToBaseOn(employeeMonthly, running.Currency, today);
            var convertedEmployer = rates.ToBaseOn(employerMonthly, running.Currency, today);
            if (convertedEmployee is null || convertedEmployer is null)
            {
                missing.Add(FxSnapshot.Normalize(running.Currency));
                continue;
            }
            employee += convertedEmployee.Value;
            employer += convertedEmployer.Value;
        }

        var withSnapshot = current.Select(row => row.BavContractId).ToHashSet();

        return new BavOverviewView(
            contracts.Count,
            contracts.Count(contract => contract.Status == BavContractStatuses.Active),
            contracts.Count(contract => contract.Status == BavContractStatuses.PaidUp),
            Round(total),
            baseCurrency,
            missing.Count == 0,
            missing.ToList(),
            unconverted.Select(pair => new BavCurrencyAmount(pair.Key, Round(pair.Value))).ToList(),
            Round(employee),
            Round(employer),
            Round(guaranteedAnnuity),
            Round(projectedAnnuity),
            contracts.Count(contract => !withSnapshot.Contains(contract.Id)),
            estimatedCostContracts);
    }

    // ---- contract writes ----

    public async Task<BavContractOutcome> CreateAsync(
        Guid userId, Guid spaceId, BavContractWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        var normalized = Normalize(request);
        if (normalized.Error is not null) return new(BavMutationResult.Invalid, Error: normalized.Error);
        var value = normalized.Value!;

        // The identity rule: the same policy number at the same provider IS the same contract. The
        // caller is told which one so an annual statement becomes a snapshot on it, never a second
        // contract. The unique index behind this makes the same true for a concurrent second writer.
        if (value.PolicyNumberLookup is not null)
        {
            var existing = await db.BavContracts.AsNoTracking()
                .Where(row => row.FullWorthSpaceId == spaceId
                              && row.ProviderKey == value.ProviderKey
                              && row.PolicyNumberLookup == value.PolicyNumberLookup)
                .Select(row => row.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != Guid.Empty)
                return new(BavMutationResult.Conflict,
                    Error: "A contract with this policy number already exists at this provider. Add a snapshot to it instead.",
                    ExistingContractId: existing);
        }

        var now = DateTimeOffset.UtcNow;
        var contract = new BavContract
        {
            FullWorthSpaceId = spaceId,
            ProviderName = value.ProviderName,
            ProviderKey = value.ProviderKey,
            TariffName = value.TariffName,
            PolicyNumberEncrypted = value.PolicyNumberEncrypted,
            PolicyNumberLookup = value.PolicyNumberLookup,
            PolicyNumberLast4 = value.PolicyNumberLast4,
            ImplementationRoute = value.ImplementationRoute,
            Status = value.Status,
            EmployerName = value.EmployerName,
            PolicyHolderName = value.PolicyHolderName,
            InsuredPersonName = value.InsuredPersonName,
            StartDate = value.StartDate,
            RetirementDate = value.RetirementDate,
            ContractEndDate = value.ContractEndDate,
            Currency = value.Currency,
            GuaranteeQuotaPercent = value.GuaranteeQuotaPercent,
            GuaranteedAnnuityFactor = value.GuaranteedAnnuityFactor,
            FundSelectionChangeable = value.FundSelectionChangeable,
            Notes = value.Notes,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        // One Asset per contract carries the balance into net worth, so the pension value reaches the
        // user through the existing net-worth path instead of a second one. It starts at zero: the
        // first snapshot is what gives it a value.
        var asset = new Asset
        {
            FullWorthSpaceId = spaceId,
            Name = AssetName(value),
            Kind = AssetKinds.InsurancePension,
            CurrentValue = 0m,
            Currency = value.Currency,
            ValuedAt = null,
            IncludeInNetWorth = request.IncludeInNetWorth,
            Notes = null
        };
        db.Assets.Add(asset);
        contract.AssetId = asset.Id;
        db.BavContracts.Add(contract);

        audit.Record(spaceId, userId, "pension.contract.created", "BavContract", contract.Id);
        await db.SaveChangesAsync(ct);

        return new(BavMutationResult.Success, await ViewAsync(contract, ct));
    }

    public async Task<BavContractOutcome> UpdateAsync(
        Guid userId, Guid spaceId, Guid contractId, BavContractWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var contract = await db.BavContracts.SingleOrDefaultAsync(
            row => row.Id == contractId && row.FullWorthSpaceId == spaceId, ct);
        if (contract is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        var normalized = Normalize(request);
        if (normalized.Error is not null) return new(BavMutationResult.Invalid, Error: normalized.Error);
        var value = normalized.Value!;

        if (value.PolicyNumberLookup is not null)
        {
            var clash = await db.BavContracts.AsNoTracking()
                .Where(row => row.FullWorthSpaceId == spaceId
                              && row.Id != contractId
                              && row.ProviderKey == value.ProviderKey
                              && row.PolicyNumberLookup == value.PolicyNumberLookup)
                .Select(row => row.Id)
                .FirstOrDefaultAsync(ct);
            if (clash != Guid.Empty)
                return new(BavMutationResult.Conflict,
                    Error: "Another contract already carries this policy number at this provider.",
                    ExistingContractId: clash);
        }

        // Changing the contract's currency would make the stored snapshots mean something else, since a
        // snapshot keeps its own currency and is never converted in place.
        if (value.Currency != contract.Currency
            && await db.BavSnapshots.AsNoTracking().AnyAsync(row => row.BavContractId == contractId, ct))
            return new(BavMutationResult.Invalid,
                Error: $"The contract currency cannot change from {contract.Currency} to {value.Currency} once snapshots exist.");

        contract.ProviderName = value.ProviderName;
        contract.ProviderKey = value.ProviderKey;
        contract.TariffName = value.TariffName;
        contract.PolicyNumberEncrypted = value.PolicyNumberEncrypted;
        contract.PolicyNumberLookup = value.PolicyNumberLookup;
        contract.PolicyNumberLast4 = value.PolicyNumberLast4;
        contract.ImplementationRoute = value.ImplementationRoute;
        contract.Status = value.Status;
        contract.EmployerName = value.EmployerName;
        contract.PolicyHolderName = value.PolicyHolderName;
        contract.InsuredPersonName = value.InsuredPersonName;
        contract.StartDate = value.StartDate;
        contract.RetirementDate = value.RetirementDate;
        contract.ContractEndDate = value.ContractEndDate;
        contract.Currency = value.Currency;
        contract.GuaranteeQuotaPercent = value.GuaranteeQuotaPercent;
        contract.GuaranteedAnnuityFactor = value.GuaranteedAnnuityFactor;
        contract.FundSelectionChangeable = value.FundSelectionChangeable;
        contract.Notes = value.Notes;
        contract.UpdatedAt = DateTimeOffset.UtcNow;

        if (contract.AssetId is { } assetId)
        {
            var asset = await db.Assets.SingleOrDefaultAsync(row => row.Id == assetId, ct);
            if (asset is not null)
            {
                asset.Name = AssetName(value);
                asset.IncludeInNetWorth = request.IncludeInNetWorth;
                asset.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        audit.Record(spaceId, userId, "pension.contract.updated", "BavContract", contract.Id);
        await db.SaveChangesAsync(ct);
        return new(BavMutationResult.Success, await ViewAsync(contract, ct));
    }

    public async Task<BavMutationResult> DeleteAsync(Guid userId, Guid spaceId, Guid contractId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return BavMutationResult.NotFound;

        var contract = await db.BavContracts.SingleOrDefaultAsync(
            row => row.Id == contractId && row.FullWorthSpaceId == spaceId, ct);
        if (contract is null) return BavMutationResult.NotFound;
        if (!await IsOwnerAsync(userId, spaceId, ct)) return BavMutationResult.Forbidden;

        // The asset exists only to carry this contract's value into net worth, so it goes with it —
        // otherwise net worth keeps counting a contract that is gone.
        var assetId = contract.AssetId;
        db.BavContracts.Remove(contract);
        audit.Record(spaceId, userId, "pension.contract.deleted", "BavContract", contractId);
        await db.SaveChangesAsync(ct);

        if (assetId is { } id)
        {
            var asset = await db.Assets.SingleOrDefaultAsync(row => row.Id == id, ct);
            if (asset is not null && asset.Kind == AssetKinds.InsurancePension)
            {
                db.Assets.Remove(asset);
                await db.SaveChangesAsync(ct);
            }
        }

        return BavMutationResult.Success;
    }

    // ---- snapshot writes ----

    /// <summary>
    /// Adds a dated state. Nothing that already exists is touched: a second statement adds a row, and
    /// a date that already carries a snapshot is a conflict rather than an overwrite. Only the newest
    /// accepted snapshot feeds the asset value, and only its <c>Balance</c> ever does — a projection
    /// never becomes wealth.
    /// </summary>
    public async Task<BavSnapshotOutcome> AddSnapshotAsync(
        Guid userId, Guid spaceId, Guid contractId, BavSnapshotWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var contract = await db.BavContracts.SingleOrDefaultAsync(
            row => row.Id == contractId && row.FullWorthSpaceId == spaceId, ct);
        if (contract is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        var currency = FxSnapshot.Normalize(request.Currency ?? contract.Currency);
        if (currency != contract.Currency)
            return new(BavMutationResult.Invalid,
                Error: $"A snapshot must use the contract currency {contract.Currency}, not {currency}.");

        var validation = ValidateSnapshot(request);
        if (validation is not null) return new(BavMutationResult.Invalid, Error: validation);

        var sha = Trim(request.DocumentSha256)?.ToLowerInvariant();
        if (sha is not null && (sha.Length != 64 || !sha.All(Uri.IsHexDigit)))
            return new(BavMutationResult.Invalid, Error: "DocumentSha256 must be a 64-character hex digest.");

        // Re-reading the same document produces nothing, and a second hand-entered snapshot for a date
        // that already has one is refused instead of duplicating the day.
        var duplicate = await db.BavSnapshots.AsNoTracking()
            .Where(row => row.BavContractId == contractId
                          && row.EffectiveDate == request.EffectiveDate
                          && row.DocumentSha256 == sha)
            .Select(row => row.Id)
            .FirstOrDefaultAsync(ct);
        if (duplicate != Guid.Empty)
            return new(BavMutationResult.Conflict,
                Error: sha is null
                    ? "This contract already has a snapshot for that date."
                    : "This document has already been recorded for that date.",
                ExistingSnapshotId: duplicate);

        if (request.BavDocumentId is { } documentId
            && !await db.BavDocuments.AsNoTracking().AnyAsync(row => row.Id == documentId && row.FullWorthSpaceId == spaceId, ct))
            return new(BavMutationResult.NotFound);

        var snapshot = new BavSnapshot
        {
            FullWorthSpaceId = spaceId,
            BavContractId = contractId,
            EffectiveDate = request.EffectiveDate,
            Currency = currency,
            Balance = request.Balance,
            GuaranteedBalance = request.GuaranteedBalance,
            SurrenderValue = request.SurrenderValue,
            SecurityAssetsAmount = request.SecurityAssetsAmount,
            FundAssetsAmount = request.FundAssetsAmount,
            GuaranteedCapitalAtRetirement = request.GuaranteedCapitalAtRetirement,
            GuaranteedMonthlyAnnuity = request.GuaranteedMonthlyAnnuity,
            ProjectedCapitalAtRetirement = request.ProjectedCapitalAtRetirement,
            ProjectedMonthlyAnnuity = request.ProjectedMonthlyAnnuity,
            ProjectionReturnPercent = request.ProjectionReturnPercent,
            ProjectionBasis = Trim(request.ProjectionBasis),
            Source = SourceOrDefault(request.Source),
            BavDocumentId = request.BavDocumentId,
            DocumentSha256 = sha,
            ExtractionConfidence = request.ExtractionConfidence,
            Note = Trim(request.Note),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // "Current" is the newest date, not the newest insert: entering last year's statement after this
        // year's must not move the contract's value backwards.
        var newest = await db.BavSnapshots
            .Where(row => row.BavContractId == contractId)
            .OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var becomesCurrent = newest is null || request.EffectiveDate >= newest.EffectiveDate;
        if (becomesCurrent)
        {
            foreach (var previous in await db.BavSnapshots
                         .Where(row => row.BavContractId == contractId && row.IsCurrent)
                         .ToListAsync(ct))
                previous.IsCurrent = false;
            snapshot.IsCurrent = true;
        }

        db.BavSnapshots.Add(snapshot);

        if (becomesCurrent && snapshot.Balance is { } balance && contract.AssetId is { } assetId)
        {
            var asset = await db.Assets.SingleOrDefaultAsync(row => row.Id == assetId, ct);
            if (asset is not null)
            {
                // Only a snapshot balance is ever written here, in the snapshot's own currency and as of
                // the snapshot's own date. The asset's valuation history records it through the existing
                // trigger, so there is no second history for pension values.
                asset.CurrentValue = balance;
                asset.Currency = currency;
                asset.ValuedAt = request.EffectiveDate;
                asset.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        contract.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(spaceId, userId, "pension.snapshot.added", "BavSnapshot", snapshot.Id);
        await db.SaveChangesAsync(ct);
        return new(BavMutationResult.Success, ToView(snapshot));
    }

    // ---- contribution writes ----

    public async Task<BavContributionOutcome> AddContributionAsync(
        Guid userId, Guid spaceId, Guid contractId, BavContributionWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var contract = await db.BavContracts.SingleOrDefaultAsync(
            row => row.Id == contractId && row.FullWorthSpaceId == spaceId, ct);
        if (contract is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        var currency = FxSnapshot.Normalize(request.Currency ?? contract.Currency);
        if (request.EmployeeAmount < 0 || request.EmployerSubsidyAmount < 0 || request.EmployerAmount < 0)
            return new(BavMutationResult.Invalid, Error: "A contribution share cannot be negative.");
        if (request.StatedTotalAmount is < 0)
            return new(BavMutationResult.Invalid, Error: "A stated total cannot be negative.");
        if (request.ValidUntil is { } until && until < request.ValidFrom)
            return new(BavMutationResult.Invalid, Error: "The end date cannot precede the start date.");

        var cycle = Trim(request.Cycle) ?? BavContributionCycles.Monthly;
        if (!BavContributionCycles.Allowed.Contains(cycle))
            return new(BavMutationResult.Invalid, Error: $"Unknown contribution cycle '{cycle}'.");

        var endReason = Trim(request.EndReason);
        if (endReason is not null)
        {
            if (!BavContributionEndReasons.Allowed.Contains(endReason))
                return new(BavMutationResult.Invalid, Error: $"Unknown end reason '{endReason}'.");
            if (request.ValidUntil is null)
                return new(BavMutationResult.Invalid, Error: "An end reason needs an end date.");
        }

        // FullWorth does not compute a personal tax or social-insurance effect. One may only be stored
        // together with what stated it, and its own arithmetic is stored as an explicit simulation.
        var hasTaxEffect = request.TaxSavingAmount is not null
                           || request.SocialSecuritySavingAmount is not null
                           || request.NetEffortAmount is not null;
        var taxSource = Trim(request.TaxEffectSource);
        if (hasTaxEffect)
        {
            if (taxSource is null)
                return new(BavMutationResult.Invalid,
                    Error: "A tax or social-insurance effect needs a source: document, payslip, or simulation.");
            if (!BavTaxEffectSources.Allowed.Contains(taxSource))
                return new(BavMutationResult.Invalid, Error: $"Unknown tax effect source '{taxSource}'.");
            if (request.TaxSavingAmount is < 0 || request.SocialSecuritySavingAmount is < 0 || request.NetEffortAmount is < 0)
                return new(BavMutationResult.Invalid, Error: "A tax or social-insurance effect cannot be negative.");
        }
        else if (taxSource is not null)
            return new(BavMutationResult.Invalid, Error: "A tax effect source without an amount says nothing.");

        var source = SourceOrDefault(request.Source);
        if (request.BavDocumentId is { } documentId
            && !await db.BavDocuments.AsNoTracking().AnyAsync(row => row.Id == documentId && row.FullWorthSpaceId == spaceId, ct))
            return new(BavMutationResult.NotFound);

        var contribution = new BavContribution
        {
            FullWorthSpaceId = spaceId,
            BavContractId = contractId,
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            EndReason = endReason,
            Cycle = cycle,
            Currency = currency,
            EmployeeAmount = request.EmployeeAmount,
            EmployerSubsidyAmount = request.EmployerSubsidyAmount,
            EmployerAmount = request.EmployerAmount,
            StatedTotalAmount = request.StatedTotalAmount,
            Source = source,
            TaxSavingAmount = request.TaxSavingAmount,
            SocialSecuritySavingAmount = request.SocialSecuritySavingAmount,
            NetEffortAmount = request.NetEffortAmount,
            TaxEffectSource = hasTaxEffect ? taxSource : null,
            TaxEffectSourceReference = Trim(request.TaxEffectSourceReference),
            BavDocumentId = request.BavDocumentId,
            Note = Trim(request.Note),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.BavContributions.Add(contribution);
        contract.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(spaceId, userId, "pension.contribution.added", "BavContribution", contribution.Id);
        await db.SaveChangesAsync(ct);
        return new(BavMutationResult.Success, ToView(contribution));
    }

    // ---- cost writes ----

    public async Task<BavCostOutcome> AddCostAsync(
        Guid userId, Guid spaceId, Guid contractId, BavCostWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var contract = await db.BavContracts.SingleOrDefaultAsync(
            row => row.Id == contractId && row.FullWorthSpaceId == spaceId, ct);
        if (contract is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        var kind = Trim(request.Kind);
        if (kind is null || !BavCostKinds.Allowed.Contains(kind))
            return new(BavMutationResult.Invalid, Error: $"Unknown cost kind '{request.Kind}'.");
        var basis = Trim(request.Basis);
        if (basis is null || !BavCostBases.Allowed.Contains(basis))
            return new(BavMutationResult.Invalid, Error: $"Unknown cost basis '{request.Basis}'.");
        var timing = Trim(request.Timing) ?? BavCostTimings.Ongoing;
        if (!BavCostTimings.Allowed.Contains(timing))
            return new(BavMutationResult.Invalid, Error: $"Unknown cost timing '{timing}'.");

        // A cost figure has to be a figure of something.
        if (basis == BavCostBases.FixedAmount)
        {
            if (request.Amount is null) return new(BavMutationResult.Invalid, Error: "A fixed cost needs an amount.");
            if (request.Percent is not null) return new(BavMutationResult.Invalid, Error: "A fixed cost carries no percentage.");
            if (request.Amount < 0) return new(BavMutationResult.Invalid, Error: "A cost amount cannot be negative.");
        }
        else
        {
            if (request.Percent is null) return new(BavMutationResult.Invalid, Error: "A percentage cost needs a percentage.");
            if (request.Amount is not null) return new(BavMutationResult.Invalid, Error: "A percentage cost carries no amount.");
            if (request.Percent is < 0 or > 100) return new(BavMutationResult.Invalid, Error: "A cost percentage must be between 0 and 100.");
        }

        // An estimated cost says how it was estimated, so it is never displayed as a contract value.
        var estimateBasis = Trim(request.EstimateBasis);
        if (request.IsEstimated && estimateBasis is null)
            return new(BavMutationResult.Invalid, Error: "An estimated cost has to say what the estimate is based on.");
        if (!request.IsEstimated && estimateBasis is not null)
            return new(BavMutationResult.Invalid, Error: "An estimate basis belongs to an estimated cost.");

        if (request.AppliesUntilDate is { } until && until < request.EffectiveDate)
            return new(BavMutationResult.Invalid, Error: "The end date cannot precede the start date.");

        if (request.BavSnapshotId is { } snapshotId
            && !await db.BavSnapshots.AsNoTracking().AnyAsync(row => row.Id == snapshotId && row.BavContractId == contractId, ct))
            return new(BavMutationResult.NotFound);
        if (request.BavDocumentId is { } documentId
            && !await db.BavDocuments.AsNoTracking().AnyAsync(row => row.Id == documentId && row.FullWorthSpaceId == spaceId, ct))
            return new(BavMutationResult.NotFound);

        var cost = new BavCost
        {
            FullWorthSpaceId = spaceId,
            BavContractId = contractId,
            BavSnapshotId = request.BavSnapshotId,
            EffectiveDate = request.EffectiveDate,
            AppliesUntilDate = request.AppliesUntilDate,
            Kind = kind,
            Basis = basis,
            Amount = request.Amount,
            Currency = FxSnapshot.Normalize(request.Currency ?? contract.Currency),
            Percent = request.Percent,
            Timing = timing,
            IsEstimated = request.IsEstimated,
            EstimateBasis = estimateBasis,
            ContinuesWhenPaidUp = request.ContinuesWhenPaidUp,
            Source = SourceOrDefault(request.Source),
            BavDocumentId = request.BavDocumentId,
            Note = Trim(request.Note),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.BavCosts.Add(cost);
        contract.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(spaceId, userId, "pension.cost.added", "BavCost", cost.Id);
        await db.SaveChangesAsync(ct);
        return new(BavMutationResult.Success, ToView(cost));
    }

    // ---- allocation writes ----

    public async Task<BavAllocationOutcome> AddAllocationAsync(
        Guid userId, Guid spaceId, Guid contractId, BavAllocationWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var contract = await db.BavContracts.SingleOrDefaultAsync(
            row => row.Id == contractId && row.FullWorthSpaceId == spaceId, ct);
        if (contract is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        var fundName = Trim(request.FundName);
        if (fundName is null) return new(BavMutationResult.Invalid, Error: "A fund needs a name.");

        var isin = PensionIdentity.NormalizeIsin(request.Isin);
        if (isin is not null && !PensionIdentity.IsValidIsin(isin))
            return new(BavMutationResult.Invalid, Error: "An ISIN is two letters followed by ten alphanumeric characters.");

        var assetClass = Trim(request.AssetClass) ?? BavAssetClasses.Other;
        if (!BavAssetClasses.Allowed.Contains(assetClass))
            return new(BavMutationResult.Invalid, Error: $"Unknown asset class '{assetClass}'.");
        if (request.WeightPercent is < 0 or > 100)
            return new(BavMutationResult.Invalid, Error: "A weight must be between 0 and 100 percent.");
        if (request.Amount is < 0)
            return new(BavMutationResult.Invalid, Error: "A fund value cannot be negative.");
        if (request.OngoingChargesPercent is < 0 or > 100)
            return new(BavMutationResult.Invalid, Error: "Ongoing charges must be between 0 and 100 percent.");
        if (request.OngoingChargesEstimated && request.OngoingChargesPercent is null)
            return new(BavMutationResult.Invalid, Error: "An estimated charge needs a figure to be an estimate of.");

        if (request.BavSnapshotId is { } snapshotId
            && !await db.BavSnapshots.AsNoTracking().AnyAsync(row => row.Id == snapshotId && row.BavContractId == contractId, ct))
            return new(BavMutationResult.NotFound);

        var allocation = new BavInvestmentAllocation
        {
            FullWorthSpaceId = spaceId,
            BavContractId = contractId,
            BavSnapshotId = request.BavSnapshotId,
            EffectiveDate = request.EffectiveDate,
            FundName = fundName,
            Isin = isin,
            WeightPercent = request.WeightPercent,
            Amount = request.Amount,
            Currency = FxSnapshot.Normalize(request.Currency ?? contract.Currency),
            OngoingChargesPercent = request.OngoingChargesPercent,
            OngoingChargesEstimated = request.OngoingChargesEstimated,
            AssetClass = assetClass,
            Source = SourceOrDefault(request.Source),
            Note = Trim(request.Note),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.BavInvestmentAllocations.Add(allocation);
        contract.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(spaceId, userId, "pension.allocation.added", "BavInvestmentAllocation", allocation.Id);
        await db.SaveChangesAsync(ct);
        return new(BavMutationResult.Success, ToView(allocation));
    }

    // ---- normalisation and projection ----

    private sealed record NormalizedContract(
        string ProviderName,
        string ProviderKey,
        string? TariffName,
        string? PolicyNumberEncrypted,
        string? PolicyNumberLookup,
        string? PolicyNumberLast4,
        string ImplementationRoute,
        string Status,
        string? EmployerName,
        string? PolicyHolderName,
        string? InsuredPersonName,
        DateOnly? StartDate,
        DateOnly? RetirementDate,
        DateOnly? ContractEndDate,
        string Currency,
        decimal? GuaranteeQuotaPercent,
        decimal? GuaranteedAnnuityFactor,
        bool FundSelectionChangeable,
        string? Notes);

    private (NormalizedContract? Value, string? Error) Normalize(BavContractWrite request)
    {
        var providerName = Trim(request.ProviderName);
        if (providerName is null) return (null, "A contract needs a provider.");
        var providerKey = PensionIdentity.ProviderKey(providerName);
        if (providerKey.Length == 0) return (null, "The provider name has to contain letters or digits.");

        var route = Trim(request.ImplementationRoute) ?? BavImplementationRoutes.Other;
        if (!BavImplementationRoutes.Allowed.Contains(route))
            return (null, $"Unknown implementation route '{route}'.");

        var status = Trim(request.Status) ?? BavContractStatuses.Active;
        if (!BavContractStatuses.Allowed.Contains(status))
            return (null, $"Unknown contract status '{status}'.");

        var currency = FxSnapshot.Normalize(request.Currency);
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            return (null, "A currency is a three-letter code.");

        if (request.GuaranteeQuotaPercent is < 0 or > 100)
            return (null, "The guarantee quota must be between 0 and 100 percent.");
        if (request.GuaranteedAnnuityFactor is < 0)
            return (null, "The guaranteed annuity factor cannot be negative.");
        if (request.ContractEndDate is { } end && request.StartDate is { } start && end < start)
            return (null, "The contract end cannot precede its start.");

        var policyNumber = Trim(request.PolicyNumber);
        return (new NormalizedContract(
            providerName,
            providerKey,
            Trim(request.TariffName),
            cipher.Protect(PensionIdentity.NormalizePolicyNumber(policyNumber)),
            PensionIdentity.PolicyNumberLookup(policyNumber, cipher),
            PensionIdentity.PolicyNumberLast4(policyNumber),
            route,
            status,
            Trim(request.EmployerName),
            Trim(request.PolicyHolderName),
            Trim(request.InsuredPersonName),
            request.StartDate,
            request.RetirementDate,
            request.ContractEndDate,
            currency,
            request.GuaranteeQuotaPercent,
            request.GuaranteedAnnuityFactor,
            request.FundSelectionChangeable,
            Trim(request.Notes)), null);
    }

    private static string? ValidateSnapshot(BavSnapshotWrite request)
    {
        foreach (var (label, amount) in new (string, decimal?)[]
                 {
                     ("balance", request.Balance),
                     ("guaranteed balance", request.GuaranteedBalance),
                     ("surrender value", request.SurrenderValue),
                     ("security assets", request.SecurityAssetsAmount),
                     ("fund assets", request.FundAssetsAmount),
                     ("guaranteed capital", request.GuaranteedCapitalAtRetirement),
                     ("guaranteed annuity", request.GuaranteedMonthlyAnnuity),
                     ("projected capital", request.ProjectedCapitalAtRetirement),
                     ("projected annuity", request.ProjectedMonthlyAnnuity)
                 })
            if (amount is < 0) return $"The {label} cannot be negative.";

        if (request.GuaranteedBalance is { } guaranteed && request.Balance is { } balance && guaranteed > balance)
            return "The guaranteed part cannot exceed the balance.";

        var hasProjection = request.ProjectedCapitalAtRetirement is not null || request.ProjectedMonthlyAnnuity is not null;
        var basis = Trim(request.ProjectionBasis);
        if (basis is not null && !BavProjectionBases.Allowed.Contains(basis))
            return $"Unknown projection basis '{basis}'.";
        if (hasProjection && (basis is null || request.ProjectionReturnPercent is null))
            return "A projected figure needs its basis and the return assumption behind it, or it cannot be told apart from a guarantee.";
        if (!hasProjection && (basis is not null || request.ProjectionReturnPercent is not null))
            return "A projection basis belongs to a projected figure.";
        if (request.ProjectionReturnPercent is < -100 or > 100)
            return "The assumed return must be between -100 and 100 percent.";

        var source = Trim(request.Source);
        if (source is not null && !BavValueSources.Allowed.Contains(source))
            return $"Unknown value source '{source}'.";

        if (request.ExtractionConfidence is < 0 or > 1)
            return "The extraction confidence is a value between 0 and 1.";

        if (request.EffectiveDate > DateOnly.FromDateTime(DateTime.UtcNow))
            return "A snapshot date cannot be in the future: it would be a value nobody has seen yet.";

        return null;
    }

    private static string SourceOrDefault(string? source)
    {
        var trimmed = Trim(source);
        return trimmed is not null && BavValueSources.Allowed.Contains(trimmed) ? trimmed : BavValueSources.Manual;
    }

    private static string AssetName(NormalizedContract value)
    {
        var suffix = value.TariffName is null ? null : $" · {value.TariffName}";
        return Truncate($"{value.ProviderName}{suffix}", 200);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>The arrangement in force on a date. A one-off contribution is never "in force".</summary>
    private static BavContribution? Running(IEnumerable<BavContribution> rows, Guid contractId, DateOnly on) =>
        rows.Where(row => row.BavContractId == contractId
                          && row.Cycle != BavContributionCycles.OneOff
                          && row.ValidFrom <= on
                          && (row.ValidUntil is null || row.ValidUntil >= on))
            .OrderByDescending(row => row.ValidFrom)
            .ThenByDescending(row => row.CreatedAt)
            .FirstOrDefault();

    private async Task<Dictionary<Guid, bool>> NetWorthFlagsAsync(
        IReadOnlyCollection<BavContract> contracts, CancellationToken ct)
    {
        var assetIds = contracts.Where(row => row.AssetId is not null).Select(row => row.AssetId!.Value).ToList();
        if (assetIds.Count == 0) return [];
        return await db.Assets.AsNoTracking()
            .Where(asset => assetIds.Contains(asset.Id))
            .Select(asset => new { asset.Id, asset.IncludeInNetWorth })
            .ToDictionaryAsync(row => row.Id, row => row.IncludeInNetWorth, ct);
    }

    private async Task<BavContractView> ViewAsync(BavContract contract, CancellationToken ct)
    {
        var snapshots = await db.BavSnapshots.AsNoTracking().Where(row => row.BavContractId == contract.Id).ToListAsync(ct);
        var contributions = await db.BavContributions.AsNoTracking().Where(row => row.BavContractId == contract.Id).ToListAsync(ct);
        var flags = await NetWorthFlagsAsync([contract], ct);
        return Project(contract, snapshots, contributions, flags);
    }

    private static BavContractView Project(
        BavContract contract,
        IReadOnlyCollection<BavSnapshot> snapshots,
        IReadOnlyCollection<BavContribution> contributions,
        IReadOnlyDictionary<Guid, bool> netWorthFlags)
    {
        var own = snapshots.Where(row => row.BavContractId == contract.Id).ToList();
        var current = own.FirstOrDefault(row => row.IsCurrent)
                      ?? own.OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.CreatedAt).FirstOrDefault();
        var running = Running(contributions, contract.Id, DateOnly.FromDateTime(DateTime.UtcNow))
                      ?? contributions.Where(row => row.BavContractId == contract.Id)
                          .OrderByDescending(row => row.ValidFrom).ThenByDescending(row => row.CreatedAt)
                          .FirstOrDefault();

        return new BavContractView(
            contract.Id,
            contract.FullWorthSpaceId,
            contract.ProviderName,
            contract.TariffName,
            contract.PolicyNumberLookup is not null,
            contract.PolicyNumberLast4,
            contract.ImplementationRoute,
            contract.Status,
            BavContractStatuses.HoldsCapital(contract.Status),
            contract.Status == BavContractStatuses.PaidUp,
            contract.EmployerName,
            contract.PolicyHolderName,
            contract.InsuredPersonName,
            contract.StartDate,
            contract.RetirementDate,
            contract.ContractEndDate,
            contract.Currency,
            contract.GuaranteeQuotaPercent,
            contract.GuaranteedAnnuityFactor,
            contract.FundSelectionChangeable,
            contract.AssetId,
            contract.RecurringContractId,
            contract.AssetId is { } assetId && netWorthFlags.TryGetValue(assetId, out var include) ? include : null,
            contract.Notes,
            current is null ? null : ToView(current),
            running is null ? null : ToView(running),
            own.Count,
            contract.CreatedAt,
            contract.UpdatedAt);
    }

    private static BavSnapshotView ToView(BavSnapshot row) => new(
        row.Id,
        row.BavContractId,
        row.EffectiveDate,
        row.Currency,
        row.Balance,
        row.GuaranteedBalance,
        row.SurrenderValue,
        row.SecurityAssetsAmount,
        row.FundAssetsAmount,
        row.GuaranteedCapitalAtRetirement,
        row.GuaranteedMonthlyAnnuity,
        row.ProjectedCapitalAtRetirement,
        row.ProjectedMonthlyAnnuity,
        row.ProjectionReturnPercent,
        row.ProjectionBasis,
        row.ProjectionBasis == BavProjectionBases.Simulation,
        row.Source,
        row.BavDocumentId,
        row.BavDocumentId is not null || row.DocumentSha256 is not null,
        row.ExtractionConfidence,
        row.IsCurrent,
        row.Note,
        row.CreatedByUserId,
        row.CreatedAt);

    private static BavContributionView ToView(BavContribution row)
    {
        var partsTotal = row.EmployeeAmount + row.EmployerSubsidyAmount + row.EmployerAmount;
        var employerTotal = row.EmployerSubsidyAmount + row.EmployerAmount;
        return new BavContributionView(
            row.Id,
            row.BavContractId,
            row.ValidFrom,
            row.ValidUntil,
            row.EndReason,
            row.Cycle,
            row.Currency,
            row.EmployeeAmount,
            row.EmployerSubsidyAmount,
            row.EmployerAmount,
            partsTotal,
            employerTotal,
            row.StatedTotalAmount,
            row.StatedTotalAmount is { } stated && Math.Abs(stated - partsTotal) > 0.01m,
            row.Source,
            row.TaxSavingAmount,
            row.SocialSecuritySavingAmount,
            row.NetEffortAmount,
            row.TaxEffectSource,
            row.TaxEffectSourceReference,
            BavTaxEffectSources.IsStated(row.TaxEffectSource),
            row.TaxEffectSource == BavTaxEffectSources.Simulation,
            row.BavDocumentId,
            row.Note,
            row.CreatedAt);
    }

    private static BavCostView ToView(BavCost row) => new(
        row.Id,
        row.BavContractId,
        row.BavSnapshotId,
        row.EffectiveDate,
        row.AppliesUntilDate,
        row.Kind,
        row.Basis,
        row.Amount,
        row.Currency,
        row.Percent,
        row.Timing,
        row.IsEstimated,
        row.EstimateBasis,
        row.ContinuesWhenPaidUp,
        row.Source,
        row.Note,
        row.CreatedAt);

    private static BavAllocationView ToView(BavInvestmentAllocation row) => new(
        row.Id,
        row.BavContractId,
        row.BavSnapshotId,
        row.EffectiveDate,
        row.FundName,
        row.Isin,
        row.WeightPercent,
        row.Amount,
        row.Currency,
        row.OngoingChargesPercent,
        row.OngoingChargesEstimated,
        row.AssetClass,
        row.Source,
        row.Note,
        row.CreatedAt);
}
