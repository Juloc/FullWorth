using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>Das Aenderungsprotokoll, der Abgleich mit den Abrechnungen und der Notbehelf-Parser - ohne Datenbank.</summary>
public sealed class EmploymentContractLogicTests
{
    private static ContractStateView State(string validFrom, decimal gross, string kind = ContractKinds.Contract, decimal? hours = 40,
        decimal? vacation = 30, bool car = false, string? employer = "Beispiel GmbH", IReadOnlyList<ContractField>? extras = null, int confirmedDay = 1) =>
        new(Guid.NewGuid(), Guid.Empty,
            new ContractStateWrite(DateOnly.Parse(validFrom), kind, gross, employer, hours, vacation, car, null, "3 Monate", null, extras),
            new DateTimeOffset(2026, 11, confirmedDay, 9, 0, 0, TimeSpan.Zero), null);

    [Fact]
    public void TheFirstStateIsTheStart_AndNewestComesFirst()
    {
        var events = ContractChangeLog.Build([State("2026-07-01", 5400, ContractKinds.Addendum), State("2025-01-01", 5000)]);

        Assert.Equal(["Gehaltserhöhung", "Start"], events.Select(x => x.Badge));
        var start = events[1];
        Assert.Equal("Arbeitsvertrag Beispiel GmbH", start.Title);
        Assert.All(start.Changed, x => Assert.Null(x.Old));
        Assert.Empty(start.Unchanged);
    }

    [Fact]
    public void OnlyChangedFieldsAreChanged_TheRestIsUnchanged_AndTheSalaryCarriesThePercentage()
    {
        var events = ContractChangeLog.Build([State("2025-01-01", 5000), State("2026-07-01", 5400, ContractKinds.Addendum, vacation: 28)]);

        var latest = events[0];
        Assert.Equal(["gross", "vacation"], latest.Changed.Select(x => x.Key));
        var gross = latest.Changed.Single(x => x.Key == "gross");
        Assert.Equal("5.000,00 €", gross.Old);
        Assert.Equal("5.400,00 €", gross.New);
        Assert.Equal(8.0m, gross.Percent);
        Assert.Null(latest.Changed.Single(x => x.Key == "vacation").Percent);
        Assert.Contains(latest.Unchanged, x => x is { Key: "hours", Value: "40 h" });
        Assert.DoesNotContain(latest.Unchanged, x => x.Key == "gross");
    }

    [Fact]
    public void AnAddendumWithoutASalaryChangeIsNamedAfterWhatChanged()
    {
        Assert.Equal("Benefits", ContractChangeLog.Build([State("2025-01-01", 5000), State("2026-01-01", 5000, car: true)])[0].Badge);
        Assert.Equal("Arbeitszeit", ContractChangeLog.Build([State("2025-01-01", 5000), State("2026-01-01", 5000, hours: 32)])[0].Badge);
        Assert.Equal("Gehaltsänderung", ContractChangeLog.Build([State("2025-01-01", 5000), State("2026-01-01", 4500)])[0].Badge);
        Assert.Equal("Nachtrag", ContractChangeLog.Build([State("2025-01-01", 5000), State("2026-01-01", 5000, ContractKinds.Addendum, employer: "Neu GmbH")])[0].Badge);
    }

    [Fact]
    public void ANamedExtraFieldIsComparedByItsLabel_AndARemovedOneShowsAsChanged()
    {
        var events = ContractChangeLog.Build([
            State("2025-01-01", 5000, extras: [new("Probezeit", "6 Monate"), new("Standort", "Berlin")]),
            State("2026-01-01", 5000, extras: [new("Standort", "Berlin"), new("Bonus", "10 %")])]);

        var latest = events[0];
        Assert.Contains(latest.Changed, x => x is { Label: "Bonus", Old: null, New: "10 %" });
        Assert.Contains(latest.Changed, x => x is { Label: "Probezeit", Old: "6 Monate", New: null });
        Assert.Contains(latest.Unchanged, x => x.Label == "Standort");
    }

