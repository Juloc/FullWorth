using System.Collections.Concurrent;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// The §32a EStG income-tax tariff ("Einkommensteuertarif") of one calendar year. The statute always uses the
/// same five-zone shape, so a single evaluation routine plus per-year coefficients covers every year — there is
/// deliberately no per-year calculation logic anywhere in this module.
///
/// Zone 1 = Grundfreibetrag (tax free), zones 2 and 3 are the two progressive zones, zone 4 is the 42 % zone and
/// zone 5 the 45 % ("Reichensteuer") zone.
/// </summary>
public sealed record IncomeTaxTariff(
    decimal BasicAllowance,
    decimal Zone2Upper,
    decimal Zone2Factor,
    decimal Zone2EntryRate,
    decimal Zone3Upper,
    decimal Zone3Factor,
    decimal Zone3EntryRate,
    decimal Zone3Base,
    decimal Zone4Upper,
    decimal Zone4Rate,
    decimal Zone4Subtrahend,
    decimal Zone5Rate,
    decimal Zone5Subtrahend)
{
    private static readonly ConcurrentDictionary<IncomeTaxTariff, decimal> ClassFiveW1Cache = new();

    /// <summary>§32a EStG: annual income tax on a rounded-down taxable income.</summary>
    public decimal Tax(decimal taxableIncome)
    {
        var x = Math.Floor(Math.Max(0m, taxableIncome));
        decimal tax;
        if (x <= BasicAllowance)
        {
            tax = 0m;
        }
        else if (x <= Zone2Upper)
        {
            var y = (x - BasicAllowance) / 10_000m;
            tax = (Zone2Factor * y + Zone2EntryRate) * y;
        }
        else if (x <= Zone3Upper)
        {
            var z = (x - Zone2Upper) / 10_000m;
            tax = (Zone3Factor * z + Zone3EntryRate) * z + Zone3Base;
        }
        else if (x <= Zone4Upper)
        {
            tax = Zone4Rate * x - Zone4Subtrahend;
        }
        else
        {
            tax = Zone5Rate * x - Zone5Subtrahend;
        }

        return Math.Round(Math.Max(0m, tax), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// §39b Abs. 2 Satz 7 EStG threshold "W1" for tax classes V and VI: below it the wage tax is the minimum of
    /// 14 % of the taxable income, above it the doubled tariff difference. The BMF publishes W1/W2/W3 per year,
    /// but they are fully determined by the year's §32a tariff, so they are derived here instead of maintaining a
    /// second, hand-copied table (deriving reproduces the published 2026 values 14.071 / 34.939 / 222.260).
    /// </summary>
    public decimal ClassFiveW1 => ClassFiveW1Cache.GetOrAdd(this, static tariff => tariff.DeriveClassFiveW1());

    /// <summary>§39b threshold "W2" — half of the upper end of the second progression zone.</summary>
    public decimal ClassFiveW2 => Math.Floor(Zone3Upper / 2m);

    /// <summary>§39b threshold "W3" — the 45 % zone entry divided by the 1,25 comparison factor.</summary>
    public decimal ClassFiveW3 => Math.Round(Zone4Upper / 1.25m, MidpointRounding.AwayFromZero);

    // W1 is the largest whole euro amount at which 14 % of the taxable income still reaches the doubled tariff
    // difference 2 · (T(1,25x) − T(0,75x)). The difference grows strictly faster than 0,14x, so the crossing is
    // unique and a bisection over [0, Zone2Upper] finds it exactly.
    private decimal DeriveClassFiveW1()
    {
        var entryRate = Zone2EntryRate / 10_000m;
        bool MinimumStillBinds(decimal x) => entryRate * x >= (Tax(x * 1.25m) - Tax(x * 0.75m)) * 2m;

        decimal low = 0m, high = Math.Ceiling(Zone2Upper);
        if (MinimumStillBinds(high)) return high;
        while (high - low > 1m)
        {
            var mid = Math.Floor((low + high) / 2m);
            if (mid <= low) break;
            if (MinimumStillBinds(mid)) low = mid; else high = mid;
        }
        return low;
    }
}

/// <summary>
/// Every statutory figure the German payroll calculation needs for one calendar year. Resolved once per
/// calculation from <see cref="CompensationProfileInput.TaxYear"/>, then threaded through the (single, shared)
/// formula so a 2019 snapshot is computed with 2019 law and never changes when a later year is added.
/// </summary>
public sealed record TaxYearParameters(
    int Year,
    IncomeTaxTariff IncomeTax,
    // Solidaritätszuschlag (SolzG 1995). The exemption limit is the annual wage-tax Freigrenze for tax classes
    // I/II/IV/V/VI; class III uses twice the amount.
    decimal SolidaritySurchargeRate,
    decimal SolidarityExemptionLimitAnnual,
    decimal SolidarityGlideRate,
    // Wage-tax allowances baked into the payroll tariff (§9a, §10c, §24b, §32 EStG).
    decimal EmployeeLumpSum,
    decimal SpecialExpenseLumpSum,
    decimal SingleParentRelief,
    decimal ChildAllowanceFull,
    // Social-insurance contribution rates (total, employer + employee) and the ceilings they apply to.
    decimal HealthGeneralRate,
    decimal HealthReducedRate,
    decimal AverageHealthAdditionalRate,
    decimal HealthAdditionalRateEmployeeShare,
    decimal PensionRate,
    decimal UnemploymentRate,
    decimal CareRate,
    decimal CareSaxonyEmployeeShift,
    decimal CareChildlessSurcharge,
    decimal CareChildDiscountPerChild,
    int CareChildDiscountMaxChildren,
    decimal HealthCareCeilingAnnual,
    decimal PensionCeilingAnnualWest,
    decimal PensionCeilingAnnualEast,
    // Vorsorgepauschale: share of the employee pension contribution that is deductible in the payroll tariff
    // (§39b Abs. 2 Satz 5 Nr. 3 EStG in connection with the §10 Abs. 3 phase-in).
    decimal PensionProvisionPhaseIn,
    string SourceNote)
{
    public decimal ChildAllowanceHalf => ChildAllowanceFull / 2m;

    /// <summary>§3 Nr. 63 EStG — 8 % of the west pension ceiling, derived rather than tabulated twice.</summary>
    public decimal BavTaxFreeLimit => PensionCeilingAnnualWest * 0.08m;

    /// <summary>§1 Abs. 1 Nr. 9 SvEV — 4 % of the west pension ceiling.</summary>
    public decimal BavSocialFreeLimit => PensionCeilingAnnualWest * 0.04m;

    public decimal PensionEmployeeRate => PensionRate / 2m;
    public decimal UnemploymentEmployeeRate => UnemploymentRate / 2m;
    public decimal HealthEmployeeBaseRate => HealthGeneralRate / 2m;
    public decimal HealthReducedEmployeeRate => HealthReducedRate / 2m;
    public decimal CareHalfRate => CareRate / 2m;

    /// <summary>
    /// The pension/unemployment ceiling for the payroll "Rechtskreis" of the given federal state. The east ceiling
    /// applied to Brandenburg, Mecklenburg-Vorpommern, Saxony, Saxony-Anhalt and Thuringia until it was unified
    /// with the west ceiling in 2025; Berlin counts as west.
    /// </summary>
    public decimal PensionCeilingAnnual(string? stateCode) =>
        IsEastRechtskreis(stateCode) ? PensionCeilingAnnualEast : PensionCeilingAnnualWest;

    public static bool IsEastRechtskreis(string? stateCode) =>
        (stateCode ?? string.Empty).Trim().ToUpperInvariant() is "BB" or "MV" or "SN" or "ST" or "TH";
}

/// <summary>
/// Per-year seed of the German payroll parameters, 2018 through the current tax year.
///
/// Sources (each applies to every seeded year in its own annual version):
/// <list type="bullet">
/// <item>Income-tax tariff: §32a EStG in the version in force for that year, as published in the BMF
/// "Programmablaufplan für die maschinelle Berechnung der Lohnsteuer" of the same year. 2024 uses the
/// retroactively raised Grundfreibetrag of 11.784 € (Gesetz zur steuerlichen Freistellung des Existenzminimums
/// 2024, Dec 2024), i.e. the final tariff that the December 2024 payroll re-run applied.</item>
/// <item>Solidaritätszuschlag: §3, §4 SolzG 1995. Until 2020 the annual wage-tax Freigrenze was 972 € with a 20 %
/// glide zone; from 2021 the Freigrenze jumped to 16.956 € with an 11,9 % glide zone.</item>
/// <item>Contribution ceilings: Sozialversicherungs-Rechengrößenverordnung of the respective year.</item>
/// <item>Contribution rates: §241/§243 SGB V (health 14,6 % / reduced 14,0 %), §242a SGB V (BMG notice of the
/// average Zusatzbeitrag), §157 SGB VI (pension 18,6 %), §341 SGB III (unemployment), §55 SGB XI (care incl. the
/// childless surcharge and the Saxony split).</item>
/// <item>Vorsorgepauschale phase-in: §39b Abs. 2 Satz 5 Nr. 3 EStG with §10 Abs. 3 EStG — 72 % in 2018 rising by
/// 4 points a year, accelerated to the full 100 % from 2023 by the Jahressteuergesetz 2022.</item>
/// </list>
///
/// Mid-year changes are modelled with the values in force at the end of the year, because a snapshot carries a
/// year and not a month. The only case in this range is care insurance: the rate rose from 3,05 % to 3,40 % and
/// the childless surcharge from 0,35 % to 0,60 % on 1 July 2023, and the per-child discounts were introduced on
/// the same date, so the 2023 row uses the post-July values.
/// </summary>
public static class TaxYearTable
{
    private const decimal Soli = 0.055m;
    private const decimal HealthGeneral = 0.146m;
    private const decimal HealthReduced = 0.140m;
    private const decimal Pension = 0.186m;
    private const decimal CareSaxonyShift = 0.005m;
    private const decimal SpecialExpenses = 36m;

    private static readonly IReadOnlyDictionary<int, TaxYearParameters> Table = Seed();

    public static int FirstYear { get; } = Table.Keys.Min();
    public static int LastYear { get; } = Table.Keys.Max();
    public static IReadOnlyList<int> Years { get; } = Table.Keys.OrderBy(y => y).ToArray();

    /// <summary>
    /// Resolves the parameter set for a year. An unset year (and any year after the newest seeded one) uses the
    /// newest seeded year; a year before the first seeded one uses the first seeded year, which is the closest
    /// law we have. Never throws, so an odd snapshot year can never break a calculation.
    /// </summary>
    public static TaxYearParameters Get(int? year)
    {
        if (year is null) return Table[LastYear];
        var clamped = Math.Clamp(year.Value, FirstYear, LastYear);
        return Table[clamped];
    }

    public static TaxYearParameters Resolve(CompensationProfileInput input) => Get(input.TaxYear);

    private static Dictionary<int, TaxYearParameters> Seed()
    {
        var years = new[]
        {
            // Year, §32a tariff, SolZ Freigrenze / glide, AN-Pauschbetrag, §24b, Kinderfreibetrag (KFB + BEA),
            // Ø Zusatzbeitrag, employee share of it, AV rate, PV rate, childless surcharge, child discount,
            // KV/PV ceiling, RV/AV ceiling west, RV/AV ceiling east, Vorsorgepauschale phase-in.
            Year(2018,
                new IncomeTaxTariff(9_000m, 13_996m, 997.80m, 1_400m, 54_949m, 220.13m, 2_397m, 948.49m,
                    260_532m, 0.42m, 8_621.75m, 0.45m, 16_437.70m),
                soliLimit: 972m, soliGlide: 0.20m,
                employeeLumpSum: 1_000m, singleParent: 1_908m, childAllowance: 7_428m,
                averageAdditional: 0.010m, additionalEmployeeShare: 1.0m,
                unemployment: 0.030m, care: 0.0255m, childless: 0.0025m, childDiscount: 0m,
                healthCeiling: 53_100m, pensionWest: 78_000m, pensionEast: 69_600m,
                provisionPhaseIn: 0.72m,
                source: "§32a EStG 2018; SolzG 1995; SVRechGrV 2018; Zusatzbeitrag 1,0 % (still employee-only)"),

            Year(2019,
                new IncomeTaxTariff(9_168m, 14_254m, 980.14m, 1_400m, 55_960m, 216.16m, 2_397m, 965.58m,
                    265_326m, 0.42m, 8_780.90m, 0.45m, 16_740.68m),
                soliLimit: 972m, soliGlide: 0.20m,
                employeeLumpSum: 1_000m, singleParent: 1_908m, childAllowance: 7_620m,
                averageAdditional: 0.009m, additionalEmployeeShare: 0.5m,
                unemployment: 0.025m, care: 0.0305m, childless: 0.0025m, childDiscount: 0m,
                healthCeiling: 54_450m, pensionWest: 80_400m, pensionEast: 73_800m,
                provisionPhaseIn: 0.76m,
                source: "§32a EStG 2019; SVRechGrV 2019; GKV-Versichertenentlastungsgesetz split the "
                    + "Zusatzbeitrag 50/50 from 2019; PV 3,05 %; AV cut to 2,5 %"),

            Year(2020,
                new IncomeTaxTariff(9_408m, 14_532m, 972.87m, 1_400m, 57_051m, 212.02m, 2_397m, 972.79m,
                    270_500m, 0.42m, 8_963.74m, 0.45m, 17_078.74m),
                soliLimit: 972m, soliGlide: 0.20m,
                employeeLumpSum: 1_000m, singleParent: 4_008m, childAllowance: 7_812m,
                averageAdditional: 0.011m, additionalEmployeeShare: 0.5m,
                unemployment: 0.024m, care: 0.0305m, childless: 0.0025m, childDiscount: 0m,
                healthCeiling: 56_250m, pensionWest: 82_800m, pensionEast: 77_400m,
                provisionPhaseIn: 0.80m,
                source: "§32a EStG 2020; SVRechGrV 2020; §24b raised to 4.008 € by the Zweites "
                    + "Corona-Steuerhilfegesetz; AV cut to 2,4 %"),

            Year(2021,
                new IncomeTaxTariff(9_744m, 14_753m, 995.21m, 1_400m, 57_918m, 208.85m, 2_397m, 950.96m,
                    274_612m, 0.42m, 9_136.63m, 0.45m, 17_374.99m),
                soliLimit: 16_956m, soliGlide: 0.119m,
                employeeLumpSum: 1_000m, singleParent: 4_008m, childAllowance: 8_388m,
                averageAdditional: 0.013m, additionalEmployeeShare: 0.5m,
                unemployment: 0.024m, care: 0.0305m, childless: 0.0025m, childDiscount: 0m,
                healthCeiling: 58_050m, pensionWest: 85_200m, pensionEast: 80_400m,
                provisionPhaseIn: 0.84m,
                source: "§32a EStG 2021; SVRechGrV 2021; Gesetz zur Rückführung des Solidaritätszuschlags "
                    + "raised the annual Freigrenze to 16.956 € and the glide rate to 11,9 %"),

            Year(2022,
                new IncomeTaxTariff(10_347m, 14_926m, 1_088.67m, 1_400m, 58_596m, 206.43m, 2_397m, 869.32m,
                    277_825m, 0.42m, 9_336.45m, 0.45m, 17_671.20m),
                soliLimit: 16_956m, soliGlide: 0.119m,
                employeeLumpSum: 1_200m, singleParent: 4_008m, childAllowance: 8_548m,
                averageAdditional: 0.013m, additionalEmployeeShare: 0.5m,
                unemployment: 0.024m, care: 0.0305m, childless: 0.0035m, childDiscount: 0m,
                healthCeiling: 58_050m, pensionWest: 84_600m, pensionEast: 81_000m,
                provisionPhaseIn: 0.88m,
                source: "§32a EStG 2022 (Steuerentlastungsgesetz 2022, retroactive Grundfreibetrag 10.347 €); "
                    + "AN-Pauschbetrag 1.200 €; childless care surcharge 0,35 % per GVWG; SVRechGrV 2022"),

            Year(2023,
                new IncomeTaxTariff(10_908m, 15_999m, 979.18m, 1_400m, 62_809m, 192.59m, 2_397m, 966.53m,
                    277_825m, 0.42m, 9_972.98m, 0.45m, 18_307.73m),
                soliLimit: 17_543m, soliGlide: 0.119m,
                employeeLumpSum: 1_230m, singleParent: 4_260m, childAllowance: 8_952m,
                averageAdditional: 0.016m, additionalEmployeeShare: 0.5m,
                unemployment: 0.026m, care: 0.034m, childless: 0.006m, childDiscount: 0.0025m,
                healthCeiling: 59_850m, pensionWest: 87_600m, pensionEast: 85_200m,
                provisionPhaseIn: 1.00m,
                source: "§32a EStG 2023; SVRechGrV 2023; Pflegeunterstützungs- und -entlastungsgesetz raised PV "
                    + "to 3,40 % and the childless surcharge to 0,60 % and introduced the per-child discounts "
                    + "on 1.7.2023 (values in force at year end); JStG 2022 brought the Vorsorgepauschale to 100 %"),

            Year(2024,
                new IncomeTaxTariff(11_784m, 17_005m, 954.80m, 1_400m, 66_760m, 181.19m, 2_397m, 991.21m,
                    277_825m, 0.42m, 10_636.31m, 0.45m, 18_971.06m),
                soliLimit: 18_130m, soliGlide: 0.119m,
                employeeLumpSum: 1_230m, singleParent: 4_260m, childAllowance: 9_540m,
                averageAdditional: 0.017m, additionalEmployeeShare: 0.5m,
                unemployment: 0.026m, care: 0.034m, childless: 0.006m, childDiscount: 0.0025m,
                healthCeiling: 62_100m, pensionWest: 90_600m, pensionEast: 89_400m,
                provisionPhaseIn: 1.00m,
                source: "§32a EStG 2024 in the final version (Grundfreibetrag 11.784 €, Kinderfreibetrag "
                    + "6.612 € + 2.928 € BEA); SVRechGrV 2024"),

            Year(2025,
                new IncomeTaxTariff(12_096m, 17_443m, 932.30m, 1_400m, 68_480m, 176.64m, 2_397m, 1_015.13m,
                    277_825m, 0.42m, 10_911.92m, 0.45m, 19_246.67m),
                soliLimit: 19_450m, soliGlide: 0.119m,
                employeeLumpSum: 1_230m, singleParent: 4_260m, childAllowance: 9_600m,
                averageAdditional: 0.025m, additionalEmployeeShare: 0.5m,
                unemployment: 0.026m, care: 0.036m, childless: 0.006m, childDiscount: 0.0025m,
                healthCeiling: 66_150m, pensionWest: 96_600m, pensionEast: 96_600m,
                provisionPhaseIn: 1.00m,
                source: "§32a EStG 2025 (Steuerfortentwicklungsgesetz); SVRechGrV 2025 — first year with a "
                    + "unified east/west pension ceiling; PV 3,60 %; Ø Zusatzbeitrag 2,5 %"),

            Year(2026,
                new IncomeTaxTariff(12_348m, 17_799m, 914.51m, 1_400m, 69_878m, 173.10m, 2_397m, 1_034.87m,
                    277_825m, 0.42m, 11_135.63m, 0.45m, 19_470.38m),
                soliLimit: 20_350m, soliGlide: 0.119m,
                employeeLumpSum: 1_230m, singleParent: 4_260m, childAllowance: 9_756m,
                averageAdditional: 0.029m, additionalEmployeeShare: 0.5m,
                unemployment: 0.026m, care: 0.036m, childless: 0.006m, childDiscount: 0.0025m,
                healthCeiling: 69_750m, pensionWest: 101_400m, pensionEast: 101_400m,
                provisionPhaseIn: 1.00m,
                source: "§32a EStG 2026 / BMF PAP 2026; SVRechGrV 2026; Ø Zusatzbeitrag 2,9 %")
        };

        return years.ToDictionary(y => y.Year);
    }

    private static TaxYearParameters Year(
        int year,
        IncomeTaxTariff tariff,
        decimal soliLimit,
        decimal soliGlide,
        decimal employeeLumpSum,
        decimal singleParent,
        decimal childAllowance,
        decimal averageAdditional,
        decimal additionalEmployeeShare,
        decimal unemployment,
        decimal care,
        decimal childless,
        decimal childDiscount,
        decimal healthCeiling,
        decimal pensionWest,
        decimal pensionEast,
        decimal provisionPhaseIn,
        string source) => new(
            year,
            tariff,
            Soli,
            soliLimit,
            soliGlide,
            employeeLumpSum,
            SpecialExpenses,
            singleParent,
            childAllowance,
            HealthGeneral,
            HealthReduced,
            averageAdditional,
            additionalEmployeeShare,
            Pension,
            unemployment,
            care,
            CareSaxonyShift,
            childless,
            childDiscount,
            5,
            healthCeiling,
            pensionWest,
            pensionEast,
            provisionPhaseIn,
            source);
}
