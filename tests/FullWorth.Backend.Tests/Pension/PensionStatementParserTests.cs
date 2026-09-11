using System.Globalization;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Pension;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Pension;

/// <summary>
/// The deterministic pension-statement reader (docs/PENSION.md, "Extraktion"). No database, no
/// network, no Codex: the parser is a pure function and these are the promises it makes.
///
/// The statement texts below are written for these tests in the wording German providers use; none of
/// them is a copy of a real document, and the policy numbers are invented.
/// </summary>
public sealed class PensionStatementParserTests
{
    private static BavDocumentDraft Parse(params string[] pages) =>
        new PensionStatementParser().Parse(new BavDocumentText(pages, TextLayerUsed: true));

    /// <summary>A full statement, used wherever a test needs a document that states everything.</summary>
    private const string Statement = """
        Allianz Lebensversicherungs-AG
        Standmitteilung zum 31.12.2025
        Versicherungsnummer: 4711/8899
        Tarif: Perspektive Direktversicherung
        Durchführungsweg: Direktversicherung
        Arbeitgeber: Muster Maschinenbau GmbH
        Versicherungsbeginn: 01.07.2018
        Rentenbeginn: 01.08.2049

        Vertragsguthaben: 12.345,67 €
        Rückkaufswert: 11.480,00 €
        Garantierte Ablaufleistung: 40.000,00 €
        Garantierte monatliche Rente: 145,20 €
        Voraussichtliche Ablaufleistung bei einer angenommenen Wertentwicklung von 5,0 %: 62.400,00 €
        Voraussichtliche monatliche Rente: 226,50 €

        Beitrag Arbeitnehmer (Entgeltumwandlung): 100,00 €
        Arbeitgeberzuschuss (15 %): 15,00 €
        Arbeitgeberbeitrag: 50,00 €
        Gesamtbeitrag monatlich: 180,00 €
        """;

    // ------------------------------------------------------------------ values

    [Fact]
    public void A_german_balance_is_read_as_one_number_with_two_decimals()
    {
        var draft = Parse("Stand: 31.12.2025\nVertragsguthaben: 12.345,67 €");

        Assert.Equal(12345.67m, draft.Snapshot.Balance);
        Assert.Equal(new DateOnly(2025, 12, 31), draft.Snapshot.EffectiveDate);
        Assert.Equal("EUR", draft.Snapshot.Currency);
    }

    [Theory]
    [InlineData("Vertragsguthaben")]
    [InlineData("Vertragswert")]
    [InlineData("Deckungskapital")]
    public void Every_wording_for_the_current_value_is_the_balance(string label)
    {
        var draft = Parse($"Stand: 31.12.2025\n{label}: 12.345,67 €");

        Assert.Equal(12345.67m, draft.Snapshot.Balance);
    }

    // The surrender value is what the contract would pay out if it ended today. It is lower than the
    // balance by the costs it has not amortised yet, so reading it as the balance understates the
    // asset - and it has its own field for exactly that reason.
    [Fact]
    public void A_surrender_value_is_not_the_balance()
    {
        var draft = Parse("Vertragswert zum 01.01.2026: 20.000,00 EUR\nRückkaufswert: 18.500,00 EUR");

        Assert.Equal(20000m, draft.Snapshot.Balance);
        Assert.Equal(18500m, draft.Snapshot.SurrenderValue);
        // The date the value line carries, which is the other form a statement states its Stand in.
        Assert.Equal(new DateOnly(2026, 1, 1), draft.Snapshot.EffectiveDate);
    }

    [Fact]
    public void A_written_out_month_is_a_date_too()
    {
        var draft = Parse("Stand der Werte: 31. Dezember 2025\nVertragsguthaben: 1.000,00 €");

        Assert.Equal(new DateOnly(2025, 12, 31), draft.Snapshot.EffectiveDate);
    }

