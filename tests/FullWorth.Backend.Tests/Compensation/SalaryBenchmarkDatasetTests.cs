using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// Guards the curated salary reference dataset (<c>salary-benchmarks-de.json</c>).
/// The point of these tests is honesty as much as correctness: the dataset must never present a
/// derived number as a measured one, and every number must be traceable to a source label.
/// </summary>
public sealed class SalaryBenchmarkDatasetTests
{
    private static readonly string[] ExpectedStateKeys =
    [
        "DE",
        "BW", "BY", "BE", "BB", "HB", "HH", "HE", "MV",
        "NI", "NW", "RP", "SL", "SN", "ST", "SH", "TH"
    ];

    private static readonly string[] CoreExperienceBands = ["0-2", "3-5", "6-10", "10plus"];

    // ---------------------------------------------------------------- loading & shape

    [Fact]
    public void Dataset_LoadsAndParses()
    {
        var data = SalaryBenchmarkDataset.Data;

        Assert.Equal(1, data.SchemaVersion);
        Assert.Equal("EUR", data.Currency);
        Assert.Equal("annual-gross-fulltime", data.Basis);
        Assert.False(string.IsNullOrWhiteSpace(data.DataAsOf));
        Assert.NotEmpty(data.NationalRows);
        Assert.NotEmpty(data.Professions);
        Assert.NotEmpty(data.Sources);
        Assert.NotEmpty(data.QualityLevels);
    }

