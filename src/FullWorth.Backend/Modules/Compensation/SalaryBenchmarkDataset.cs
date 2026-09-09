using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Read-only access to the curated German salary reference dataset
/// (<c>salary-benchmarks-de.json</c>, shipped as an embedded resource).
///
/// The dataset is deliberately compact: one national median per profession x year x
/// experience band, plus one factor per Bundesland. Regional values are computed here at
/// lookup time and are always flagged as derived — see the "method" block in the JSON.
/// </summary>
public static class SalaryBenchmarkDataset
{
    public const string ResourceFileName = "salary-benchmarks-de.json";

    public const string QualityPublishedAnchor = "published-anchor";
    public const string QualityCuratedEstimate = "curated-estimate";
    public const string QualityDerivedYear = "derived-year";
    public const string QualityDerivedRegional = "derived-regional";
    public const string QualityDerivedYearRegional = "derived-year-regional";
    public const string QualityDerivedSpread = "derived-spread";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Lazy<SalaryBenchmarkFile> Lazy =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Index> Indexed =
        new(() => new Index(Lazy.Value), LazyThreadSafetyMode.ExecutionAndPublication);

    public static SalaryBenchmarkFile Data => Lazy.Value;

    public static SalaryBenchmarkMetadata Metadata()
    {
        var data = Data;
        return new SalaryBenchmarkMetadata(
            data.SchemaVersion,
            data.DatasetKey,
            data.Currency,
            data.Basis,
            data.DataAsOf,
            data.AnchorYear,
            data.Years,
            data.ProjectedYears,
            data.NationalStateKey,
            data.Disclaimer,
            data.VerificationStatus,
            data.Method,
            data.Sources,
            data.QualityLevels,
            data.ExperienceBands,
            data.SpreadGroups,
            data.WageIndex,
            data.States,
            data.Professions,
            data.NationalRows.Count);
    }

    /// <summary>Resolves the source labels behind a <c>sourceSet</c> key.</summary>
    public static IReadOnlyList<string> SourceKeysFor(string sourceSetKey) =>
        Indexed.Value.SourceSets.TryGetValue(sourceSetKey, out var set)
            ? set.Sources
            : Array.Empty<string>();

    public static SalaryBenchmarkQualityLevel QualityLevel(string key) =>
        Indexed.Value.QualityLevels.TryGetValue(key, out var level)
            ? level
            : throw new InvalidOperationException($"Unknown quality level '{key}' in {ResourceFileName}.");

    public static SalaryBenchmarkProfession? FindProfession(string? profession) =>
        profession is null ? null
            : Indexed.Value.Professions.TryGetValue(Normalize(profession), out var found) ? found : null;

    public static SalaryBenchmarkState? FindState(string? bundesland) =>
        bundesland is null ? null
            : Indexed.Value.States.TryGetValue(Normalize(bundesland), out var found) ? found : null;

    public static string? NormalizeExperienceKey(string? experience) =>
        experience is null ? null
            : Indexed.Value.Experiences.TryGetValue(Normalize(experience), out var found) ? found.Key : null;

    public static SalaryBenchmarkState NationalState() => Indexed.Value.National;