    /// <summary>
    /// An assumed currency is a wrong amount as soon as the assumption is wrong, and the wealth area
    /// converts whatever it is handed - so a document that names none leaves the field empty and asks.
    /// (Every German bAV statement in practice is in euro; the commit path may default, the parser
    /// may not.)
    /// </summary>
    [Fact]
    public void A_document_that_names_no_currency_does_not_get_one()
    {
        var draft = Parse("Stand: 31.12.2025\nVertragsguthaben: 12.345,67");

        Assert.Equal(12345.67m, draft.Snapshot.Balance);
        Assert.Null(draft.Snapshot.Currency);
        Assert.Contains("snapshot.currency", draft.Unresolved);
    }

    /// <summary>
    /// Scanned statements are the normal case, so the label match survives what OCR does to one: a
    /// one for an l, a column of spaces instead of a colon, and a label the layout broke in half.
    /// </summary>
    [Fact]
    public void Ocr_noise_and_a_broken_label_still_match()
    {
        var draft = Parse("""
            Stand: 31.12.2025
            Vertragsguthaben     12.345,67 €
            Garantierte Ab1auf1eistung: 40.000,00 €
            Voraussichtliche
            Ablaufleistung bei angenommener Wertentwicklung von 4,0 %: 50.000,00 €
            """);

        Assert.Equal(12345.67m, draft.Snapshot.Balance);
        Assert.Equal(40000m, draft.Snapshot.GuaranteedCapitalAtRetirement);
        Assert.Equal(50000m, draft.Snapshot.ProjectedCapitalAtRetirement);
        Assert.Equal(4.0m, draft.Snapshot.ProjectionReturnPercent);
        Assert.Equal(BavProjectionBases.DocumentForecast, draft.Snapshot.ProjectionBasis);
    }

    // ------------------------------------------------------- guarantee vs. forecast

    /// <summary>
    /// The split this feature lives or dies by: a guarantee is a promise, a forecast is an assumption,
    /// and they are printed next to each other on every statement.
    /// </summary>
    [Fact]
    public void A_guarantee_and_a_forecast_land_in_different_fields()
    {
        var draft = Parse(Statement);

        Assert.Equal(40000m, draft.Snapshot.GuaranteedCapitalAtRetirement);
        Assert.Equal(145.20m, draft.Snapshot.GuaranteedMonthlyAnnuity);
        Assert.Equal(62400m, draft.Snapshot.ProjectedCapitalAtRetirement);
        Assert.Equal(226.50m, draft.Snapshot.ProjectedMonthlyAnnuity);
        // A projection without these two could never be committed: CK_BavSnapshots_Projection refuses it.
        Assert.Equal(5.0m, draft.Snapshot.ProjectionReturnPercent);
        Assert.Equal(BavProjectionBases.DocumentForecast, draft.Snapshot.ProjectionBasis);
    }

    /// <summary>
    /// A percentage is a percentage: 2,5 % is 2.5 and never 0.025, whichever way the document writes it.
    /// </summary>
    [Theory]
    [InlineData("2,5 %", 2.5)]
    [InlineData("0,95%", 0.95)]
    [InlineData("2,5 Prozent", 2.5)]
    public void A_percentage_keeps_its_scale(string printed, double expected)
    {
        var draft = Parse($"Beitragsgarantie: {printed}\nVertragsguthaben: 1.000,00 €");

        Assert.Equal((decimal)expected, draft.Contract.GuaranteeQuotaPercent);
    }

    /// <summary>
    /// A forecast whose assumption the document does not print cannot be stored - the store and the
    /// database check both refuse it - so it is not extracted at all and the review screen is asked.
    /// </summary>
    [Fact]
    public void A_forecast_without_a_stated_return_is_asked_about_instead_of_extracted()
    {
        var draft = Parse("Stand: 31.12.2025\nVertragsguthaben: 10.000,00 €\nVoraussichtliche Ablaufleistung: 55.000,00 €");

        Assert.Null(draft.Snapshot.ProjectedCapitalAtRetirement);
        Assert.Null(draft.Snapshot.ProjectionReturnPercent);
        Assert.Null(draft.Snapshot.ProjectionBasis);
        Assert.Contains("snapshot.projectedCapitalAtRetirement", draft.Unresolved);
        // The balance is unaffected: one unreadable forecast does not cost the document its value.
        Assert.Equal(10000m, draft.Snapshot.Balance);
    }

