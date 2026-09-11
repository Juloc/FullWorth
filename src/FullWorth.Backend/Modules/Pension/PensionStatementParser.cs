using System.Text;
using System.Text.RegularExpressions;
using FullWorth.Backend.Modules.Parity;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The deterministic reader for a German pension statement (Standmitteilung, Versicherungsschein).
/// Pure: the same text produces the same draft on every host and under every locale, because nothing
/// here asks the machine what a number or a date looks like — amounts go through
/// <see cref="ImportNumber"/>, dates through <see cref="ImportDate"/> plus the explicit German month
/// table below, and every regex is <see cref="RegexOptions.CultureInvariant"/>.
///
/// The shape follows <c>PayslipTextParser</c>, which reads the other German document this product
/// parses: labels are anchored per line, the value is the nearest number <b>after</b> the label rather
/// than anything a full-line regex happens to catch, and the matching is deliberately forgiving about
/// what OCR does to a label (a one for an l, a zero for an o, doubled spaces, a hyphen where the
/// original had none, a label split across two lines).
///
/// Two rules of the pension area are load-bearing here and are enforced rather than assumed:
///
/// <list type="bullet">
///   <item>A guarantee is not a projection. "garantierte Leistung" and "voraussichtliche Leistung"
///         land in different draft fields, and a projected figure is only filled when the document
///         also states the return it rests on — see <see cref="ReadProjection"/>.</item>
///   <item>The employee share, the employer's statutory subsidy and the purely employer-financed
///         share stay apart, and <c>StatedTotalAmount</c> is the total as printed. A document that
///         does not add up is handed to the review screen as it is; nothing here recomputes it.</item>
/// </list>
///
/// Nothing in this file logs, and nothing puts a value into a provenance entry: a
/// <see cref="BavFieldProvenance.MatchedLabel"/> is the form label the document used
/// ("Vertragsguthaben"), never what stood next to it. That is what keeps the policy number — which
/// this parser does read, because the match rules need it — out of every diagnostic surface.
/// </summary>
public sealed partial class PensionStatementParser : IBavDocumentParser
{
    // How much a single field match is worth. A label and its value on one line is the strongest
    // signal a text layer can give; a value found only by joining two lines could have belonged to the
    // next row; a value derived from a neighbouring field (the date printed on the balance line, the
    // currency of the whole document) is weaker still but far better than nothing.
    private const decimal SameLineConfidence = 0.90m;
    private const decimal JoinedLineConfidence = 0.75m;
    private const decimal DerivedConfidence = 0.60m;
    private const decimal KeywordConfidence = 0.80m;

    private const string FieldEffectiveDate = "snapshot.effectiveDate";
    private const string FieldBalance = "snapshot.balance";
    private const string FieldCurrency = "snapshot.currency";
    private const string FieldProjectedCapital = "snapshot.projectedCapitalAtRetirement";
    private const string FieldProjectedAnnuity = "snapshot.projectedMonthlyAnnuity";

    public BavDocumentDraft Parse(BavDocumentText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IsEmpty)
            return BavDocumentDraft.Empty(text.PageCount, text.TextLayerUsed, BavExtractionSources.Deterministic);

        var scan = new Scan(text);

        var contract = ReadContract(scan);
        var snapshot = ReadSnapshot(scan, out var documentCurrency);
        var contribution = ReadContribution(scan, documentCurrency);
        // Allocations run before the costs on purpose: a fund line carries its own TER, and claiming
        // that line here is what stops the contract-level "Fondskosten" search from reading the same
        // percentage a second time as a separate cost.
        var allocations = ReadAllocations(scan, documentCurrency);
        var costs = ReadCosts(scan);

        contract = contract with { Currency = documentCurrency };