    /// <summary>
    /// Resolves one benchmark record. Falls back deliberately and reports every fallback:
    /// unknown/blank Bundesland -&gt; national, missing year -&gt; nearest available year,
    /// experience band not defined for the profession -&gt; nearest defined band.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown profession or unknown experience band key.</exception>
    public static SalaryBenchmarkRecord Lookup(SalaryBenchmarkQuery query)
    {
        var data = Data;
        var index = Indexed.Value;

        var profession = FindProfession(query.Profession)
            ?? throw new ArgumentException(
                $"Unknown profession '{query.Profession}'. Known keys: {string.Join(", ", index.Professions.Values.Select(p => p.Key).Distinct())}.",
                nameof(query.Profession));

        var notes = new List<string>();

        // --- Bundesland ---
        var stateFallback = false;
        var state = FindState(query.Bundesland);
        if (state is null)
        {
            state = index.National;
            if (!string.IsNullOrWhiteSpace(query.Bundesland))
            {
                stateFallback = true;
                notes.Add($"Bundesland '{query.Bundesland.Trim()}' ist unbekannt – es wird der Bundeswert verwendet.");
            }
        }

        // --- experience band ---
        var experienceKey = query.Experience is null || string.IsNullOrWhiteSpace(query.Experience)
            ? DefaultExperienceKey(profession)
            : NormalizeExperienceKey(query.Experience)
              ?? throw new ArgumentException(
                  $"Unknown experience band '{query.Experience}'. Known keys: {string.Join(", ", data.ExperienceBands.Select(b => b.Key))}.",
                  nameof(query.Experience));

        var experienceFallback = false;
        if (!profession.ExperienceBands.Contains(experienceKey, StringComparer.Ordinal))
        {
            var replacement = NearestExperienceKey(profession, experienceKey);
            var requestedLabel = index.ExperienceByKey[experienceKey].Label;
            notes.Add(
                $"Für {profession.Name} liegt keine Stufe '{requestedLabel}' vor – es wird '{index.ExperienceByKey[replacement].Label}' verwendet.");
            experienceKey = replacement;
            experienceFallback = true;
        }

        // --- year ---
        var requestedYear = query.Year ?? data.Years[^1];
        if (!index.Rows.TryGetValue((profession.Key, experienceKey), out var candidates))
            throw new InvalidOperationException(
                $"{ResourceFileName} declares band '{experienceKey}' for '{profession.Key}' but ships no rows for it.");
        var row = candidates.TryGetValue(requestedYear, out var exact)
            ? exact
            : candidates[NearestYear(candidates.Keys, requestedYear)];
        var yearFallback = row.Year != requestedYear;
        if (yearFallback)
            notes.Add($"Für {requestedYear} liegt kein Datensatz vor – es wird das nächstliegende Jahr {row.Year} verwendet.");

        // --- values ---
        var median = row.Median;
        var p25 = row.P25;
        var p75 = row.P75;
        var quality = row.Quality;

        if (!state.National)
        {
            median = RoundHundred(median * state.Factor);
            p25 = RoundHundred(p25 * state.Factor);
            p75 = RoundHundred(p75 * state.Factor);
            quality = quality == QualityDerivedYear ? QualityDerivedYearRegional : QualityDerivedRegional;
            notes.Add(
                $"Regionalfaktor {Fmt(state.Factor)} für {state.Name} auf den Bundeswert angewendet (branchenübergreifend, nicht berufsspezifisch).");
        }

        if (row.Year != data.AnchorYear)
        {
            var from = index.WageIndexByYear[data.AnchorYear];
            var to = index.WageIndexByYear[row.Year];
            notes.Add(
                $"Jahr {row.Year} aus dem Ankerjahr {data.AnchorYear} über den Nominallohnindex gerechnet ({Fmt(from.Index)} → {Fmt(to.Index)}).");
            if (to.Projected)
                notes.Add($"Der Nominallohnindex für {row.Year} ist eine Projektion, kein veröffentlichter Jahresdurchschnitt.");
        }

        notes.Add("P25/P75 sind nicht erhoben, sondern aus dem Median über einen dokumentierten Streuungsfaktor gerechnet.");

        var level = QualityLevel(quality);
        var quartileLevel = QualityLevel(row.QuartileQuality);
        var sourceKeys = SourceKeysFor(row.SourceSet);
        if (!state.National)
            sourceKeys = sourceKeys.Concat(state.Sources).Distinct(StringComparer.Ordinal).ToArray();
        var sources = sourceKeys
            .Select(key => index.Sources.TryGetValue(key, out var s) ? s : new SalaryBenchmarkSource(key, key, null, null))
            .ToArray();

        return new SalaryBenchmarkRecord(
            profession.Key,
            profession.Name,
            profession.Group,
            state.Key,
            state.Name,
            state.Factor,
            state.National,
            row.Year,
            experienceKey,
            index.ExperienceByKey[experienceKey].Label,
            median,
            p25,
            p75,
            data.Currency,
            data.Basis,
            quality,
            level.Label,
            level.Derived,
            level.Measured,
            level.Confidence,
            level.Explanation,
            row.QuartileQuality,
            quartileLevel.Explanation,
            sourceKeys,
            string.Join(" · ", sources.Select(s => s.Label)),
            sources,
            data.DataAsOf,
            data.Disclaimer,
            data.VerificationStatus,
            data.ProjectedYears.Contains(row.Year),
            stateFallback,
            yearFallback,
            experienceFallback,
            requestedYear,
            query.Bundesland,
            query.Experience,
            notes,
            BuildComparison(query.AnnualGross, median, p25, p75));
    }