    // ------------------------------------------------------------- money direction

    /// <summary>
    /// The three shares are different money (docs/PENSION.md, "Money direction") and the printed total
    /// is a fourth fact. 100 + 15 + 50 is 165 and the document says 180; nothing here picks a winner.
    /// </summary>
    [Fact]
    public void The_shares_and_the_printed_total_are_both_kept_even_when_they_disagree()
    {
        var draft = Parse(Statement);

        Assert.NotNull(draft.Contribution);
        Assert.Equal(100m, draft.Contribution.EmployeeAmount);
        Assert.Equal(15m, draft.Contribution.EmployerSubsidyAmount);
        Assert.Equal(50m, draft.Contribution.EmployerAmount);
        Assert.Equal(180m, draft.Contribution.StatedTotalAmount);
        Assert.Equal(BavContributionCycles.Monthly, draft.Contribution.Cycle);
    }

    // ---------------------------------------------------------------- beitragsfrei

    [Fact]
    public void Beitragsfrei_is_paid_up_and_not_terminated()
    {
        var draft = Parse("Der Vertrag ist seit dem 01.06.2023 beitragsfrei gestellt.\nVertragsguthaben: 8.000,00 €");

        Assert.Equal(BavContractStatuses.PaidUp, draft.Contract.Status);
        Assert.True(BavContractStatuses.HoldsCapital(draft.Contract.Status));
    }

    /// <summary>
    /// "Beitragsfreie Versicherungssumme" is printed on statements of perfectly active contracts - it
    /// is what the contract would still pay if payments stopped. Reading it as a status would switch a
    /// running contract to beitragsfrei, so the word only counts where it stands on its own.
    /// </summary>
    [Fact]
    public void A_beitragsfreie_Versicherungssumme_does_not_make_a_contract_paid_up()
    {
        var draft = Parse("Vertragsguthaben: 8.000,00 €\nBeitragsfreie Versicherungssumme: 42.000,00 €");

        Assert.Null(draft.Contract.Status);
        Assert.Contains("contract.status", draft.Unresolved);
    }

    // ------------------------------------------------------------------- funds

    [Fact]
    public void A_fund_line_yields_its_isin_weight_and_charges()
    {
        var draft = Parse(
            "Aufteilung des Fondsvermögens\n" +
            "Aktienfonds Welt Index, IE00B4L5Y983, Anteil 45,00 %, laufende Kosten des Fonds 0,22 % p. a., Wert 4.500,00 €");

        var allocation = Assert.Single(draft.Allocations);
        Assert.Equal("Aktienfonds Welt Index", allocation.FundName);
        Assert.Equal("IE00B4L5Y983", allocation.Isin);
        Assert.Equal(45.00m, allocation.WeightPercent);
        Assert.Equal(0.22m, allocation.OngoingChargesPercent);
        Assert.Equal(4500m, allocation.Amount);
        Assert.Equal(BavAssetClasses.Equity, allocation.AssetClass);
    }

    // -------------------------------------------------------------------- costs