    private static PayslipRecordView Payslip(string period, decimal gross, decimal bonus = 0, decimal car = 0) =>
        new(Guid.NewGuid(), Guid.Empty, new PayslipRecordWrite(DateOnly.Parse(period), gross, gross * 0.6m, gross * 0.6m, Bonus: bonus, CompanyCarTaxableBenefit: car), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void ARaiseWithoutAnAddendumIsRecognisedFromTheMonthItStarted()
    {
        var hints = ContractReconciliation.Reconcile([State("2025-01-01", 5000)],
            [Payslip("2026-05-31", 5000), Payslip("2026-06-30", 5000), Payslip("2026-07-31", 5400), Payslip("2026-08-31", 5400)]);

        var hint = Assert.Single(hints);
        Assert.Equal("change_detected", hint.Kind);
        Assert.Equal(new DateOnly(2026, 7, 1), hint.SuggestedValidFrom);
        Assert.Equal(5400m, hint.SuggestedGross);
    }

    [Fact]
    public void ASingleDeviatingMonthIsOnlyMentioned_AndBonusAndCompanyCarDoNotCount()
    {
        var hints = ContractReconciliation.Reconcile([State("2025-01-01", 5000)],
            [Payslip("2026-05-31", 5000), Payslip("2026-06-30", 4200), Payslip("2026-07-31", 6500, bonus: 1500), Payslip("2026-08-31", 5250, car: 250), Payslip("2026-09-30", 5000)]);

        var hint = Assert.Single(hints);
        Assert.Equal("differs", hint.Kind);
        Assert.Null(hint.SuggestedValidFrom);
        Assert.Contains("Juni 2026", hint.Message);
    }

    [Fact]
    public void NothingIsComparedBeforeTheFirstContractState_OrWithoutAnyState()
    {
        Assert.Empty(ContractReconciliation.Reconcile([State("2026-06-01", 5000)], [Payslip("2026-01-31", 3000), Payslip("2026-02-28", 3000)]));
        Assert.Empty(ContractReconciliation.Reconcile([], [Payslip("2026-01-31", 3000)]));
    }

    [Fact]
    public void TheParserFindsWhatAKeywordPinsDown_AndLeavesTheRestEmpty()
    {
        var result = ContractTextParser.Parse("""
            Änderungsvereinbarung zum Arbeitsvertrag
            zwischen der Beispiel Software GmbH und Herrn Mustermann
            Mit Wirkung zum 01.07.2026 beträgt das monatliche Bruttogehalt 5.400,00 €.
            Die regelmäßige Arbeitszeit beträgt 40 Wochenstunden.
            Der Urlaubsanspruch beträgt 30 Arbeitstage pro Kalenderjahr.
            Die Kündigungsfrist beträgt 3 Monate zum Monatsende.
            Ein Dienstwagen wird zur privaten Nutzung überlassen.
            """);

        Assert.Equal(new DateOnly(2026, 7, 1), result.ValidFrom);
        Assert.Equal(ContractKinds.Addendum, result.Kind);
        Assert.Equal(5400m, result.GrossMonthly);
        Assert.Equal(40m, result.WeeklyHours);
        Assert.Equal(30m, result.VacationDays);
        Assert.True(result.CompanyCar);
        Assert.Equal("Beispiel Software GmbH", result.Employer);
        Assert.StartsWith("3 Monate", result.NoticePeriod);
        Assert.Null(result.BavEmployerShare);
        Assert.True(result.ConfidencePercent >= 70m);
    }

    [Fact]
    public void AnAnnualSalaryIsDividedByTwelveAndSaysSo()
    {
        var result = ContractTextParser.Parse("Arbeitsvertrag. Das Jahresbruttogehalt beträgt 60.000,00 €. Beginn des Arbeitsverhältnisses: 01.03.2024");

        Assert.Equal(5000m, result.GrossMonthly);
        Assert.Contains(result.Warnings, x => x.Contains("Jahresgehalt"));
        Assert.Equal(new DateOnly(2024, 3, 1), result.ValidFrom);
    }

    // Wie ein echter Nachtrag formuliert: das Gehalt steht im Satz, mal als "3.240.-", mal als "3.100,-",
    // und zwischen Zahl und "Euro" kann ein Zeilenumbruch stehen. Alle Zahlen sind erfunden.
    [Theory]
    [InlineData("Herr Muster erhält ab 01.01.2026 eine Grundgehaltserhöhung auf brutto 3.240.- Euro.", 3240, 2026, 1, 1)]
    [InlineData("Herr Muster erhält ab 01.01.2025 eine Grundgehaltserhöhung auf brutto 3.100,- Euro.", 3100, 2025, 1, 1)]
    [InlineData("Herr Muster erhält ab 01.08.2023 eine Grundgehaltserhöhung auf brutto 2.750.-\nEuro. Das entspricht", 2750, 2023, 8, 1)]
    [InlineData("Herr Muster erhält ab 01.01.2024 eine Grundgehaltserhöhung auf brutto 2.900.\nEuro.", 2900, 2024, 1, 1)]
    public void Parser_ReadsTheNewSalaryFromARaiseSentence(string text, int gross, int year, int month, int day)
    {
        var result = ContractTextParser.Parse("Nachtrag zum Anstellungsvertrag\n§ 3a: Vergütung:\n" + text);

        Assert.Equal(gross, result.GrossMonthly);
        Assert.Equal(new DateOnly(year, month, day), result.ValidFrom);
        Assert.Equal(ContractKinds.Addendum, result.Kind);
    }
}