    /// <summary>All available years for one profession / Bundesland / experience combination.</summary>
    public static SalaryBenchmarkSeries Series(string profession, string? bundesland, string? experience)
    {
        var data = Data;
        var points = new List<SalaryBenchmarkSeriesPoint>(data.Years.Count);
        SalaryBenchmarkRecord? last = null;

        foreach (var year in data.Years)
        {
            var record = Lookup(new SalaryBenchmarkQuery(profession, bundesland, year, experience, null));
            if (record.YearFallbackApplied) continue;
            last = record;
            points.Add(new SalaryBenchmarkSeriesPoint(
                record.Year,
                record.AnnualGrossMedian,
                record.AnnualGrossP25,
                record.AnnualGrossP75,
                record.Quality,
                record.Derived,
                record.YearIsProjected));
        }

        last ??= Lookup(new SalaryBenchmarkQuery(profession, bundesland, null, experience, null));
        return new SalaryBenchmarkSeries(
            last.ProfessionKey,
            last.ProfessionName,
            last.StateKey,
            last.StateName,
            last.ExperienceKey,
            last.ExperienceLabel,
            data.Currency,
            data.DataAsOf,
            data.Disclaimer,
            last.StateFallbackApplied,
            last.ExperienceFallbackApplied,
            last.DerivationNotes.Where(n => !n.StartsWith("Jahr ", StringComparison.Ordinal)).ToArray(),
            points);
    }

    private static SalaryBenchmarkComparison? BuildComparison(decimal? annualGross, decimal median, decimal p25, decimal p75)
    {
        if (annualGross is null or <= 0m) return null;
        var gross = annualGross.Value;
        var (position, label) = gross switch
        {
            _ when gross < p25 => ("below-p25", "unter dem unteren Viertel"),
            _ when gross < median => ("p25-to-median", "zwischen unterem Viertel und Median"),
            _ when gross <= p75 => ("median-to-p75", "zwischen Median und oberem Viertel"),
            _ => ("above-p75", "über dem oberen Viertel")
        };
        return new SalaryBenchmarkComparison(
            Math.Round(gross, 2, MidpointRounding.AwayFromZero),
            Math.Round(gross - median, 2, MidpointRounding.AwayFromZero),
            median <= 0m ? 0m : Math.Round(gross / median * 100m, 1, MidpointRounding.AwayFromZero),
            position,
            label);
    }

    private static string DefaultExperienceKey(SalaryBenchmarkProfession profession)
    {
        // "3-5" is the middle band and the closest thing to a profession's overall median.
        if (profession.ExperienceBands.Contains("3-5", StringComparer.Ordinal)) return "3-5";
        return profession.ExperienceBands[^1];
    }

    private static string NearestExperienceKey(SalaryBenchmarkProfession profession, string requested)
    {
        var order = Indexed.Value.ExperienceOrder;
        var target = order.IndexOf(requested);
        return profession.ExperienceBands
            .OrderBy(key => Math.Abs(order.IndexOf(key) - target))
            .ThenBy(key => order.IndexOf(key))
            .First();
    }

    private static int NearestYear(IEnumerable<int> available, int requested) => available
        .OrderBy(year => Math.Abs(year - requested))
        .ThenByDescending(year => year)
        .First();

    private static decimal RoundHundred(decimal value) =>
        Math.Round(value / 100m, 0, MidpointRounding.AwayFromZero) * 100m;