    [Fact]
    public void The_cost_block_is_read_as_kind_basis_and_timing()
    {
        var draft = Parse("""
            Kosten Ihres Vertrages
            Effektivkosten (Renditeminderung): ca. 1,15 % p. a.
            Abschluss- und Vertriebskosten: 2.400,00 €
            Verwaltungskosten: 2,50 % der Beiträge
            Stückkosten: 12,00 € jährlich
            """);

        var effective = Assert.Single(draft.Costs, cost => cost.Kind == BavCostKinds.Other);
        Assert.Equal(1.15m, effective.Percent);
        Assert.Equal(BavCostBases.PercentOfCapital, effective.Basis);
        // "ca." makes it an approximation, and an estimate is only storable with its basis.
        Assert.True(effective.IsEstimated);
        Assert.False(string.IsNullOrWhiteSpace(effective.EstimateBasis));

        var acquisition = Assert.Single(draft.Costs, cost => cost.Kind == BavCostKinds.Acquisition);
        Assert.Equal(2400m, acquisition.Amount);
        Assert.Equal(BavCostBases.FixedAmount, acquisition.Basis);
        Assert.Equal(BavCostTimings.Incurred, acquisition.Timing);
        Assert.False(acquisition.IsEstimated);

        // A cost taken from the contribution is zero once no contribution is paid - which is the one
        // part of "beitragsfrei ist nicht kostenfrei" that is arithmetic rather than an assumption.
        var administration = Assert.Single(draft.Costs, cost => cost.Kind == BavCostKinds.AdministrationOnContribution);
        Assert.Equal(2.50m, administration.Percent);
        Assert.False(administration.ContinuesWhenPaidUp);

        var fixedCost = Assert.Single(draft.Costs, cost => cost.Kind == BavCostKinds.AdministrationFixed);
        Assert.Equal(12m, fixedCost.Amount);
        Assert.True(fixedCost.ContinuesWhenPaidUp);
    }

    // -------------------------------------------------------------------- route

    [Theory]
    [InlineData("Direktversicherung", BavImplementationRoutes.DirectInsurance)]
    [InlineData("Pensionskasse", BavImplementationRoutes.PensionFund)]
    [InlineData("Pensionsfonds", BavImplementationRoutes.PensionScheme)]
    [InlineData("Unterstützungskasse", BavImplementationRoutes.ProvidentFund)]
    [InlineData("Direktzusage", BavImplementationRoutes.DirectCommitment)]
    public void The_route_word_decides_the_route(string printed, string expected)
    {
        var draft = Parse($"Durchführungsweg: {printed}\nVertragsguthaben: 1.000,00 €");

        Assert.Equal(expected, draft.Contract.ImplementationRoute);
    }

    // ------------------------------------------------------------------- locale

    /// <summary>
    /// The whole reason <see cref="Modules.Parity.ImportNumber"/> and
    /// <see cref="Modules.Parity.ImportDate"/> exist: the same statement may not read differently on a
    /// German desktop, an American one and the invariant culture a container runs with. Comparing the
    /// serialised drafts checks every field at once, including the ones no other test looks at.
    /// </summary>
    [Fact]
    public void The_same_statement_produces_the_same_draft_under_every_culture()
    {
        string Under(string culture)
        {
            using var scope = new CultureScope(culture);
            return JsonSerializer.Serialize(Parse(Statement));
        }

        var german = Under("de-DE");
        Assert.Equal(german, Under("en-US"));
        Assert.Equal(german, Under(string.Empty));
        // And the values themselves are the German reading, not a coincidence of two wrong answers.
        using var _ = new CultureScope("en-US");
        var draft = Parse(Statement);
        Assert.Equal(12345.67m, draft.Snapshot.Balance);
        Assert.Equal(new DateOnly(2025, 12, 31), draft.Snapshot.EffectiveDate);
        Assert.Equal(new DateOnly(2049, 8, 1), draft.Contract.RetirementDate);
    }

    // --------------------------------------------------------------- provenance

    [Fact]
    public void A_filled_field_carries_the_page_it_was_found_on_and_the_label_it_matched()
    {
        var draft = Parse(
            "Allianz Lebensversicherungs-AG\nVersicherungsnummer: 4711/8899",
            "Standmitteilung zum 31.12.2025\nVertragsguthaben: 12.345,67 €");

        var balance = Assert.Single(draft.Provenance, entry => entry.Field == "snapshot.balance");
        Assert.Equal(2, balance.Page);
        Assert.Equal("Vertragsguthaben", balance.MatchedLabel);
        Assert.Equal(BavExtractionSources.Deterministic, balance.Source);
        Assert.InRange(balance.Confidence, 0.5m, 1m);
    }