        return new BavDocumentDraft(
            contract,
            snapshot,
            contribution,
            allocations,
            costs,
            scan.Provenance,
            SnapshotConfidence(scan.Provenance),
            BavExtractionSources.Deterministic,
            text.PageCount,
            text.TextLayerUsed,
            scan.Unresolved);
    }

    /// <summary>
    /// The draft's confidence is a statement about the three fields a snapshot cannot be committed
    /// without — effective date, balance, currency — weighted 0.4 / 0.4 / 0.2, using each field's own
    /// match confidence. Everything else a statement carries is reviewable detail: a document with
    /// eleven recognised cost rows and no date is not a good extraction, and a number that rose with
    /// every extra field would say it was. The result is 0 when none of the three was found, which is
    /// exactly what an unusable text layer should report.
    ///
    /// Shared with <see cref="PensionDocumentCodexStructurer"/> so the optional AI pass cannot invent
    /// its own scale.
    /// </summary>
    internal static decimal SnapshotConfidence(IReadOnlyList<BavFieldProvenance> provenance)
    {
        decimal Of(string field) => provenance
            .Where(entry => entry.Field == field)
            .Select(entry => entry.Confidence)
            .DefaultIfEmpty(0m)
            .Max();

        var value = (0.4m * Of(FieldEffectiveDate)) + (0.4m * Of(FieldBalance)) + (0.2m * Of(FieldCurrency));
        return Math.Round(Math.Clamp(value, 0m, 1m), 2, MidpointRounding.AwayFromZero);
    }

    // ---------------------------------------------------------------- contract

    private static BavContractDraft ReadContract(Scan scan)
    {
        var provider = scan.Keep("contract.providerName", scan.Text(ProviderLabels, Claim.Shared));
        var tariff = scan.Keep("contract.tariffName", scan.Text(TariffLabels, Claim.Shared));
        var policyNumber = scan.Keep("contract.policyNumber", scan.PolicyNumber(PolicyNumberLabels));
        var employer = scan.Keep("contract.employerName", scan.Text(EmployerLabels, Claim.Shared));
        var policyHolder = scan.Keep("contract.policyHolderName", scan.Text(PolicyHolderLabels, Claim.Shared));
        var insured = scan.Keep("contract.insuredPersonName", scan.Text(InsuredPersonLabels, Claim.Shared));

        var start = scan.Keep("contract.startDate", scan.Date(StartDateLabels));
        var retirement = scan.Keep("contract.retirementDate", scan.Date(RetirementDateLabels));

        var quota = scan.Keep("contract.guaranteeQuotaPercent", scan.Percent(GuaranteeQuotaLabels, Claim.Shared));
        var annuityFactor = scan.Keep("contract.guaranteedAnnuityFactor", scan.Amount(AnnuityFactorLabels, Claim.Shared));

        var route = MapKeyword(scan, RouteKeywords, "contract.implementationRoute");
        var status = MapKeyword(scan, StatusKeywords, "contract.status");

        if (provider is null) scan.Unresolved.Add("contract.providerName");
        if (policyNumber is null) scan.Unresolved.Add("contract.policyNumber");
        if (route is null) scan.Unresolved.Add("contract.implementationRoute");
        // Status is never inferred from the presence of a contribution: a statement that says nothing
        // about the state of the contract is asked about, because "beitragsfrei" and "aktiv" differ in
        // what the review screen then has to write, not in how the balance reads.
        if (status is null) scan.Unresolved.Add("contract.status");

        return new BavContractDraft(
            ProviderName: provider,
            TariffName: tariff,
            PolicyNumber: policyNumber,
            ImplementationRoute: route,
            EmployerName: employer,
            PolicyHolderName: policyHolder,
            InsuredPersonName: insured,
            StartDate: start,
            RetirementDate: retirement,
            Currency: null,
            GuaranteeQuotaPercent: quota,
            GuaranteedAnnuityFactor: annuityFactor,
            Status: status);
    }

    // ---------------------------------------------------------------- snapshot

    private static BavSnapshotDraft ReadSnapshot(Scan scan, out string? currency)
    {
        // The specific labels go first and claim their line, so the plain "Vertragsguthaben" search
        // cannot pick up the guaranteed one: "garantiertes Vertragsguthaben" contains the general
        // label, and reading a guarantee as today's balance is the mistake this area must not make.
        var guaranteedBalance = scan.Keep("snapshot.guaranteedBalance", scan.Amount(GuaranteedBalanceLabels));
        var surrender = scan.Keep("snapshot.surrenderValue", scan.Amount(SurrenderValueLabels));
        var securityAssets = scan.Keep("snapshot.securityAssetsAmount", scan.Amount(SecurityAssetsLabels));
        var fundAssets = scan.Keep("snapshot.fundAssetsAmount", scan.Amount(FundAssetsLabels));

        var balanceHit = scan.Amount(BalanceLabels);
        var balance = scan.Keep(FieldBalance, balanceHit);
        if (balance is null) scan.Unresolved.Add(FieldBalance);

        var guaranteedCapital = scan.Keep("snapshot.guaranteedCapitalAtRetirement", scan.Amount(GuaranteedCapitalLabels));
        var guaranteedAnnuity = scan.Keep("snapshot.guaranteedMonthlyAnnuity", scan.Amount(GuaranteedAnnuityLabels));

        var projection = ReadProjection(scan);

        // "Stand: 31.12.2025" is the normal form; "Vertragsguthaben zum 01.01.2026: ..." states the
        // date on the value's own line instead, which is why the balance line is the fallback. Nothing
        // further is guessed — a statement without a readable date is asked about.
        var effectiveDate = scan.Date(EffectiveDateLabels);
        if (effectiveDate is null && balanceHit is not null)
        {
            var derived = DateIn(balanceHit.Raw);
            if (derived is not null)
                effectiveDate = new Hit<DateOnly>(derived.Value, balanceHit.Page, balanceHit.LineId,
                    balanceHit.Raw, DerivedConfidence, balanceHit.Label);
        }
        var date = scan.Keep(FieldEffectiveDate, effectiveDate);
        if (date is null) scan.Unresolved.Add(FieldEffectiveDate);

        currency = ReadCurrency(scan, balanceHit);

        return new BavSnapshotDraft(
            EffectiveDate: date,
            Currency: currency,
            Balance: balance,
            GuaranteedBalance: guaranteedBalance,
            SurrenderValue: surrender,
            SecurityAssetsAmount: securityAssets,
            FundAssetsAmount: fundAssets,
            GuaranteedCapitalAtRetirement: guaranteedCapital,
            GuaranteedMonthlyAnnuity: guaranteedAnnuity,
            ProjectedCapitalAtRetirement: projection.Capital,
            ProjectedMonthlyAnnuity: projection.Annuity,
            ProjectionReturnPercent: projection.ReturnPercent,
            ProjectionBasis: projection.Basis);
    }

    /// <summary>
    /// A forecast is only ever stored together with the return it assumes. The database says the same
    /// thing (<c>CK_BavSnapshots_Projection</c> refuses a projected figure without
    /// <c>ProjectionReturnPercent</c> and <c>ProjectionBasis</c>), so a draft that filled a projected
    /// field without them could never be committed — it would look extracted on screen and fail on
    /// save. When the document prints a forecast but no percentage, the field therefore stays empty
    /// and its name goes into <c>Unresolved</c>: the review screen asks for the assumption instead of
    /// showing a number nobody can qualify.
    /// </summary>
    private static (decimal? Capital, decimal? Annuity, decimal? ReturnPercent, string? Basis) ReadProjection(Scan scan)
    {
        var capital = scan.Amount(ProjectedCapitalLabels);
        var annuity = scan.Amount(ProjectedAnnuityLabels);
        if (capital is null && annuity is null) return (null, null, null, null);

        // An explicit assumption sentence ("bei einer angenommenen Wertentwicklung von 5 %") outranks a
        // percentage that merely stands on the projection line, because past performance is printed
        // the same way and only the wording tells them apart.
        var percentHit = scan.Percent(ReturnAssumptionLabels, Claim.Shared)
                         ?? PercentOn(capital)
                         ?? PercentOn(annuity);

        if (percentHit is null)
        {
            if (capital is not null) scan.Unresolved.Add(FieldProjectedCapital);
            if (annuity is not null) scan.Unresolved.Add(FieldProjectedAnnuity);
            return (null, null, null, null);
        }

        var capitalValue = scan.Keep(FieldProjectedCapital, capital);
        var annuityValue = scan.Keep(FieldProjectedAnnuity, annuity);
        var percent = scan.Keep("snapshot.projectionReturnPercent", percentHit);
        scan.Record("snapshot.projectionBasis", percentHit.Page, percentHit.Confidence, percentHit.Label);
        return (capitalValue, annuityValue, percent, BavProjectionBases.DocumentForecast);

        static Hit<decimal>? PercentOn(Hit<decimal>? hit)
        {
            if (hit is null) return null;
            var percent = PercentIn(hit.Raw);
            return percent is null ? null : hit with { Value = percent.Value, Confidence = DerivedConfidence };
        }
    }

    /// <summary>
    /// A currency is never assumed. Every German bAV statement this parser will ever see is in euro,
    /// but an assumed currency is a wrong amount the moment the assumption is wrong, and the wealth
    /// area converts what it is given — so a document that names no currency leaves the field empty
    /// and the commit path decides. The balance's own line wins over the document, and a document that
    /// names two currencies without one on the balance line is reported as unresolved rather than
    /// resolved by majority.
    /// </summary>
    private static string? ReadCurrency(Scan scan, Hit<decimal>? balance)
    {
        if (balance is not null && CurrencyIn(balance.Raw) is { } onBalanceLine)
        {
            scan.Record(FieldCurrency, balance.Page, SameLineConfidence, "Währungszeichen");
            return onBalanceLine;
        }

        var distinct = scan.Currencies();
        if (distinct.Count == 1)
        {
            scan.Record(FieldCurrency, distinct[0].Page, DerivedConfidence, "Währungszeichen");
            return distinct[0].Value;
        }

        scan.Unresolved.Add(FieldCurrency);
        return null;
    }

    // ------------------------------------------------------------ contribution

    private static BavContributionDraft? ReadContribution(Scan scan, string? documentCurrency)
    {
        // The specific shares first: "Arbeitgeberzuschuss" and "Arbeitgeberbeitrag" are different
        // money (a §1a BetrAVG subsidy on the deferred share vs. a purely employer-financed one), and
        // the generic "Beitrag" must not be able to claim a line one of them already explained.
        var employee = scan.Amount(EmployeeContributionLabels);
        var subsidy = scan.Amount(EmployerSubsidyLabels);
        var employer = scan.Amount(EmployerContributionLabels);
        var total = scan.Amount(TotalContributionLabels);
        if (employee is null && subsidy is null && employer is null && total is null) return null;

        var employeeAmount = scan.Keep("contribution.employeeAmount", employee);
        var subsidyAmount = scan.Keep("contribution.employerSubsidyAmount", subsidy);
        var employerAmount = scan.Keep("contribution.employerAmount", employer);
        // Printed as printed. The shares are not adjusted to match it and it is not adjusted to match
        // the shares; the review screen shows the mismatch and a person decides what the document meant.
        var statedTotal = scan.Keep("contribution.statedTotalAmount", total);

        var firstShare = employee ?? subsidy ?? employer ?? total;
        var cycleHit = CycleOn(firstShare) ?? scan.Keyword([.. CycleKeywords.Select(entry => entry.Label)]);
        string? cycle = null;
        if (cycleHit is not null)
        {
            cycle = CycleKeywords.First(entry => entry.Label.Display == cycleHit.Label).Value;
            scan.Record("contribution.cycle", cycleHit.Page, cycleHit.Confidence, cycleHit.Label);
        }

        var validFrom = scan.Keep("contribution.validFrom", scan.Date(ContributionDateLabels));
        // Not defaulted to the statement's date: the arrangement a statement prints usually started
        // before it, so a date nobody printed is asked for rather than borrowed from another field.
        if (validFrom is null) scan.Unresolved.Add("contribution.validFrom");

        return new BavContributionDraft(
            ValidFrom: validFrom,
            Cycle: cycle,
            Currency: (firstShare is null ? null : CurrencyIn(firstShare.Raw)) ?? documentCurrency,
            EmployeeAmount: employeeAmount,
            EmployerSubsidyAmount: subsidyAmount,
            EmployerAmount: employerAmount,
            StatedTotalAmount: statedTotal);

        static Hit<string>? CycleOn(Hit<decimal>? hit)
        {
            if (hit is null) return null;
            var folded = Fold(hit.Raw, keepSpaces: true);
            foreach (var entry in CycleKeywords)
                if (ContainsWholeWord(folded, entry.Label.Spaced))
                    return new Hit<string>(entry.Label.Display, hit.Page, hit.LineId, hit.Raw, hit.Confidence, entry.Label.Display);
            return null;
        }
    }

    // ------------------------------------------------------------- allocations

    private static IReadOnlyList<BavAllocationDraft> ReadAllocations(Scan scan, string? documentCurrency)
    {
        var allocations = new List<BavAllocationDraft>();

        foreach (var unit in scan.Singles)
        {
            var isin = IsinRegex().Match(unit.Raw);
            if (!isin.Success) continue;
            scan.ClaimLine(unit.LineId);

            var index = allocations.Count;
            var name = FundNameIn(unit.Raw, isin.Index) ?? scan.PreviousText(unit);
            if (name is null)
            {
                // The ISIN is the only identifier the line gave. Keeping it as the name preserves the
                // position instead of dropping it, and the review screen is told to ask for the name.
                name = isin.Value;
                scan.Unresolved.Add($"allocations[{index}].fundName");
            }

            var tail = unit.Raw[(isin.Index + isin.Length)..];
            var percents = PercentsIn(unit.Raw);
            var terTail = scan.TailIn(unit, FundChargeLabels);

            decimal? weight = null;
            decimal? charges = null;
            if (terTail is not null)
            {
                charges = percents.FirstOrDefault(candidate => candidate.Index >= terTail.Value)?.Value;
                weight = percents.FirstOrDefault(candidate => candidate.Index < terTail.Value)?.Value;
            }
            else if (percents.Count == 1)
            {
                weight = percents[0].Value;
            }
            else if (percents.Count > 1)
            {
                // Two unlabelled percentages on a fund line are a weight and a charge in an unknown
                // order. The weight is the one the layout puts first; the charge is not guessed.
                weight = percents[0].Value;
                scan.Unresolved.Add($"allocations[{index}].ongoingChargesPercent");
            }

            var amount = MoneyIn(tail) ?? MoneyIn(unit.Raw);
            var assetClass = AssetClassIn(unit.Raw);

            allocations.Add(new BavAllocationDraft(
                FundName: name,
                Isin: isin.Value,
                WeightPercent: weight,
                Amount: amount,
                Currency: CurrencyIn(unit.Raw) ?? documentCurrency,
                OngoingChargesPercent: charges,
                OngoingChargesEstimated: charges is not null && EstimateQualifierIn(unit.Raw) is not null,
                AssetClass: assetClass));

            scan.Record($"allocations[{index}].fundName", unit.Page, SameLineConfidence, "ISIN");
            scan.Record($"allocations[{index}].isin", unit.Page, SameLineConfidence, "ISIN");
            if (weight is not null) scan.Record($"allocations[{index}].weightPercent", unit.Page, SameLineConfidence, "Anteil");
            if (charges is not null) scan.Record($"allocations[{index}].ongoingChargesPercent", unit.Page, SameLineConfidence, "TER");
            if (amount is not null) scan.Record($"allocations[{index}].amount", unit.Page, SameLineConfidence, "ISIN");
            if (assetClass is not null) scan.Record($"allocations[{index}].assetClass", unit.Page, KeywordConfidence, "Anlageklasse");
        }

        return allocations;
    }

    // ------------------------------------------------------------------- costs

    private static IReadOnlyList<BavCostDraft> ReadCosts(Scan scan)
    {
        var costs = new List<BavCostDraft>();

        // Effektivkosten / Reduction in Yield is the aggregate the contract's yield is reduced by.
        // BavCostKinds has no aggregate kind, so it is stored as "other" on a capital basis rather
        // than folded into one of the named kinds, which would double-count the parts below it.
        Add(scan, costs, EffectiveCostLabels, BavCostKinds.Other, BavCostBases.PercentOfCapital, BavCostTimings.Ongoing);

        // Acquisition cost is money that was already charged into the contract, which is why it is
        // "incurred" and not "ongoing"; as a percentage it is a percentage of the Beitragssumme.
        Add(scan, costs, AcquisitionCostLabels, BavCostKinds.Acquisition, BavCostBases.PercentOfSum, BavCostTimings.Incurred);

        AddAdministration(scan, costs);
        Add(scan, costs, UnitCostLabels, BavCostKinds.AdministrationFixed, BavCostBases.PercentOfContribution, BavCostTimings.Ongoing);
        Add(scan, costs, FundChargeLabels, BavCostKinds.Fund, BavCostBases.PercentOfCapital, BavCostTimings.Ongoing);
        Add(scan, costs, GuaranteeCostLabels, BavCostKinds.Guarantee, BavCostBases.PercentOfCapital, BavCostTimings.Ongoing);
        Add(scan, costs, RiskPremiumLabels, BavCostKinds.RiskPremium, BavCostBases.PercentOfContribution, BavCostTimings.Ongoing);
        Add(scan, costs, PayoutCostLabels, BavCostKinds.Payout, BavCostBases.PercentOfAnnuity, BavCostTimings.Future);

        return costs;

        static void Add(Scan scan, List<BavCostDraft> costs, Label[] labels, string kind, string percentBasis, string timing)
        {
            var percent = scan.Percent(labels, Claim.Exclusive);
            if (percent is not null)
            {
                Append(scan, costs, kind, percentBasis, timing, percent, isPercent: true);
                return;
            }

            var amount = scan.Amount(labels);
            if (amount is not null) Append(scan, costs, kind, BavCostBases.FixedAmount, timing, amount, isPercent: false);
        }

        static void Append(Scan scan, List<BavCostDraft> costs, string kind, string basis, string timing,
            Hit<decimal> hit, bool isPercent)
        {
            var index = costs.Count;
            var estimate = EstimateQualifierIn(hit.Raw);
            costs.Add(new BavCostDraft(
                Kind: kind,
                Basis: basis,
                Amount: isPercent ? null : hit.Value,
                Percent: isPercent ? hit.Value : null,
                Timing: timing,
                IsEstimated: estimate is not null,
                EstimateBasis: estimate,
                // Arithmetic, not an assumption: a cost taken as a percentage of the contribution is
                // zero once no contribution is paid, while a cost on the capital keeps running. This
                // is what keeps "beitragsfrei" from being read as "cost-free" (docs/PENSION.md).
                ContinuesWhenPaidUp: basis != BavCostBases.PercentOfContribution));
            scan.Record($"costs[{index}].{(isPercent ? "percent" : "amount")}", hit.Page, hit.Confidence, hit.Label);
        }

        static void AddAdministration(Scan scan, List<BavCostDraft> costs)
        {
            var percent = scan.Percent(AdministrationCostLabels, Claim.Exclusive);
            if (percent is not null)
            {
                var folded = Fold(percent.Raw, keepSpaces: true);
                var basis = ContainsAny(folded, ContributionBasisHints) ? BavCostBases.PercentOfContribution
                    : ContainsAny(folded, CapitalBasisHints) ? BavCostBases.PercentOfCapital
                    : null;
                if (basis is null)
                {
                    // "Verwaltungskosten 2,5 %" is a percentage of something, and of the contribution
                    // or of the capital are very different contracts. Guessing one would change the
                    // projection by more than the figure itself, so the review screen is asked.
                    scan.Unresolved.Add("costs.administration.basis");
                    return;
                }

                var kind = basis == BavCostBases.PercentOfContribution
                    ? BavCostKinds.AdministrationOnContribution
                    : BavCostKinds.AdministrationOnCapital;
                Append(scan, costs, kind, basis, BavCostTimings.Ongoing, percent, isPercent: true);
                return;
            }

            var amount = scan.Amount(AdministrationCostLabels);
            if (amount is not null)
                Append(scan, costs, BavCostKinds.AdministrationFixed, BavCostBases.FixedAmount,
                    BavCostTimings.Ongoing, amount, isPercent: false);
        }
    }

    private static string? MapKeyword(Scan scan, (Label Label, string Value)[] keywords, string field)
    {
        var hit = scan.Keyword(keywords.Select(entry => entry.Label).ToArray());
        if (hit is null) return null;
        scan.Record(field, hit.Page, hit.Confidence, hit.Label);
        return keywords.First(entry => entry.Label.Display == hit.Label).Value;
    }

    // -------------------------------------------------------------- extractors

    /// <summary>
    /// The nearest amount after a label. Dates, percentages and ISINs are blanked out first, because
    /// "Vertragsguthaben zum 01.01.2026: 12.345,67 €" otherwise reads 1.01 as the balance, and a bare
    /// integer only counts as money next to a currency token — "65. Lebensjahr" and a page number are
    /// not amounts. The value goes through <see cref="ImportNumber"/>, so "1.234,56" and "1234.56" are
    /// both read correctly and neither depends on the host's culture.
    /// </summary>
    private static decimal? MoneyIn(string text)
    {
        var masked = MaskNonMoney(text);
        foreach (var match in MoneyRegex().Matches(masked).Cast<Match>())
        {
            var token = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (token.Length == 0) continue;
            try
            {
                // Grouping, because every figure on a statement is money with two decimals: "1.234" is
                // 1234 euro, never 1.234 euro.
                if (ImportNumber.TryParse(token, ImportNumber.ThreeDigitTail.Grouping) is { } amount)
                    return Math.Abs(amount);
            }
            catch (FormatException)
            {
                // A token the money regex accepted but ImportNumber rejects is malformed OCR, not an
                // amount. Skip it and keep looking along the line.
            }
        }
        return null;
    }

    /// <summary>"2,5 %", "0,95%" and "2,5 Prozent" all mean 2.5. A percentage is never stored as 0.025.</summary>
    private static decimal? PercentIn(string text) => PercentsIn(text).FirstOrDefault()?.Value;

    private static List<PercentCandidate> PercentsIn(string text)
    {
        var found = new List<PercentCandidate>();
        foreach (var match in PercentRegex().Matches(text).Cast<Match>())
        {
            try
            {
                // Decimal, not grouping: a percentage with three trailing digits is 1.234 %, and a
                // contract charging 1234 % does not exist.
                if (ImportNumber.TryParse(match.Groups[1].Value, ImportNumber.ThreeDigitTail.Decimal) is { } value)
                    found.Add(new PercentCandidate(match.Index, value));
            }
            catch (FormatException)
            {
                // Not a percentage after all.
            }
        }
        return found;
    }

    /// <summary>
    /// The nearest date after a label, numeric or written out. The numeric forms go to
    /// <see cref="ImportDate"/> (a German 03.04.2026 is 3 April on every host, which
    /// <c>DateOnly.TryParse</c> cannot promise); the month-name forms are resolved here against the
    /// explicit German month table, invariantly, rather than by relaxing <see cref="ImportDate"/> into
    /// something locale-aware.
    /// </summary>
    private static DateOnly? DateIn(string text)
    {
        var numeric = NumericDateRegex().Match(text);
        var written = MonthNameDateRegex().Match(text);

        if (numeric.Success && (!written.Success || numeric.Index <= written.Index))
        {
            var parsed = ImportDate.TryParse(numeric.Groups[1].Value);
            if (parsed is not null) return parsed;
        }

        if (!written.Success) return null;
        if (!Months.TryGetValue(written.Groups[2].Value, out var month)) return null;
        if (!int.TryParse(written.Groups[3].Value, out var year) || year is < 1900 or > 2200) return null;

        // "Januar 2026" with no day is the first of the month: a statement's Stand is a point in time,
        // and the day the document omitted is asked about on the review screen, not invented as the
        // month's end — which would move the value into the following period.
        var day = 1;
        if (written.Groups[1].Success && int.TryParse(written.Groups[1].Value, out var stated))
        {
            if (stated < 1 || stated > DateTime.DaysInMonth(year, month)) return null;
            day = stated;
        }
        return new DateOnly(year, month, day);
    }

    private static string? TextIn(string text)
    {
        var cut = text;
        var columnBreak = ColumnBreakRegex().Match(cut);
        if (columnBreak.Success) cut = cut[..columnBreak.Index];
        cut = cut.Trim(' ', '\t', ':', '-', '–', '—', '.', ',', ';', '|', '*');
        if (cut.Length is 0 or > 80) return null;
        return cut.Count(char.IsLetter) >= 2 ? cut : null;
    }

    /// <summary>
    /// The policy number, which the match rules in <see cref="PensionIdentity"/> need. It is read into
    /// the draft and nowhere else: it is not a provenance label, it is not part of any message, and no
    /// code path in this file writes it anywhere a log or an error string could pick it up.
    /// </summary>
    private static string? PolicyNumberIn(string text)
    {
        foreach (var token in text.Split([' ', '\t', '|', ';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim('(', ')', '[', ']', '.', ':', '*', '"');
            if (candidate.Length < 4 || !candidate.Any(char.IsDigit)) continue;
            if (!candidate.All(character => char.IsLetterOrDigit(character) || character is '-' or '/' or '.')) continue;
            return candidate;
        }
        return null;
    }

    private static string? FundNameIn(string raw, int isinIndex)
    {
        var before = raw[..isinIndex].Trim(' ', '\t', ':', '-', '–', '(', ')', '|', ',', '*');
        if (before.Length is 0 or > 80) return null;
        return before.Count(char.IsLetter) >= 3 ? before : null;
    }

    private static string? AssetClassIn(string raw)
    {
        var folded = Fold(raw, keepSpaces: true);
        foreach (var (label, value) in AssetClassKeywords)
            if (ContainsWholeWord(folded, label.Spaced))
                return value;
        return null;
    }

    private static string? EstimateQualifierIn(string raw)
    {
        var folded = Fold(raw, keepSpaces: true);
        foreach (var qualifier in EstimateQualifiers)
            if (ContainsWholeWord(folded, qualifier.Spaced))
                return $"Im Dokument nur als Näherung angegeben (\"{qualifier.Display}\")";
        return null;
    }

    private static string? CurrencyIn(string raw)
    {
        var match = CurrencyRegex().Match(raw);
        if (!match.Success) return null;
        var token = match.Groups[1].Value.ToUpperInvariant();
        return token switch
        {
            "€" or "EUR" => "EUR",
            "CHF" => "CHF",
            "$" or "USD" => "USD",
            _ => null
        };
    }

    private static string MaskNonMoney(string text)
    {
        var buffer = text.ToCharArray();
        Blank(NumericDateRegex().Matches(text));
        Blank(MonthNameDateRegex().Matches(text));
        Blank(PercentRegex().Matches(text));
        Blank(IsinRegex().Matches(text));
        return new string(buffer);

        void Blank(MatchCollection matches)
        {
            foreach (var match in matches.Cast<Match>())
                for (var index = match.Index; index < match.Index + match.Length; index++)
                    buffer[index] = ' ';
        }
    }

    private sealed record PercentCandidate(int Index, decimal Value);

    // ------------------------------------------------------- folding & matching

    /// <summary>
    /// One line reduced to what a label match may depend on, plus the index in the original line every
    /// folded character came from, so a value is always read out of the original text.
    /// </summary>
    private sealed record FoldedText(string Folded, int[] Source);

    /// <summary>
    /// A label as written and as matched. The <see cref="Squeezed"/> form has its spaces removed too,
    /// which is how "Vers.-Nr.", "Vers Nr" and "VersNr" all match the same label without weakening the
    /// word boundaries — those are still checked against the original characters.
    /// </summary>
    private sealed record Label(string Display, string Spaced, string Squeezed);

    private static Label[] L(params string[] displays) =>
        [.. displays.Select(display => new Label(display, Fold(display, true), Fold(display, false)))];

    private static Label One(string display) => new(display, Fold(display, true), Fold(display, false));

    /// <summary>
    /// Lower-cases, folds the umlauts, undoes the two substitutions OCR makes inside a word (a one for
    /// an l, a zero for an o) and drops the punctuation a document may or may not put inside a label.
    /// The index map is what keeps this honest: the folded text decides <b>where</b> a label sits, the
    /// original text decides what the value is.
    /// </summary>
    private static (string Folded, int[] Source) FoldWithMap(string raw, bool keepSpaces)
    {
        var folded = new StringBuilder(raw.Length);
        var source = new List<int>(raw.Length);
        var pendingSpace = false;
        var current = 0;

        void Append(char character)
        {
            folded.Append(character);
            source.Add(current);
        }

        for (current = 0; current < raw.Length; current++)
        {
            var character = raw[current];
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = folded.Length > 0;
                continue;
            }
            if (Dropped(character)) continue;

            if (pendingSpace && keepSpaces) Append(' ');
            pendingSpace = false;

            switch (char.ToLowerInvariant(character))
            {
                case 'ä': Append('a'); break;
                case 'ö': Append('o'); break;
                case 'ü': Append('u'); break;
                case 'ß': Append('s'); Append('s'); break;
                case '1': Append('l'); break;
                case '0': Append('o'); break;
                case '|': Append('l'); break;
                default: Append(char.ToLowerInvariant(character)); break;
            }
        }

        return (folded.ToString().TrimEnd(), source.ToArray());
    }

    private static string Fold(string raw, bool keepSpaces) => FoldWithMap(raw, keepSpaces).Folded;

    private static bool Dropped(char character) => character
        is '.' or ',' or ':' or ';' or '-' or '–' or '—' or '_' or '/' or '\\'
        or '(' or ')' or '[' or ']' or '{' or '}' or '"' or '\'' or '´' or '`'
        or '·' or '•' or '*' or '€' or '$' or '%' or '+' or '#' or '§' or '!' or '?'
        or '„' or '“' or '”' or '»' or '«';

    /// <summary>
    /// True when the folded text contains the label with a non-letter on both sides. The boundary is
    /// decided on the <b>original</b> character, so "Arbeitgeber 12" matches the label "Arbeitgeber"
    /// while "beitragsfreie Versicherungssumme" does not match "beitragsfrei" — which matters, because
    /// that line is printed on statements of perfectly active contracts.
    /// </summary>
    private static int? BoundedIndexOf(FoldedText text, string raw, string label)
    {
        if (label.Length == 0) return null;
        var from = 0;
        while (from <= text.Folded.Length - label.Length)
        {
            var at = text.Folded.IndexOf(label, from, StringComparison.Ordinal);
            if (at < 0) return null;
            var end = at + label.Length;
            if (IsBoundary(text, raw, at - 1) && IsBoundary(text, raw, end))
                return end < text.Folded.Length ? text.Source[end] : raw.Length;
            from = at + 1;
        }
        return null;
    }

    private static bool IsBoundary(FoldedText text, string raw, int position)
    {
        if (position < 0 || position >= text.Folded.Length) return true;
        if (text.Folded[position] == ' ') return true;
        return !char.IsLetter(raw[text.Source[position]]);
    }

    private static bool ContainsWholeWord(string foldedText, string foldedLabel)
    {
        var from = 0;
        while (from <= foldedText.Length - foldedLabel.Length)
        {
            var at = foldedText.IndexOf(foldedLabel, from, StringComparison.Ordinal);
            if (at < 0) return false;
            var beforeOk = at == 0 || !char.IsLetter(foldedText[at - 1]);
            var end = at + foldedLabel.Length;
            var afterOk = end == foldedText.Length || !char.IsLetter(foldedText[end]);
            if (beforeOk && afterOk) return true;
            from = at + 1;
        }
        return false;
    }

    private static bool ContainsAny(string foldedText, Label[] labels) =>
        labels.Any(label => ContainsWholeWord(foldedText, label.Spaced));

    // ------------------------------------------------------------------- scan

    /// <summary>Whether a search may read a line another field already explained, and whether it claims it.</summary>
    private enum Claim
    {
        /// <summary>Skips a claimed line and claims its own, so one printed figure feeds one field.</summary>
        Exclusive,

        /// <summary>Reads any line and claims none: a date, a name or an assumption may share a line.</summary>
        Shared
    }

    /// <summary>
    /// One searchable line. <see cref="ValueFloor"/> is 0 for a real line and the start of the second
    /// line for a joined pair: a joined pair may only supply a value that stands on its second line.
    /// Without that floor, "Abschluss- und Vertriebskosten: 2.400,00 €" joined to the next row would
    /// read the following row's percentage as the acquisition cost's percentage — a label that already
    /// has content behind it is not a label whose value sits on the next line.
    /// </summary>
    private sealed record Unit(
        int Page, int LineId, string Raw, FoldedText Spaced, FoldedText Squeezed, decimal Confidence, int ValueFloor);

    private sealed record Hit<T>(T Value, int Page, int LineId, string Raw, decimal Confidence, string Label);

    /// <summary>
    /// One pass over one document. Holds the per-call state (which lines are already explained, the
    /// provenance collected so far), so the parser itself stays stateless and can be a singleton.
    /// </summary>
    private sealed class Scan
    {
        private readonly List<Unit> units = [];
        private readonly List<Unit> singles = [];
        private readonly HashSet<int> claimed = [];

        internal Scan(BavDocumentText text)
        {
            var joined = new List<Unit>();
            var lineId = 0;
            for (var page = 0; page < text.Pages.Count; page++)
            {
                var lines = (text.Pages[page] ?? string.Empty)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var first = singles.Count;
                foreach (var line in lines)
                {
                    var raw = line.Replace('\r', ' ');
                    singles.Add(new Unit(page + 1, lineId++, raw,
                        FoldedOf(raw, true), FoldedOf(raw, false), SameLineConfidence, ValueFloor: 0));
                }

                // A label the layout broke across two lines ("Voraussichtliche" / "Ablaufleistung
                // 82.400 €") and a label whose value sits on the next line are the same problem, so
                // both are covered by searching the pair as one line - after every single line has
                // been tried, so a clean match always wins.
                for (var index = first; index < singles.Count - 1; index++)
                {
                    var raw = singles[index].Raw + " " + singles[index + 1].Raw;
                    joined.Add(new Unit(singles[index].Page, singles[index].LineId, raw,
                        FoldedOf(raw, true), FoldedOf(raw, false), JoinedLineConfidence,
                        ValueFloor: singles[index].Raw.Length + 1));
                }
            }

            units.AddRange(singles);
            units.AddRange(joined);

            static FoldedText FoldedOf(string raw, bool keepSpaces)
            {
                var (folded, source) = FoldWithMap(raw, keepSpaces);
                return new FoldedText(folded, source);
            }
        }

        internal List<BavFieldProvenance> Provenance { get; } = [];

        internal List<string> Unresolved { get; } = [];

        internal IReadOnlyList<Unit> Singles => singles;

        internal void ClaimLine(int lineId) => claimed.Add(lineId);

        internal Hit<decimal>? Amount(Label[] labels, Claim claim = Claim.Exclusive) => Find(labels, MoneyIn, claim);

        internal Hit<decimal>? Percent(Label[] labels, Claim claim) => Find(labels, PercentIn, claim);

        internal Hit<DateOnly>? Date(Label[] labels) => Find(labels, DateIn, Claim.Shared);

        internal Hit<string>? Text(Label[] labels, Claim claim) => FindText(labels, TextIn, claim);

        internal Hit<string>? PolicyNumber(Label[] labels) => FindText(labels, PolicyNumberIn, Claim.Shared);

        /// <summary>A word the document either uses or does not — a route, a status, a cycle.</summary>
        internal Hit<string>? Keyword(Label[] labels)
        {
            foreach (var label in labels)
                foreach (var unit in units)
                    if (BoundedIndexOf(unit.Spaced, unit.Raw, label.Spaced) is not null
                        || BoundedIndexOf(unit.Squeezed, unit.Raw, label.Squeezed) is not null)
                        return new Hit<string>(label.Display, unit.Page, unit.LineId, unit.Raw, KeywordConfidence, label.Display);
            return null;
        }

        internal int? TailIn(Unit unit, Label[] labels)
        {
            foreach (var label in labels)
            {
                var tail = BoundedIndexOf(unit.Spaced, unit.Raw, label.Spaced)
                           ?? BoundedIndexOf(unit.Squeezed, unit.Raw, label.Squeezed);
                if (tail is not null) return tail;
            }
            return null;
        }

        /// <summary>The line above, for a fund name the layout put on its own line.</summary>
        internal string? PreviousText(Unit unit)
        {
            var index = singles.FindIndex(candidate => candidate.LineId == unit.LineId);
            if (index <= 0) return null;
            var previous = singles[index - 1];
            return previous.Page == unit.Page ? TextIn(previous.Raw) : null;
        }

        internal List<Hit<string>> Currencies()
        {
            var found = new List<Hit<string>>();
            foreach (var unit in singles)
            {
                var currency = CurrencyIn(unit.Raw);
                if (currency is null || found.Any(hit => hit.Value == currency)) continue;
                found.Add(new Hit<string>(currency, unit.Page, unit.LineId, unit.Raw, DerivedConfidence, "Währungszeichen"));
            }
            return found;
        }

        internal T? Keep<T>(string field, Hit<T>? hit) where T : struct
        {
            if (hit is null) return null;
            Record(field, hit.Page, hit.Confidence, hit.Label);
            return hit.Value;
        }

        internal string? Keep(string field, Hit<string>? hit)
        {
            if (hit is null) return null;
            Record(field, hit.Page, hit.Confidence, hit.Label);
            return hit.Value;
        }

        internal void Record(string field, int? page, decimal confidence, string matchedLabel) =>
            Provenance.Add(new BavFieldProvenance(field, page, confidence, BavExtractionSources.Deterministic, matchedLabel));

        private Hit<T>? Find<T>(Label[] labels, Func<string, T?> extract, Claim claim) where T : struct
        {
            foreach (var label in labels)
                foreach (var unit in units)
                {
                    if (claim == Claim.Exclusive && claimed.Contains(unit.LineId)) continue;
                    if (TailOf(unit, label) is not { } tail || tail < unit.ValueFloor) continue;
                    if (extract(unit.Raw[tail..]) is not { } value) continue;
                    if (claim == Claim.Exclusive) claimed.Add(unit.LineId);
                    return new Hit<T>(value, unit.Page, unit.LineId, unit.Raw, unit.Confidence, label.Display);
                }
            return null;
        }

        private Hit<string>? FindText(Label[] labels, Func<string, string?> extract, Claim claim)
        {
            foreach (var label in labels)
                foreach (var unit in units)
                {
                    if (claim == Claim.Exclusive && claimed.Contains(unit.LineId)) continue;
                    if (TailOf(unit, label) is not { } tail || tail < unit.ValueFloor) continue;
                    if (extract(unit.Raw[tail..]) is not { } value) continue;
                    if (claim == Claim.Exclusive) claimed.Add(unit.LineId);
                    return new Hit<string>(value, unit.Page, unit.LineId, unit.Raw, unit.Confidence, label.Display);
                }
            return null;
        }

        private static int? TailOf(Unit unit, Label label) =>
            BoundedIndexOf(unit.Spaced, unit.Raw, label.Spaced)
            ?? BoundedIndexOf(unit.Squeezed, unit.Raw, label.Squeezed);
    }

    // ------------------------------------------------------------------ labels
    // Label order is priority order: the most specific wording first, because the general one is a
    // substring of it ("garantiertes Vertragsguthaben" contains "Vertragsguthaben").

    private static readonly Label[] ProviderLabels = L(
        "Versicherer", "Versicherungsunternehmen", "Versicherungsgesellschaft", "Gesellschaft",
        "Anbieter", "Versorgungsträger", "Vorsorgeeinrichtung");

    private static readonly Label[] TariffLabels = L(
        "Tarifbezeichnung", "Tarifname", "Tarif", "Produktname", "Produktbezeichnung", "Produkt", "Bezeichnung des Tarifs");

    private static readonly Label[] PolicyNumberLabels = L(
        "Versicherungsnummer", "Versicherungsscheinnummer", "Versicherungsschein-Nr.", "Vertragsnummer",
        "Vertrags-Nr.", "Policennummer", "Policenummer", "Police-Nr.", "Policen-Nr.", "Vers.-Nr.",
        "Vers.Nr.", "Mitgliedsnummer");

    private static readonly Label[] EmployerLabels = L(
        "Trägerunternehmen", "Arbeitgeber", "Firma", "Betrieb");

    private static readonly Label[] PolicyHolderLabels = L(
        "Versicherungsnehmer", "Vertragsnehmer", "Policeninhaber");

    private static readonly Label[] InsuredPersonLabels = L(
        "versicherte Person", "Versicherter", "begünstigte Person", "Arbeitnehmer/in", "Bezugsberechtigte Person");

    private static readonly Label[] StartDateLabels = L(
        "Versicherungsbeginn", "Vertragsbeginn", "Beginn der Versicherung", "Beginn des Vertrages",
        "Vertragsabschluss", "Beginn");

    private static readonly Label[] RetirementDateLabels = L(
        "Rentenbeginn", "vereinbarter Rentenbeginn", "Rentenbeginndatum", "Versicherungsablauf",
        "Vertragsablauf", "Ablaufdatum", "Ablauf der Versicherung", "Leistungsbeginn", "Ablauf");

    private static readonly Label[] EffectiveDateLabels = L(
        "Stand der Werte", "Wertstand zum", "Werte zum", "Wert zum", "Stand zum", "Standmitteilung zum",
        "Bewertungsstichtag", "Stichtag", "Vertragsstand zum", "Stand");

    private static readonly Label[] BalanceLabels = L(
        "aktuelles Vertragsguthaben", "Vertragsguthaben", "Vertragswert", "Deckungskapital",
        "Policenwert", "Vorsorgekapital", "Versorgungskapital", "Guthaben");

    private static readonly Label[] GuaranteedBalanceLabels = L(
        "garantiertes Vertragsguthaben", "garantiertes Guthaben", "garantiertes Deckungskapital",
        "Garantieguthaben", "garantierter Vertragswert");

    private static readonly Label[] SurrenderValueLabels = L(
        "Rückkaufswert", "Rueckkaufswert", "Übertragungswert", "Uebertragungswert", "Abfindungswert");

    private static readonly Label[] SecurityAssetsLabels = L(
        "Sicherungsvermögen", "Sicherungsvermoegen", "Sicherungskapital", "Deckungsstock");

    private static readonly Label[] FundAssetsLabels = L(
        "Fondsvermögen", "Fondsvermoegen", "Fondsguthaben", "Fondsanlage", "Fondswert", "Investmentvermögen");

    private static readonly Label[] GuaranteedCapitalLabels = L(
        "garantierte Ablaufleistung", "garantiertes Kapital", "Garantiekapital", "garantiertes Endkapital",
        "garantierte Kapitalabfindung", "garantierte Versicherungsleistung", "garantierte Leistung",
        "Garantieleistung");

    private static readonly Label[] GuaranteedAnnuityLabels = L(
        "garantierte monatliche Rente", "garantierte Monatsrente", "garantierte monatliche Altersrente",
        "garantierte Altersrente", "monatliche Garantierente", "Garantierente");

    private static readonly Label[] ProjectedCapitalLabels = L(
        "voraussichtliche Ablaufleistung", "voraussichtliches Kapital", "prognostizierte Ablaufleistung",
        "hochgerechnete Ablaufleistung", "mögliche Ablaufleistung", "voraussichtliche Leistung",
        "voraussichtliches Vertragsguthaben", "prognostiziertes Kapital");

    private static readonly Label[] ProjectedAnnuityLabels = L(
        "voraussichtliche monatliche Rente", "voraussichtliche Monatsrente", "prognostizierte Monatsrente",
        "hochgerechnete Monatsrente", "mögliche Monatsrente", "voraussichtliche Altersrente",
        "prognostizierte monatliche Rente");

    // Only assumption wording, never a bare "Wertentwicklung": past performance is printed with the
    // same word, and a projection basis read off last year's return is a fabricated forecast.
    private static readonly Label[] ReturnAssumptionLabels = L(
        "bei einer angenommenen Wertentwicklung von", "angenommene Wertentwicklung", "unterstellte Wertentwicklung",
        "bei einer unterstellten Wertentwicklung", "bei einer Wertentwicklung von", "Wertentwicklungsannahme",
        "angenommene Verzinsung", "unterstellte Verzinsung", "hochgerechnet mit", "Renditeannahme",
        "angenommener Wertzuwachs");

    private static readonly Label[] GuaranteeQuotaLabels = L(
        "Garantiequote", "Garantieniveau", "Beitragsgarantie", "Garantieanteil");

    private static readonly Label[] AnnuityFactorLabels = L(
        "garantierter Rentenfaktor", "Rentenfaktor");

    private static readonly Label[] EmployeeContributionLabels = L(
        "Arbeitnehmerbeitrag", "Beitrag Arbeitnehmer", "Arbeitnehmeranteil", "Eigenbeitrag",
        "Entgeltumwandlung", "umgewandeltes Entgelt", "AN-Beitrag");

    private static readonly Label[] EmployerSubsidyLabels = L(
        "gesetzlicher Arbeitgeberzuschuss", "Arbeitgeberzuschuss", "AG-Zuschuss", "Zuschuss des Arbeitgebers",
        "Arbeitgeberförderung", "Zuschuss Arbeitgeber");

    private static readonly Label[] EmployerContributionLabels = L(
        "arbeitgeberfinanzierter Beitrag", "Arbeitgeberbeitrag", "Beitrag Arbeitgeber", "Arbeitgeberanteil",
        "AG-Beitrag");

    private static readonly Label[] TotalContributionLabels = L(
        "Gesamtbeitrag", "Beitrag insgesamt", "Gesamtaufwand", "laufender Beitrag", "Monatsbeitrag",
        "Jahresbeitrag", "Beitrag");

    private static readonly Label[] ContributionDateLabels = L(
        "Beitrag ab", "Beitragszahlung ab", "gültig ab", "Beitrag seit", "Beitragsänderung zum");

    private static readonly Label[] EffectiveCostLabels = L(
        "Effektivkosten", "effektive Kosten", "Renditeminderung", "Reduction in Yield");

    private static readonly Label[] AcquisitionCostLabels = L(
        "Abschluss- und Vertriebskosten", "Abschluss und Vertriebskosten", "Abschlusskosten",
        "Vertriebskosten", "Einrichtungskosten");

    private static readonly Label[] AdministrationCostLabels = L(
        "Verwaltungskosten", "Verwaltungskostenzuschlag", "Verwaltungsgebühr");

    private static readonly Label[] UnitCostLabels = L(
        "Stückkosten", "Stueckkosten", "Grundgebühr", "fixe Verwaltungskosten");

    private static readonly Label[] FundChargeLabels = L(
        "Fondskosten", "laufende Fondskosten", "laufende Kosten des Fonds", "TER", "Gesamtkostenquote",
        "Verwaltungsvergütung des Fonds");

    private static readonly Label[] GuaranteeCostLabels = L(
        "Garantiekosten", "Kosten der Garantie", "Garantiegebühr");

    private static readonly Label[] RiskPremiumLabels = L(
        "Risikobeitrag", "Risikoanteil", "Beitrag für Risikoschutz", "Risikoprämie");

    private static readonly Label[] PayoutCostLabels = L(
        "Kosten in der Rentenphase", "Rentenbezugskosten", "Kosten während des Rentenbezugs", "Rentenzahlungskosten");

    private static readonly Label[] ContributionBasisHints = L(
        "der Beiträge", "des Beitrags", "vom Beitrag", "je Beitrag", "der Beitragssumme", "auf den Beitrag");

    private static readonly Label[] CapitalBasisHints = L(
        "des Vermögens", "vom Vermögen", "des Guthabens", "vom Guthaben", "des Deckungskapitals",
        "des Fondsvermögens", "des Vertragsguthabens", "des Kapitals");

    private static readonly Label[] EstimateQualifiers =
    [
        One("ca"), One("circa"), One("rund"), One("etwa"), One("durchschnittlich"),
        One("voraussichtlich"), One("geschätzt"), One("geschaetzt"), One("Näherungswert"), One("Richtwert")
    ];

    private static readonly (Label Label, string Value)[] RouteKeywords =
    [
        (One("Direktversicherung"), BavImplementationRoutes.DirectInsurance),
        (One("Pensionskasse"), BavImplementationRoutes.PensionFund),
        (One("Pensionsfonds"), BavImplementationRoutes.PensionScheme),
        (One("Unterstützungskasse"), BavImplementationRoutes.ProvidentFund),
        (One("Unterstuetzungskasse"), BavImplementationRoutes.ProvidentFund),
        (One("Direktzusage"), BavImplementationRoutes.DirectCommitment),
        (One("Pensionszusage"), BavImplementationRoutes.DirectCommitment)
    ];

    // "beitragsfrei" is paid_up: the contract, its balance and its costs continue and only the
    // contributions stopped (docs/PENSION.md, "Beitragsfrei"). It is neither terminated nor free of
    // charge. The word boundary is what keeps "beitragsfreie Versicherungssumme" — printed on active
    // contracts as the value they would keep — from switching a running contract to paid_up.
    private static readonly (Label Label, string Value)[] StatusKeywords =
    [
        (One("beitragsfrei gestellt"), BavContractStatuses.PaidUp),
        (One("Beitragsfreistellung"), BavContractStatuses.PaidUp),
        (One("beitragsfrei"), BavContractStatuses.PaidUp),
        (One("in Rentenbezug"), BavContractStatuses.InPayout),
        (One("Rentenbezug läuft"), BavContractStatuses.InPayout),
        (One("Rentenzahlung läuft"), BavContractStatuses.InPayout),
        (One("übertragen auf"), BavContractStatuses.Transferred),
        (One("gekündigt"), BavContractStatuses.Terminated),
        (One("Vertrag beendet"), BavContractStatuses.Terminated),
        (One("beitragspflichtig"), BavContractStatuses.Active),
        (One("laufende Beitragszahlung"), BavContractStatuses.Active)
    ];

    private static readonly (Label Label, string Value)[] CycleKeywords =
    [
        (One("vierteljährlich"), BavContributionCycles.Quarterly),
        (One("halbjährlich"), BavContributionCycles.SemiAnnual),
        (One("monatlich"), BavContributionCycles.Monthly),
        (One("jährlich"), BavContributionCycles.Yearly),
        (One("jaehrlich"), BavContributionCycles.Yearly),
        (One("Einmalbeitrag"), BavContributionCycles.OneOff),
        (One("einmalig"), BavContributionCycles.OneOff)
    ];

    private static readonly (Label Label, string Value)[] AssetClassKeywords =
    [
        (One("Aktienfonds"), BavAssetClasses.Equity),
        (One("Aktien"), BavAssetClasses.Equity),
        (One("Rentenfonds"), BavAssetClasses.Bond),
        (One("Anleihen"), BavAssetClasses.Bond),
        (One("Mischfonds"), BavAssetClasses.Mixed),
        (One("Geldmarktfonds"), BavAssetClasses.MoneyMarket),
        (One("Geldmarkt"), BavAssetClasses.MoneyMarket),
        (One("Immobilienfonds"), BavAssetClasses.RealEstate),
        (One("Immobilien"), BavAssetClasses.RealEstate),
        (One("Rohstoffe"), BavAssetClasses.Commodity),
        (One("Sicherungsvermögen"), BavAssetClasses.GuaranteeAssets)
    ];

    /// <summary>
    /// The German month names, written out here rather than taken from a culture: a container runs
    /// with the invariant culture and would not know a single one of them.
    /// </summary>
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["januar"] = 1, ["jänner"] = 1, ["jaenner"] = 1, ["jan"] = 1,
        ["februar"] = 2, ["feb"] = 2,
        ["märz"] = 3, ["maerz"] = 3, ["marz"] = 3, ["mrz"] = 3, ["mär"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["mai"] = 5,
        ["juni"] = 6, ["jun"] = 6,
        ["juli"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sept"] = 9, ["sep"] = 9,
        ["oktober"] = 10, ["okt"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["dezember"] = 12, ["dez"] = 12
    };

    // ------------------------------------------------------------------ regexes

    private const string MonthNamePattern =
        "januar|jänner|jaenner|februar|märz|maerz|marz|april|mai|juni|juli|august|september|oktober|november|dezember" +
        "|jan|feb|mrz|mär|apr|jun|jul|aug|sept|sep|okt|nov|dez";

    private const string MonthNameDatePattern =
        @"(?:(\d{1,2})\s*\.?\s*)?\b(" + MonthNamePattern + @")\b\s*,?\s*(\d{4})(?!\d)";

    /// <summary>
    /// An amount is a grouped or decimal figure, or a bare integer next to a currency token. Without
    /// that last restriction "65. Lebensjahr", a page number and a year all become money.
    /// </summary>
    [GeneratedRegex(
        @"(?<!\d)(?:(\d{1,3}(?:[.\s]\d{3})+(?:,\d{1,2})?|\d+,\d{1,2})(?:\s*(?:€|EUR|CHF|USD))?|(\d+(?:,\d{1,2})?)\s*(?:€|EUR|CHF|USD))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d{1,4})?)\s*(?:%|prozent)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"(?<!\d)(\d{1,2}\.\d{1,2}\.\d{4}|\d{4}-\d{2}-\d{2})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex NumericDateRegex();

    [GeneratedRegex(MonthNameDatePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex MonthNameDateRegex();

    /// <summary>
    /// Two letters plus nine alphanumerics plus a check digit. Deliberately case-sensitive: an ISIN is
    /// upper case by definition, and <see cref="RegexOptions.IgnoreCase"/> would turn every twelve-
    /// character word ending in a digit into a fund position.
    /// </summary>
    [GeneratedRegex(@"\b([A-Z]{2}[A-Z0-9]{9}[0-9])\b", RegexOptions.CultureInvariant, 250)]
    private static partial Regex IsinRegex();

    [GeneratedRegex(@"(€|\$|\bEUR\b|\bCHF\b|\bUSD\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex CurrencyRegex();

    [GeneratedRegex(@"(?:\s{3,}|\t|\|)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex ColumnBreakRegex();
}