    [Fact]
    public void Dataset_DocumentsItsMethodAndLimitations()
    {
        var data = SalaryBenchmarkDataset.Data;

        Assert.False(string.IsNullOrWhiteSpace(data.Disclaimer));
        Assert.False(string.IsNullOrWhiteSpace(data.Method.Summary));
        Assert.False(string.IsNullOrWhiteSpace(data.Method.Revision));
        Assert.True(data.Method.Steps.Count >= 4, "the derivation method must be documented step by step");
        Assert.NotEmpty(data.Method.Limitations);
        Assert.All(data.Method.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step)));
        Assert.All(data.Method.Limitations, limit => Assert.False(string.IsNullOrWhiteSpace(limit)));
    }

    [Fact]
    public void Dataset_CoversAllSixteenBundeslaenderPlusNational()
    {
        var data = SalaryBenchmarkDataset.Data;

        Assert.Equal(17, data.States.Count);
        Assert.Equal(ExpectedStateKeys.OrderBy(x => x, StringComparer.Ordinal), data.States.Select(s => s.Key).OrderBy(x => x, StringComparer.Ordinal));
        var national = Assert.Single(data.States, s => s.National);
        Assert.Equal("DE", national.Key);
        Assert.Equal(1.0m, national.Factor);
        Assert.All(data.States, state => Assert.InRange(state.Factor, 0.5m, 2.0m));
    }

    [Fact]
    public void Dataset_CoversEveryYearFrom2018ToTheAnchorYearForward()
    {
        var data = SalaryBenchmarkDataset.Data;

        Assert.Equal(2018, data.Years[0]);
        Assert.True(data.Years[^1] >= 2026, "the dataset must reach the current year");
        Assert.Equal(data.Years, data.Years.OrderBy(y => y));
        Assert.Equal(data.Years.Count, data.Years.Distinct().Count());
        Assert.Equal(data.Years[^1] - data.Years[0] + 1, data.Years.Count); // contiguous, no holes
        Assert.Contains(data.AnchorYear, data.Years);
        Assert.All(data.ProjectedYears, year => Assert.Contains(year, data.Years));
        Assert.Equal(data.Years.OrderBy(y => y), data.WageIndex.Select(w => w.Year).OrderBy(y => y));
    }

    [Fact]
    public void Dataset_CoversTheRequestedProfessionsAndBands()
    {
        var data = SalaryBenchmarkDataset.Data;

        Assert.Contains(data.Professions, p => p.Key == "fachinformatiker-anwendungsentwicklung");
        Assert.Contains(data.Professions, p => p.Key == "softwareentwicklung");
        Assert.True(data.Professions.Count >= 10, "the starter set should cover a reasonable spread of common German professions");

        foreach (var band in CoreExperienceBands)
            Assert.Contains(data.ExperienceBands, b => b.Key == band);
        Assert.Contains(data.ExperienceBands, b => b.Key == "ausbildung");

        // Every profession must carry the four core experience bands for every year.
        foreach (var profession in data.Professions)
        {
            foreach (var band in CoreExperienceBands)
            {
                Assert.Contains(band, profession.ExperienceBands);
                foreach (var year in data.Years)
                    Assert.Contains(data.NationalRows, r =>
                        r.Profession == profession.Key && r.Experience == band && r.Year == year);
            }
        }
    }

    [Fact]
    public void Dataset_RowCountMatchesProfessionTimesYearTimesBand()
    {
        var data = SalaryBenchmarkDataset.Data;
        var expected = data.Professions.Sum(p => p.ExperienceBands.Count) * data.Years.Count;

        Assert.Equal(expected, data.NationalRows.Count);
        Assert.Equal(
            data.NationalRows.Count,
            data.NationalRows.Select(r => (r.Profession, r.Year, r.Experience)).Distinct().Count());
    }

    // ---------------------------------------------------------------- source labels

    [Fact]
    public void EveryRecordCarriesAtLeastOneResolvableSourceLabel()
    {
        var data = SalaryBenchmarkDataset.Data;
        var sourceKeys = data.Sources.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(data.Sources, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Key));
            Assert.False(string.IsNullOrWhiteSpace(source.Label));
        });

        Assert.All(data.NationalRows, row =>
        {
            var keys = SalaryBenchmarkDataset.SourceKeysFor(row.SourceSet);
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.Contains(key, sourceKeys));
        });

        Assert.All(data.SourceSets, set =>
        {
            Assert.NotEmpty(set.Sources);
            Assert.All(set.Sources, key => Assert.Contains(key, sourceKeys));
        });

        Assert.All(data.States, state =>
        {
            Assert.NotEmpty(state.Sources);
            Assert.All(state.Sources, key => Assert.Contains(key, sourceKeys));
        });

        Assert.All(data.Professions, profession =>
        {
            Assert.NotEmpty(profession.AnchorOverallSources);
            Assert.All(profession.AnchorOverallSources, key => Assert.Contains(key, sourceKeys));
        });

        Assert.All(data.WageIndex, point =>
        {
            Assert.NotEmpty(point.Sources);
            Assert.All(point.Sources, key => Assert.Contains(key, sourceKeys));
        });
    }

    // ---------------------------------------------------------------- honesty invariants

    [Fact]
    public void NoQualityLevelClaimsToBeMeasured()
    {
        var data = SalaryBenchmarkDataset.Data;

        Assert.All(data.QualityLevels, level =>
        {
            Assert.False(level.Measured, $"quality level '{level.Key}' must not claim measured data — this dataset has none");
            Assert.False(string.IsNullOrWhiteSpace(level.Label));
            Assert.False(string.IsNullOrWhiteSpace(level.Confidence));
            Assert.False(string.IsNullOrWhiteSpace(level.Explanation));
        });
        Assert.DoesNotContain("measured", data.QualityLevels.Select(l => l.Key));
    }

    [Fact]
    public void NoDerivedRecordClaimsAnUnderivedQualityMarker()
    {
        var data = SalaryBenchmarkDataset.Data;

        // National rows are never the raw published figure: the anchor year is a curated split of the
        // profession median across experience bands, everything else is rolled by the wage index.
        Assert.All(data.NationalRows, row =>
        {
            var level = SalaryBenchmarkDataset.QualityLevel(row.Quality);
            Assert.True(level.Derived, $"row {row.Profession}/{row.Year}/{row.Experience} is derived but claims '{row.Quality}'");
            Assert.NotEqual(SalaryBenchmarkDataset.QualityPublishedAnchor, row.Quality);

            var expected = row.Year == data.AnchorYear
                ? SalaryBenchmarkDataset.QualityCuratedEstimate
                : SalaryBenchmarkDataset.QualityDerivedYear;
            Assert.Equal(expected, row.Quality);
        });

        // p25/p75 are never sourced, they are always spread-derived.
        Assert.All(data.NationalRows, row =>
        {
            Assert.Equal(SalaryBenchmarkDataset.QualityDerivedSpread, row.QuartileQuality);
            Assert.True(SalaryBenchmarkDataset.QualityLevel(row.QuartileQuality).Derived);
        });

        // A per-Bundesland factor is a derivation, not a measurement.
        Assert.All(data.States.Where(s => !s.National), state =>
            Assert.True(SalaryBenchmarkDataset.QualityLevel(state.Quality).Derived, $"state {state.Key} claims '{state.Quality}'"));
    }

    [Fact]
    public void PublishedAnchorMarkerIsOnlyUsedForTheProfessionAnchorAndTheNationalReference()
    {
        var data = SalaryBenchmarkDataset.Data;

        Assert.All(data.Professions, profession =>
        {
            Assert.Equal(SalaryBenchmarkDataset.QualityPublishedAnchor, profession.AnchorOverallQuality);
            Assert.True(profession.AnchorOverallMedian > 0m);
        });
        Assert.Equal(SalaryBenchmarkDataset.QualityPublishedAnchor, SalaryBenchmarkDataset.NationalState().Quality);
        Assert.DoesNotContain(SalaryBenchmarkDataset.QualityPublishedAnchor, data.NationalRows.Select(r => r.Quality));
    }

    [Fact]
    public void EveryRowReferencesDeclaredKeysAndOrderedQuartiles()
    {
        var data = SalaryBenchmarkDataset.Data;
        var professions = data.Professions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var bands = data.ExperienceBands.Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
        var qualities = data.QualityLevels.Select(q => q.Key).ToHashSet(StringComparer.Ordinal);
        var years = data.Years.ToHashSet();

        Assert.All(data.NationalRows, row =>
        {
            Assert.Contains(row.Profession, professions);
            Assert.Contains(row.Experience, bands);
            Assert.Contains(row.Quality, qualities);
            Assert.Contains(row.QuartileQuality, qualities);
            Assert.Contains(row.Year, years);
            Assert.True(row.Median > 0m);
            Assert.True(row.P25 < row.Median, $"{row.Profession}/{row.Year}/{row.Experience}: p25 {row.P25} !< median {row.Median}");
            Assert.True(row.P75 > row.Median, $"{row.Profession}/{row.Year}/{row.Experience}: p75 {row.P75} !> median {row.Median}");
            Assert.Equal(0m, row.Median % 100m); // rounded to 100 EUR, no false precision
        });
    }

    [Fact]
    public void MedianRisesWithExperienceWithinAProfessionAndYear()
    {
        var data = SalaryBenchmarkDataset.Data;
        var order = data.ExperienceBands.Select(b => b.Key).ToList();

        foreach (var group in data.NationalRows.GroupBy(r => (r.Profession, r.Year)))
        {
            var ordered = group.OrderBy(r => order.IndexOf(r.Experience)).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(
                    ordered[i].Median > ordered[i - 1].Median,
                    $"{group.Key.Profession}/{group.Key.Year}: {ordered[i].Experience} ({ordered[i].Median}) must exceed {ordered[i - 1].Experience} ({ordered[i - 1].Median})");
        }
    }

    // ---------------------------------------------------------------- lookup

    [Fact]
    public void Lookup_ExactHitReturnsTheNationalRowUnchanged()
    {
        var data = SalaryBenchmarkDataset.Data;
        var row = data.NationalRows.Single(r =>
            r.Profession == "fachinformatiker-anwendungsentwicklung" && r.Year == data.AnchorYear && r.Experience == "3-5");

        var record = SalaryBenchmarkDataset.Lookup(
            new SalaryBenchmarkQuery("fachinformatiker-anwendungsentwicklung", "DE", data.AnchorYear, "3-5", null));

        Assert.Equal(row.Median, record.AnnualGrossMedian);
        Assert.Equal(row.P25, record.AnnualGrossP25);
        Assert.Equal(row.P75, record.AnnualGrossP75);
        Assert.Equal(1.0m, record.StateFactor);
        Assert.True(record.StateIsNational);
        Assert.False(record.StateFallbackApplied);
        Assert.False(record.YearFallbackApplied);
        Assert.False(record.ExperienceFallbackApplied);
        Assert.False(record.Measured);
        Assert.True(record.Derived);
        Assert.NotEmpty(record.SourceKeys);
        Assert.False(string.IsNullOrWhiteSpace(record.SourceLabel));
        Assert.NotEmpty(record.Sources);
        Assert.False(string.IsNullOrWhiteSpace(record.Disclaimer));
        Assert.NotEmpty(record.DerivationNotes);
    }

    [Fact]
    public void Lookup_UnknownBundeslandFallsBackToNational()
    {
        var national = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, "3-5", null));
        var unknown = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "Absurdistan", 2024, "3-5", null));

        Assert.Equal("DE", unknown.StateKey);
        Assert.True(unknown.StateIsNational);
        Assert.True(unknown.StateFallbackApplied);
        Assert.Equal(national.AnnualGrossMedian, unknown.AnnualGrossMedian);
        Assert.Contains(unknown.DerivationNotes, note => note.Contains("Absurdistan", StringComparison.Ordinal));
        Assert.Equal("Absurdistan", unknown.RequestedBundesland);
    }

    [Fact]
    public void Lookup_MissingBundeslandIsNotTreatedAsAFallback()
    {
        var record = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", null, 2024, "3-5", null));

        Assert.Equal("DE", record.StateKey);
        Assert.False(record.StateFallbackApplied);
    }

    [Theory]
    [InlineData("BY")]
    [InlineData("by")]
    [InlineData("Bayern")]
    [InlineData("bayern")]
    public void Lookup_AcceptsBundeslandCodeAndName(string bundesland)
    {
        var record = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", bundesland, 2024, "3-5", null));

        Assert.Equal("BY", record.StateKey);
        Assert.False(record.StateFallbackApplied);
    }

    [Theory]
    [InlineData(2011, 2018)]
    [InlineData(2017, 2018)]
    [InlineData(2099, 2026)]
    public void Lookup_MissingYearFallsBackToTheNearestAvailableYear(int requested, int expected)
    {
        var record = SalaryBenchmarkDataset.Lookup(
            new SalaryBenchmarkQuery("softwareentwicklung", "DE", requested, "3-5", null));

        Assert.Equal(expected, record.Year);
        Assert.Equal(requested, record.RequestedYear);
        Assert.True(record.YearFallbackApplied);
        Assert.Contains(record.DerivationNotes, note => note.Contains(requested.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Lookup_WithoutYearUsesTheMostRecentYear()
    {
        var data = SalaryBenchmarkDataset.Data;

        var record = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", null, "3-5", null));

        Assert.Equal(data.Years[^1], record.Year);
        Assert.False(record.YearFallbackApplied);
    }

    [Fact]
    public void Lookup_AppliesTheBundeslandFactorAndDowngradesTheQualityMarker()
    {
        var data = SalaryBenchmarkDataset.Data;
        var bavaria = data.States.Single(s => s.Key == "BY");

        var national = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", data.AnchorYear, "6-10", null));
        var regional = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "BY", data.AnchorYear, "6-10", null));

        Assert.Equal(Math.Round(national.AnnualGrossMedian * bavaria.Factor / 100m, 0, MidpointRounding.AwayFromZero) * 100m, regional.AnnualGrossMedian);
        Assert.Equal(SalaryBenchmarkDataset.QualityCuratedEstimate, national.Quality);
        Assert.Equal(SalaryBenchmarkDataset.QualityDerivedRegional, regional.Quality);
        Assert.True(regional.Derived);
        Assert.False(regional.Measured);
        Assert.Contains(regional.DerivationNotes, note => note.Contains("Regionalfaktor", StringComparison.Ordinal));
    }

    [Fact]
    public void Lookup_MarksDoublyDerivedRecordsAsYearAndRegional()
    {
        var data = SalaryBenchmarkDataset.Data;
        var otherYear = data.Years.First(y => y != data.AnchorYear);

        var record = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "SN", otherYear, "6-10", null));

        Assert.Equal(SalaryBenchmarkDataset.QualityDerivedYearRegional, record.Quality);
        Assert.Equal("very-low", record.Confidence);
        Assert.Contains(record.DerivationNotes, note => note.Contains("Nominallohnindex", StringComparison.Ordinal));
    }

    [Fact]
    public void Lookup_ProjectedYearsAreFlagged()
    {
        var data = SalaryBenchmarkDataset.Data;
        Assert.NotEmpty(data.ProjectedYears);

        var projected = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", data.ProjectedYears[0], "3-5", null));
        var anchor = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", data.AnchorYear, "3-5", null));

        Assert.True(projected.YearIsProjected);
        Assert.False(anchor.YearIsProjected);
    }

    [Fact]
    public void Lookup_ExperienceBandMissingForAProfessionFallsBackToTheNearestBand()
    {
        // Softwareentwicklung has no dual Ausbildung in the dataset, Fachinformatiker does.
        var software = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, "ausbildung", null));
        var fachinformatiker = SalaryBenchmarkDataset.Lookup(
            new SalaryBenchmarkQuery("fachinformatiker-anwendungsentwicklung", "DE", 2024, "ausbildung", null));

        Assert.True(software.ExperienceFallbackApplied);
        Assert.Equal("0-2", software.ExperienceKey);
        Assert.Equal("ausbildung", software.RequestedExperience);
        Assert.Contains(software.DerivationNotes, note => note.Contains("keine Stufe", StringComparison.Ordinal));

        Assert.False(fachinformatiker.ExperienceFallbackApplied);
        Assert.Equal("ausbildung", fachinformatiker.ExperienceKey);
    }

    [Theory]
    [InlineData("10plus", "10plus")]
    [InlineData("10+", "10plus")]
    [InlineData("10-plus", "10plus")]
    [InlineData("expert", "10plus")]
    [InlineData("berufseinsteiger", "0-2")]
    [InlineData("Einsteiger", "0-2")]
    [InlineData("senior", "6-10")]
    [InlineData("Ausbildung", "ausbildung")]
    public void Lookup_NormalizesExperienceAliases(string input, string expected)
    {
        Assert.Equal(expected, SalaryBenchmarkDataset.NormalizeExperienceKey(input));
    }

    [Fact]
    public void Lookup_WithoutExperienceUsesTheMiddleBand()
    {
        var record = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, null, null));

        Assert.Equal("3-5", record.ExperienceKey);
        Assert.False(record.ExperienceFallbackApplied);
    }

    [Fact]
    public void Lookup_UnknownProfessionOrBandIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("astronaut", "DE", 2024, "3-5", null)));
        Assert.Throws<ArgumentException>(() =>
            SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, "42-99", null)));
    }

    [Fact]
    public void Lookup_ComparesTheOwnSalaryAgainstTheBand()
    {
        var reference = SalaryBenchmarkDataset.Lookup(new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, "3-5", null));
        Assert.Null(reference.Comparison);

        var below = SalaryBenchmarkDataset.Lookup(
            new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, "3-5", reference.AnnualGrossP25 - 1000m));
        var above = SalaryBenchmarkDataset.Lookup(
            new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, "3-5", reference.AnnualGrossP75 + 1000m));
        var atMedian = SalaryBenchmarkDataset.Lookup(
            new SalaryBenchmarkQuery("softwareentwicklung", "DE", 2024, "3-5", reference.AnnualGrossMedian));

        Assert.Equal("below-p25", below.Comparison!.Position);
        Assert.True(below.Comparison.DeltaToMedian < 0m);
        Assert.Equal("above-p75", above.Comparison!.Position);
        Assert.Equal("median-to-p75", atMedian.Comparison!.Position);
        Assert.Equal(0m, atMedian.Comparison.DeltaToMedian);
        Assert.Equal(100m, atMedian.Comparison.PercentOfMedian);
    }

    // ---------------------------------------------------------------- series

    [Fact]
    public void Series_ReturnsEveryAvailableYearInOrder()
    {
        var data = SalaryBenchmarkDataset.Data;

        var series = SalaryBenchmarkDataset.Series("fachinformatiker-anwendungsentwicklung", "BW", "3-5");

        Assert.Equal(data.Years, series.Points.Select(p => p.Year));
        Assert.All(series.Points, point =>
        {
            Assert.True(point.AnnualGrossMedian > 0m);
            Assert.True(point.Derived);
            Assert.True(point.AnnualGrossP25 < point.AnnualGrossMedian);
            Assert.True(point.AnnualGrossP75 > point.AnnualGrossMedian);
        });
        Assert.Equal("BW", series.StateKey);
        Assert.False(string.IsNullOrWhiteSpace(series.Disclaimer));
    }

    // ---------------------------------------------------------------- full sweep

    [Fact]
    public void EveryProfessionStateYearAndBandCombinationResolvesHonestly()
    {
        var data = SalaryBenchmarkDataset.Data;
        var checked_ = 0;

        foreach (var profession in data.Professions)
        foreach (var state in data.States)
        foreach (var year in data.Years)
        foreach (var band in data.ExperienceBands)
        {
            var record = SalaryBenchmarkDataset.Lookup(
                new SalaryBenchmarkQuery(profession.Key, state.Key, year, band.Key, null));

            Assert.False(record.Measured, $"{profession.Key}/{state.Key}/{year}/{band.Key} claims measured data");
            Assert.True(record.Derived, $"{profession.Key}/{state.Key}/{year}/{band.Key} does not admit it is derived");
            Assert.Equal(SalaryBenchmarkDataset.QualityDerivedSpread, record.QuartileQuality);
            Assert.True(record.AnnualGrossMedian > 0m);
            Assert.True(record.AnnualGrossP25 < record.AnnualGrossMedian);
            Assert.True(record.AnnualGrossP75 > record.AnnualGrossMedian);
            Assert.NotEmpty(record.SourceKeys);
            Assert.False(string.IsNullOrWhiteSpace(record.SourceLabel));
            Assert.NotEmpty(record.DerivationNotes);
            Assert.Equal(year, record.Year);
            Assert.False(record.YearFallbackApplied);
            Assert.False(record.StateFallbackApplied);

            if (state.Key != data.NationalStateKey)
                Assert.Contains(record.Quality, new[]
                {
                    SalaryBenchmarkDataset.QualityDerivedRegional,
                    SalaryBenchmarkDataset.QualityDerivedYearRegional
                });

            checked_++;
        }

        Assert.True(checked_ > 10_000, $"expected a broad sweep, only checked {checked_}");
    }

    [Fact]
    public void MetadataCarriesTheDisclaimerAndTheVerificationStatus()
    {
        // Both exist to keep the UI honest about what this data is. A field that is present in the JSON
        // but missing from the DTO is dropped silently at deserialization and can never be shown - which
        // is exactly what happened to VerificationStatus.
        var metadata = SalaryBenchmarkDataset.Metadata();

        Assert.False(string.IsNullOrWhiteSpace(metadata.Disclaimer));
        Assert.False(
            string.IsNullOrWhiteSpace(metadata.VerificationStatus),
            "the dataset's verification status must reach the API, or the UI cannot state which anchors " +
            "were actually checked against their sources");
        Assert.Contains("nicht", metadata.VerificationStatus!, StringComparison.OrdinalIgnoreCase);
    }
}