    /// <summary>
    /// The policy number is read - the match rules need it - and it appears nowhere else. A provenance
    /// entry carries the document's vocabulary, never its content, which is what keeps personal data
    /// out of the one part of the draft a diagnostic surface would happily print.
    /// </summary>
    [Fact]
    public void No_provenance_entry_carries_the_policy_number()
    {
        var draft = Parse(Statement);

        Assert.Equal("4711/8899", draft.Contract.PolicyNumber);
        Assert.All(draft.Provenance, entry =>
        {
            Assert.DoesNotContain("4711", entry.MatchedLabel ?? string.Empty);
            Assert.DoesNotContain("4711", entry.Field);
        });
    }

    // ------------------------------------------------------------ nothing to read

    [Fact]
    public void An_empty_document_is_an_empty_draft_with_no_confidence()
    {
        var draft = Parse(string.Empty);

        Assert.Equal(0m, draft.Confidence);
        Assert.Null(draft.Snapshot.Balance);
        Assert.Null(draft.Snapshot.EffectiveDate);
        Assert.Empty(draft.Provenance);
        Assert.Equal(BavExtractionSources.Deterministic, draft.Source);
    }

    [Fact]
    public void Text_that_says_nothing_invents_nothing()
    {
        var draft = Parse("Lorem ipsum dolor sit amet\nconsetetur sadipscing elitr sed diam");

        Assert.Equal(0m, draft.Confidence);
        Assert.Null(draft.Snapshot.Balance);
        Assert.Null(draft.Snapshot.EffectiveDate);
        Assert.Null(draft.Snapshot.Currency);
        Assert.Null(draft.Contract.ProviderName);
        Assert.Null(draft.Contribution);
        Assert.Empty(draft.Allocations);
        Assert.Empty(draft.Costs);
        Assert.Empty(draft.Provenance);
        Assert.Contains("snapshot.balance", draft.Unresolved);
    }

    /// <summary>
    /// The confidence is about the three fields a snapshot needs. A statement that states them all is
    /// high; garbage is zero. Anything in between is a review screen with questions on it.
    /// </summary>
    [Fact]
    public void The_confidence_describes_the_snapshot_and_not_the_field_count()
    {
        Assert.InRange(Parse(Statement).Confidence, 0.7m, 1m);
        // A cost block with no date and no balance is not a snapshot: all that page contributes is a
        // currency, which is 0.2 of the weight at a derived 0.6 - and nothing else raises it.
        Assert.Equal(0.12m, Parse("Kosten Ihres Vertrages\nStückkosten: 12,00 € jährlich").Confidence);
        Assert.Equal(0m, Parse("Kosten Ihres Vertrages\nStückkosten: 12,00 jährlich").Confidence);
    }

    // ----------------------------------------------------------- the optional AI

    /// <summary>
    /// Without a Codex bridge the pass is a no-op: a self-hosted installation reaches the same review
    /// screen, only with more fields left to type.
    /// </summary>
    [Fact]
    public async Task A_disabled_structurer_returns_the_draft_it_was_given()
    {
        var structurer = Structurer();
        var draft = Parse(Statement);

        var enriched = await structurer.EnrichAsync(Guid.NewGuid(), draft, new BavDocumentText([Statement], true), default);

        Assert.False(structurer.IsEnabled);
        Assert.Same(draft, enriched);
    }