    // German decimal comma without depending on ICU culture data (the runtime image may be
    // globalization-invariant), so the notes read the same everywhere.
    private static string Fmt(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>Lowercase, umlaut-folded, punctuation-free key so "Baden-Württemberg", "baden wuerttemberg" and "BW" all resolve.</summary>
    internal static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            switch (c)
            {
                case 'ä': builder.Append("ae"); break;
                case 'ö': builder.Append("oe"); break;
                case 'ü': builder.Append("ue"); break;
                case 'ß': builder.Append("ss"); break;
                default:
                    if (char.IsLetterOrDigit(c)) builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    private static SalaryBenchmarkFile Load()
    {
        var assembly = typeof(SalaryBenchmarkDataset).Assembly;
        var name = ResolveResourceName(assembly);
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");
        return JsonSerializer.Deserialize<SalaryBenchmarkFile>(stream, SerializerOptions)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' deserialized to null.");
    }

    private static string ResolveResourceName(Assembly assembly)
    {
        var names = assembly.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => n.EndsWith(ResourceFileName, StringComparison.Ordinal));
        return match ?? throw new InvalidOperationException(
            $"Embedded resource '{ResourceFileName}' is missing. Check the EmbeddedResource item in FullWorth.Backend.csproj. Found: {string.Join(", ", names)}");
    }

    private sealed class Index
    {
        internal Index(SalaryBenchmarkFile data)
        {
            Sources = data.Sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
            SourceSets = data.SourceSets.ToDictionary(s => s.Key, StringComparer.Ordinal);
            QualityLevels = data.QualityLevels.ToDictionary(q => q.Key, StringComparer.Ordinal);
            ExperienceByKey = data.ExperienceBands.ToDictionary(b => b.Key, StringComparer.Ordinal);
            ExperienceOrder = data.ExperienceBands.Select(b => b.Key).ToList();
            WageIndexByYear = data.WageIndex.ToDictionary(w => w.Year);

            Professions = new Dictionary<string, SalaryBenchmarkProfession>(StringComparer.Ordinal);
            foreach (var profession in data.Professions)
            {
                Professions[Normalize(profession.Key)] = profession;
                Professions.TryAdd(Normalize(profession.Name), profession);
            }

            States = new Dictionary<string, SalaryBenchmarkState>(StringComparer.Ordinal);
            foreach (var state in data.States)
            {
                States[Normalize(state.Key)] = state;
                States.TryAdd(Normalize(state.Name), state);
            }
            foreach (var (alias, key) in StateAliases)
                if (States.TryGetValue(Normalize(key), out var state)) States.TryAdd(Normalize(alias), state);

            National = data.States.FirstOrDefault(s => s.National)
                ?? throw new InvalidOperationException($"{ResourceFileName} has no national state entry.");

            Experiences = new Dictionary<string, SalaryBenchmarkExperienceBand>(StringComparer.Ordinal);
            foreach (var band in data.ExperienceBands)
            {
                Experiences[Normalize(band.Key)] = band;
                Experiences.TryAdd(Normalize(band.Label), band);
            }
            foreach (var (alias, key) in ExperienceAliases)
                if (Experiences.TryGetValue(Normalize(key), out var band)) Experiences.TryAdd(Normalize(alias), band);

            Rows = data.NationalRows
                .GroupBy(r => (r.Profession, r.Experience))
                .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<int, SalaryBenchmarkNationalRow>)g.ToDictionary(r => r.Year));
        }

        internal IReadOnlyDictionary<string, SalaryBenchmarkSource> Sources { get; }
        internal IReadOnlyDictionary<string, SalaryBenchmarkSourceSet> SourceSets { get; }
        internal IReadOnlyDictionary<string, SalaryBenchmarkQualityLevel> QualityLevels { get; }
        internal IReadOnlyDictionary<string, SalaryBenchmarkExperienceBand> ExperienceByKey { get; }
        internal List<string> ExperienceOrder { get; }
        internal IReadOnlyDictionary<int, SalaryBenchmarkWagePoint> WageIndexByYear { get; }
        internal Dictionary<string, SalaryBenchmarkProfession> Professions { get; }
        internal Dictionary<string, SalaryBenchmarkState> States { get; }
        internal Dictionary<string, SalaryBenchmarkExperienceBand> Experiences { get; }
        internal SalaryBenchmarkState National { get; }
        internal IReadOnlyDictionary<(string Profession, string Experience), IReadOnlyDictionary<int, SalaryBenchmarkNationalRow>> Rows { get; }

        private static readonly (string Alias, string Key)[] StateAliases =
        [
            ("Deutschland", "DE"), ("Bund", "DE"), ("national", "DE"), ("bundesweit", "DE"),
            ("BW", "BW"), ("Baden Wuerttemberg", "BW"),
            ("Bayern (Freistaat)", "BY"), ("Bavaria", "BY"),
            ("Mecklenburg Vorpommern", "MV"),
            ("Nordrhein Westfalen", "NW"), ("NRW", "NW"),
            ("Rheinland Pfalz", "RP"),
            ("Sachsen Anhalt", "ST"),
            ("Schleswig Holstein", "SH"),
            ("Thueringen", "TH")
        ];

        private static readonly (string Alias, string Key)[] ExperienceAliases =
        [
            ("apprentice", "ausbildung"), ("azubi", "ausbildung"), ("trainee", "ausbildung"),
            ("berufseinsteiger", "0-2"), ("einsteiger", "0-2"), ("junior", "0-2"), ("0 2", "0-2"), ("02", "0-2"),
            ("3 5", "3-5"), ("35", "3-5"), ("mid", "3-5"),
            ("6 10", "6-10"), ("610", "6-10"), ("senior", "6-10"),
            ("10+", "10plus"), ("10 plus", "10plus"), ("10-plus", "10plus"), ("expert", "10plus"), ("10", "10plus")
        ];
    }
}
