namespace FullWorth.Backend.Modules.Compensation;

public sealed record CompensationInsightRequest(
    CompensationProfileInput Profile,
    decimal EmployerBudgetMonthly = 300m);

public sealed record CompensationInsightOption(
    string Key,
    string Title,
    string Description,
    CompensationProfileInput Profile,
    CompensationCalculationResult Calculation,
    decimal CashNetDeltaAnnual,
    decimal FullWorthDeltaAnnual,
    decimal EmployerCostDeltaAnnual);

public sealed record CompensationInsightResult(
    CompensationCalculationResult Current,
    IReadOnlyList<CompensationInsightOption> SalaryRaises,
    IReadOnlyList<CompensationInsightOption> PartTime,
    IReadOnlyList<CompensationInsightOption> EmployerBudgetOptions);

public static class CompensationInsights
{
    public static CompensationInsightResult Analyze(CompensationInsightRequest request)
    {
        if (request.EmployerBudgetMonthly < 0m || request.EmployerBudgetMonthly > 100_000m)
            throw new ArgumentOutOfRangeException(nameof(request.EmployerBudgetMonthly));

        var current = GermanCompensationCalculator.Calculate(request.Profile);
        var salaryRaises = new[] { 3m, 5m, 10m }
            .Select(percent => CreateOption(
                $"raise-{percent:0}",
                $"+{percent:0} % Brutto",
                "Nominale Gehaltserhöhung bei unveränderten Wochenstunden und Benefits.",
                current,
                request.Profile with
                {
                    Name = $"{request.Profile.Name} +{percent:0}%",
                    AnnualGross = Round(request.Profile.AnnualGross * (1m + percent / 100m))
                }))
            .ToArray();

        var partTime = new[] { 90m, 80m }
            .Select(percent =>
            {
                var factor = percent / 100m;
                return CreateOption(
                    $"part-time-{percent:0}",
                    $"{percent:0} % Arbeitszeit",
                    "Vereinfachte proportionale Reduktion von Fixgehalt und Wochenstunden; Benefits bleiben unverändert.",
                    current,
                    request.Profile with
                    {
                        Name = $"{request.Profile.Name} {percent:0}%",
                        AnnualGross = Round(request.Profile.AnnualGross * factor),
                        WeeklyHours = Math.Round(request.Profile.WeeklyHours * factor, 2)
                    });
            })
            .ToArray();

        var annualBudget = request.EmployerBudgetMonthly * 12m;
        var grossIncrease = SolveGrossIncreaseForEmployerBudget(request.Profile, current, annualBudget);
        var grossProfile = request.Profile with
        {
            Name = "AG-Budget als Brutto",
            AnnualGross = Round(request.Profile.AnnualGross + grossIncrease)
        };

        var pension = request.Profile.OccupationalPension ?? new OccupationalPensionInput();
        var bavProfile = request.Profile with
        {
            Name = "AG-Budget als bAV",
            OccupationalPension = pension with
            {
                EmployerContributionMonthly = pension.EmployerContributionMonthly + request.EmployerBudgetMonthly
            }
        };

        var benefits = (request.Profile.Benefits ?? Array.Empty<CompensationBenefitInput>()).ToList();
        benefits.Add(new CompensationBenefitInput(
            "Zusätzlicher steuerfreier Benefit (Simulation)",
            EmployerCostMonthly: request.EmployerBudgetMonthly,
            PersonalValueMonthly: request.EmployerBudgetMonthly));
        var benefitProfile = request.Profile with
        {
            Name = "AG-Budget als Benefit",
            Benefits = benefits
        };

        var employerBudgetOptions = new[]
        {
            CreateOption("budget-gross", "Mehr Brutto", "Das Arbeitgeberbudget wird inklusive zusätzlicher Arbeitgeber-Sozialbeiträge in Brutto umgerechnet.", current, grossProfile),
            CreateOption("budget-bav", "Arbeitgeber-bAV", "Das gesamte Budget wird als zusätzlicher Arbeitgeberbeitrag zur bAV simuliert.", current, bavProfile),
            CreateOption("budget-benefit", "Steuerfreier Benefit", "Vergleichssimulation: Das Budget wird als vollständig steuerfreier Benefit mit gleichem persönlichem Wert modelliert. Die konkrete steuerliche Zulässigkeit hängt vom Benefit ab.", current, benefitProfile)
        }.OrderByDescending(option => option.FullWorthDeltaAnnual).ToArray();

        return new CompensationInsightResult(current, salaryRaises, partTime, employerBudgetOptions);
    }

    private static CompensationInsightOption CreateOption(
        string key,
        string title,
        string description,
        CompensationCalculationResult current,
        CompensationProfileInput profile)
    {
        var calculation = GermanCompensationCalculator.Calculate(profile);
        return new CompensationInsightOption(
            key,
            title,
            description,
            profile,
            calculation,
            Round(calculation.EstimatedCashNetAnnual - current.EstimatedCashNetAnnual),
            Round(calculation.FullWorthCompensationValueAnnual - current.FullWorthCompensationValueAnnual),
            Round(calculation.EmployerTotalCostAnnual - current.EmployerTotalCostAnnual));
    }

    private static decimal SolveGrossIncreaseForEmployerBudget(
        CompensationProfileInput profile,
        CompensationCalculationResult current,
        decimal annualBudget)
    {
        if (annualBudget <= 0m) return 0m;
        var low = 0m;
        var high = annualBudget;
        for (var i = 0; i < 40; i++)
        {
            var mid = (low + high) / 2m;
            var candidate = GermanCompensationCalculator.Calculate(profile with { AnnualGross = profile.AnnualGross + mid });
            var employerDelta = candidate.EmployerTotalCostAnnual - current.EmployerTotalCostAnnual;
            if (employerDelta > annualBudget) high = mid; else low = mid;
        }
        return low;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