    /// <summary>
    /// The merge may only fill. A model that answers with a different balance - because it hallucinated
    /// one, or because the document really is ambiguous - cannot move the number the parser read off a
    /// label.
    /// </summary>
    [Fact]
    public void A_model_answer_cannot_overwrite_a_deterministic_value()
    {
        var draft = Parse(Statement);
        Assert.Equal(12345.67m, draft.Snapshot.Balance);

        var merged = PensionDocumentCodexStructurer.Merge(draft, new BavCodexStatement(
            Contract: new BavCodexContract("Muster Lebensversicherung AG", null, null, null, null, null, null, null, null, null, null, null, null),
            Snapshot: new BavCodexSnapshot(null, null, 99_999m, null, null, null, null, null, null, null, null, null),
            Contribution: null,
            Allocations: null,
            Costs: null,
            Page: 1,
            Confidence: 0.9m));

        Assert.Equal(12345.67m, merged.Snapshot.Balance);
        // ... while a field no label could fill is filled, and says so.
        Assert.Equal("Muster Lebensversicherung AG", merged.Contract.ProviderName);
        var provider = Assert.Single(merged.Provenance, entry => entry.Field == "contract.providerName");
        Assert.Equal(BavExtractionSources.Codex, provider.Source);
        Assert.Null(provider.MatchedLabel);
        // A model's field never outranks a matched label on the review screen.
        Assert.True(provider.Confidence < 0.9m);
        Assert.DoesNotContain("contract.providerName", merged.Unresolved);
    }

    /// <summary>
    /// The projection rule applies to model output too, and for the same reason: without the assumed
    /// return the figure cannot be committed, so accepting it would produce a draft that looks
    /// extracted and fails on save.
    /// </summary>
    [Fact]
    public void A_model_forecast_without_its_return_is_rejected_rather_than_merged()
    {
        var draft = Parse("Stand: 31.12.2025\nVertragsguthaben: 10.000,00 €");

        var merged = PensionDocumentCodexStructurer.Merge(draft, new BavCodexStatement(
            Contract: null,
            Snapshot: new BavCodexSnapshot(null, null, null, null, null, null, null, null, null,
                ProjectedCapitalAtRetirement: 80_000m, ProjectedMonthlyAnnuity: 300m, ProjectionReturnPercent: null),
            Contribution: null,
            Allocations: null,
            Costs: null,
            Page: 1,
            Confidence: 0.8m));

        Assert.Null(merged.Snapshot.ProjectedCapitalAtRetirement);
        Assert.Null(merged.Snapshot.ProjectedMonthlyAnnuity);
        Assert.Null(merged.Snapshot.ProjectionBasis);
        Assert.Contains("snapshot.projectedCapitalAtRetirement", merged.Unresolved);
        Assert.Contains("snapshot.projectedMonthlyAnnuity", merged.Unresolved);
    }

    /// <summary>With the return stated, the basis is the document's forecast - never the model's word for it.</summary>
    [Fact]
    public void A_model_forecast_with_its_return_is_merged_as_a_document_forecast()
    {
        var draft = Parse("Stand: 31.12.2025\nVertragsguthaben: 10.000,00 €");

        var merged = PensionDocumentCodexStructurer.Merge(draft, new BavCodexStatement(
            Contract: null,
            Snapshot: new BavCodexSnapshot(null, null, null, null, null, null, null, null, null,
                ProjectedCapitalAtRetirement: 80_000m, ProjectedMonthlyAnnuity: null, ProjectionReturnPercent: 4m),
            Contribution: null,
            Allocations: null,
            Costs: null,
            Page: 2,
            Confidence: 0.8m));

        Assert.Equal(80_000m, merged.Snapshot.ProjectedCapitalAtRetirement);
        Assert.Equal(4m, merged.Snapshot.ProjectionReturnPercent);
        Assert.Equal(BavProjectionBases.DocumentForecast, merged.Snapshot.ProjectionBasis);
        Assert.Equal(BavExtractionSources.Codex, merged.Source);
    }

    private static PensionDocumentCodexStructurer Structurer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite("Filename=:memory:").Options;
        return new PensionDocumentCodexStructurer(
            configuration,
            clients: null!,
            new CodexModelResolver(new IntelligenceDbContext(options)),
            NullLogger<PensionDocumentCodexStructurer>.Instance);
    }

    /// <summary>Same pattern as <c>ImportDateTests</c>: the answer may not depend on the machine.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) =>
            CultureInfo.CurrentCulture = name.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
