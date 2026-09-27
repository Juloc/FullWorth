namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Der anbieterneutrale Dienstrad-/JobRad-Rechner (Issue #179): keine zweite Steuer-/SV-Engine, sondern zwei
/// Aufrufe der bestehenden <see cref="GermanCompensationCalculator"/> - einmal ohne, einmal mit dem Rad im
/// Profil - und der Unterschied zwischen beiden ist die Antwort. Die einzige neue Rechenlogik hier betrifft,
/// was die allgemeine Gehaltsberechnung naturgemäß nicht kennt: die 0,25-%-Regel selbst, den Leasing-/Barkauf-
/// Vergleich und die Rentenpunkte.
/// </summary>
public static class JobRadCalculator
{
    public static JobRadResult Compare(JobRadComparisonRequest request)
    {
        var leasing = request.Leasing;
        var year = TaxYearTable.Resolve(request.Salary);

        var without = GermanCompensationCalculator.Calculate(request.Salary);
        var withJobRad = GermanCompensationCalculator.Calculate(WithJobRadProfile(request.Salary, leasing));
        var comparison = GermanCompensationCalculator.Compare(new(Left: request.Salary, Right: WithJobRadProfile(request.Salary, leasing)));

        // Netto pro Monat: die Jahresdifferenz auf einen normalen, durchgehend beschäftigten Monat verteilt -
        // dieselbe 12-Monats-Basis wie "Netto normaler Monat" im übrigen Compensation-Modul, unabhängig von
        // einem unterjährigen Beschäftigungszeitraum, den ein Dienstrad-Vergleich nicht mitmeint.
        var netMonthlyImpact = Round(comparison.CashNetDeltaAnnual / 12m);

        var (totalLeaseCost, cashPurchaseCost, difference) = CostComparison(leasing, request.CashAlternative);
        var pension = PensionImpact(request.Salary, leasing, year, without, withJobRad);
        var benefits = SocialBenefitEstimates(request.Salary, without, withJobRad);

        // Der Arbeitgeber zahlt hier nur seinen SV-Anteil auf das umgewandelte Entgelt und auf den geldwerten
        // Vorteil - die Leasingrate selbst geht an den Leasinggeber und steht schon oben in "Netto pro Monat"
        // (über das umgewandelte Gehalt) und in den Gesamtkosten, nicht noch einmal hier.
        var savings = Round(-comparison.EmployerCostDeltaAnnual / 12m);
        var employer = new JobRadEmployerImpact(savings, leasing.EmployerSubsidyMonthly, Round(savings - leasing.EmployerSubsidyMonthly));

        return new JobRadResult(
            Round(leasing.MonthlyTaxableBenefit),
            netMonthlyImpact,
            Round(totalLeaseCost),
            Round(cashPurchaseCost),
            Round(difference),
            pension,
            benefits,
            employer,
            without,
            withJobRad,
            without.Assumptions);
    }

    /// <summary>
    /// Das Gehaltsprofil MIT dem Rad - reine Eingabekonstruktion, keine neue Engine-Logik. Gehaltsumwandlung
    /// senkt das Bruttogehalt um den vom Arbeitnehmer tatsächlich umgewandelten Anteil (Rate abzüglich
    /// Arbeitgeberzuschuss - der Zuschuss ist das Geld des Arbeitgebers, nicht das des Arbeitnehmers); der
    /// geldwerte Vorteil kommt über <see cref="CompensationBenefitInput.TaxableBenefitMonthly"/> zurück in die
    /// Steuer- UND SV-Bemessungsgrundlage (§ 8 Abs. 2 EStG i. V. m. § 1 SvEV - beides, nicht nur die Steuer).
    /// Gehaltsextra (§ 3 Nr. 37 EStG) rührt das Bruttogehalt gar nicht erst an: es ist ohnehin steuer- und
    /// SV-frei. Separat abgerechnete Versicherung/Service ist echter Netto-Aufwand in beiden Fällen.
    /// </summary>
    private static CompensationProfileInput WithJobRadProfile(CompensationProfileInput salary, JobRadLeasingInput leasing)
    {
        var employeeConverted = Math.Max(0m, leasing.MonthlyLeasingRateGross - leasing.EmployerSubsidyMonthly);
        var reducedGross = leasing.SalaryConversion ? Math.Max(0m, salary.AnnualGross - employeeConverted * 12m) : salary.AnnualGross;

        var jobRadBenefit = new CompensationBenefitInput(
            "JobRad",
            EmployerCostMonthly: 0m,
            PersonalValueMonthly: 0m,
            TaxableBenefitMonthly: leasing.MonthlyTaxableBenefit,
            EmployeeCostMonthly: leasing.EmployeeInsuranceServiceMonthly);

        return salary with
        {
            AnnualGross = reducedGross,
            Benefits = [.. salary.Benefits ?? [], jobRadBenefit]
        };
    }

    private static (decimal TotalLeaseCost, decimal CashPurchaseCost, decimal Difference) CostComparison(
        JobRadLeasingInput leasing, JobRadCashPurchaseInput cash)
    {
        // Die Nettobelastung durch die Umwandlung wirkt schon im Netto-pro-Monat oben; hier zählt nur, was
        // TATSÄCHLICH fließt: die volle Rate über die Laufzeit, gegebenenfalls die Versicherung/Service
        // obendrauf, minus Arbeitgeberzuschuss, plus der Übernahmepreis am Ende (nur wenn der Anbieter dessen
        // Versteuerung NICHT übernimmt, kommt der eigene, nicht schon anderswo gezählte Steueranteil dazu -
        // eine grobe, klar benannte Schätzung mit dem persönlichen Grenzsteuersatz ist hier bewusst nicht
        // eingerechnet, weil sie den Anbietervergleich mit einer Fußnote verkompliziert, die nichts an der
        // Kaufentscheidung ändert).
        var ownRate = Math.Max(0m, leasing.MonthlyLeasingRateGross - leasing.EmployerSubsidyMonthly);
        var totalLease = ownRate * leasing.TermMonths
            + leasing.EmployeeInsuranceServiceMonthly * leasing.TermMonths
            + leasing.TakeoverPrice;

        var years = leasing.TermMonths / 12m;
        var totalCash = cash.ActualPurchasePrice + cash.FinancingCostTotal
            + cash.AnnualInsuranceCost * years + cash.AnnualServiceCost * years;

        return (totalLease, totalCash, totalCash - totalLease);
    }

    /// <summary>
    /// Entgeltpunkte (§ 63, § 70 SGB VI): beitragspflichtiges Bruttoentgelt (gekappt an der RV-Beitragsbemessungs-
    /// grenze) geteilt durch das Durchschnittsentgelt desselben Jahres. Der Rentenbeitrag selbst ist schon
    /// gekappt zurückgerechnet - <see cref="SocialInsuranceBreakdown.PensionAnnual"/> geteilt durch den halben
    /// Beitragssatz ergibt exakt die gekappte Bemessungsgrundlage zurück, ohne die Kappung hier ein zweites Mal
    /// nachzubilden (und ohne dafür ein weiteres Feld quer durch die bestehende Engine zu ziehen).
    /// </summary>
    private static JobRadPensionImpact PensionImpact(
        CompensationProfileInput salary, JobRadLeasingInput leasing, TaxYearParameters year,
        CompensationCalculationResult without, CompensationCalculationResult withJobRad)
    {
        if (!leasing.SalaryConversion || salary.PensionInsuranceEnabled == false)
            return new JobRadPensionImpact(0m, 0m, false, year.CurrentPensionValueMonthly, year.AverageEarningsAnnual);

        var baseWithout = PensionBase(without, year);
        var baseWith = PensionBase(withJobRad, year);
        var ceiling = year.PensionCeilingAnnual(salary.StateCode);
        // Massgeblich ist, ob das Entgelt AUCH NACH der Umwandlung noch an der Grenze liegt - dann hat die
        // Kappung, nicht die Umwandlung, das Entgelt bestimmt, und es entsteht kein Rentenverlust. Lag es
        // ohne die Umwandlung an der Grenze und faellt durch sie erstmals darunter, ist das ein echter,
        // wenn auch kleiner, Verlust - genau den bildet deltaBase unten unabhaengig von dieser Kennzeichnung ab.
        var aboveCeiling = baseWith >= ceiling - 0.01m;

        var deltaBase = Math.Max(0m, baseWithout - baseWith);
        var lostPointsPerYear = deltaBase / year.AverageEarningsAnnual;
        var lostPointsOverTerm = lostPointsPerYear * (leasing.TermMonths / 12m);
        var monthlyReduction = lostPointsOverTerm * year.CurrentPensionValueMonthly;

        return new JobRadPensionImpact(
            Math.Round(lostPointsOverTerm, 4),
            Round(monthlyReduction),
            aboveCeiling,
            year.CurrentPensionValueMonthly,
            year.AverageEarningsAnnual);
    }

    private static decimal PensionBase(CompensationCalculationResult result, TaxYearParameters year) =>
        year.PensionEmployeeRate <= 0m ? 0m : result.SocialInsurance.PensionAnnual / year.PensionEmployeeRate;

    /// <summary>
    /// Krankengeld/ALG I/Elterngeld (Issue #179, Abschnitt 4): keine dritte Sozialleistungs-Engine, sondern der
    /// Delta-Ansatz, den das Issue selbst als ausreichend nennt - die Differenz im beitragspflichtigen
    /// Monatsentgelt mal der jeweiligen Ersatzquote. Jede Zahl ist ausdrücklich eine Schätzung
    /// (<see cref="JobRadBenefitEstimate.IsEstimate"/>) und <see cref="JobRadBenefitEstimate.Basis"/> nennt die
    /// verwendete Quote im Klartext, statt eine Genauigkeit zu behaupten, die die tatsächlichen
    /// Bemessungszeiträume, Deckel und Ausnahmen dieser Leistungen nicht hergeben.
    /// </summary>
    private static IReadOnlyList<JobRadBenefitEstimate> SocialBenefitEstimates(
        CompensationProfileInput salary, CompensationCalculationResult without, CompensationCalculationResult withJobRad)
    {
        var monthlyDelta = (without.CashGrossAnnual - withJobRad.CashGrossAnnual) / 12m;
        if (monthlyDelta <= 0m) return [];

        // § 149 SGB III: 67 % statt 60 %, wenn mindestens ein Kind im Sinne des Leistungsentgelts zu
        // berücksichtigen ist - hier über die ohnehin im Profil gepflegte Kinderzahl angenähert.
        var algRate = salary.ChildrenUnder25 > 0 ? 0.67m : 0.60m;

        return
        [
            new JobRadBenefitEstimate(
                "Krankengeld",
                Round(-monthlyDelta * 0.70m),
                true,
                "70 % des Bruttoentgeltausfalls (§ 47 SGB V) - der eigene 90-%-Nettodeckel bleibt unberücksichtigt"),
            new JobRadBenefitEstimate(
                "Arbeitslosengeld I",
                Round(-monthlyDelta * algRate),
                true,
                $"{algRate * 100m:0} % des pauschalierten Nettoentgeltausfalls (§ 149 SGB III), ohne die tatsächliche Leistungsgruppe"),
            new JobRadBenefitEstimate(
                "Elterngeld",
                Round(-monthlyDelta * 0.65m),
                true,
                "65 % des Nettoentgeltausfalls (§ 2 BEEG) - nur relevant, wenn die Umwandlung in den Bemessungszeitraum fällt")
        ];
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
