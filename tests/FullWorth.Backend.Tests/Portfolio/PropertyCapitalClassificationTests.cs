using FullWorth.Backend.Modules.Portfolio;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// #174: Instandhaltung oder wertsteigernd - die Frage, an der Gewinn, Wertsteigerung und
/// Eigenkapital haengen.
///
/// Es gibt dafuer eine etablierte Systematik: gleichwertiger Ersatz ist Erhaltung, Standardhebung ist
/// Herstellung. Alte Gastherme raus, neue Gastherme rein - Erhaltung. Gastherme raus, Waermepumpe
/// rein - Herstellung.
///
/// Das laesst sich aus einer Kategorie NICHT ablesen; "heating" ist beides. Deshalb ist alles hier so
/// gebaut, dass die Vermutung nachgibt und der Benutzer entscheidet. Die zwei Faustregeln fragen
/// nach, statt umzubuchen - eine Anwendung, die steuerliche Folgen still aendert, ist an der falschen
/// Stelle klug.
///
/// Reine Rechenlogik, kein Zustand, keine Datenbank.
/// </summary>
public sealed class PropertyCapitalClassificationTests
{
    private static PropertyImprovementFacts Item(
        string category, decimal cost, int year, int month = 6, string? treatment = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), category, cost, new DateOnly(year, month, 1), treatment);

    /// <summary>
    /// Die Vermutung ist konservativ: was nicht offensichtlich den Standard hebt, gilt zuerst als
    /// Erhaltung. Falsch herum waere es schlimmer - eine Ausgabe, die ungefragt das investierte
    /// Kapital erhoeht, macht jede Renditezahl besser, als sie ist.
    /// </summary>
    [Theory]
    [InlineData("heating", PropertyImprovementTreatments.Maintenance)]
    [InlineData("bathroom", PropertyImprovementTreatments.Maintenance)]
    [InlineData("windows", PropertyImprovementTreatments.Maintenance)]
    [InlineData("solar", PropertyImprovementTreatments.ValueIncreasing)]
    [InlineData("insulation", PropertyImprovementTreatments.ValueIncreasing)]
    [InlineData("structural", PropertyImprovementTreatments.ValueIncreasing)]
    public void The_category_only_proposes_a_default(string category, string expected)
    {
        Assert.Equal(expected, PropertyCapitalClassification.DefaultFor(category));
    }

    /// <summary>
    /// Der Benutzer ueberstimmt die Vermutung immer - in beide Richtungen. Eine Voreinstellung, die
    /// sich nicht ueberstimmen laesst, ist keine.
    /// </summary>
    [Theory]
    [InlineData("heating", PropertyImprovementTreatments.ValueIncreasing)]
    [InlineData("solar", PropertyImprovementTreatments.Maintenance)]
    public void What_the_user_set_wins_over_the_default(string category, string chosen)
    {
        Assert.Equal(chosen, PropertyCapitalClassification.Effective(chosen, category));
    }

    /// <summary>
    /// Eine unbekannte Angabe ist keine Angabe. Sie durchzulassen hiesse, dass ein Tippfehler eine
    /// dritte Kategorie erzeugt, die keine Rechnung kennt.
    /// </summary>
    [Fact]
    public void An_unknown_treatment_falls_back_to_the_default()
    {
        Assert.Equal(
            PropertyImprovementTreatments.ValueIncreasing,
            PropertyCapitalClassification.Effective("wertsteigernd", "solar"));
    }

    /// <summary>
    /// Investiertes Kapital: Kaufpreis samt Nebenkosten - Makler, Notar, Grunderwerbsteuer - plus das
    /// Wertsteigernde. Erhaltungsaufwand ist verbraucht, nicht investiert, und zaehlt nicht mit.
    /// </summary>
    [Fact]
    public void Invested_capital_counts_the_purchase_its_costs_and_only_the_value_increasing_work()
    {
        var improvements = new[]
        {
            Item("solar", 20_000m, 2024),                                             // wertsteigernd
            Item("heating", 12_000m, 2024, treatment: PropertyImprovementTreatments.ValueIncreasing),
            Item("bathroom", 8_000m, 2024),                                           // Erhaltung
            Item("roof", 5_000m, 2024)                                                // Erhaltung
        };

        Assert.Equal(
            400_000m + 38_000m + 32_000m,
            PropertyCapitalClassification.InvestedCapital(400_000m, 38_000m, improvements));
    }

    /// <summary>
    /// Drei von vier Kernbereichen in fuenf Jahren - Heizung, Sanitaer, Elektrik, Fenster. Das ist
    /// die Regel, die den Einzelfall in der Praxis am haeufigsten kippt.
    /// </summary>
    [Fact]
    public void Three_of_the_four_core_areas_within_five_years_raises_a_question()
    {
        var improvements = new[]
        {
            Item("heating", 15_000m, 2024),
            Item("plumbing", 9_000m, 2025),
            Item("electrical", 7_000m, 2026)
        };

        var hint = Assert.Single(PropertyCapitalClassification.Hints(improvements, null, null));
        Assert.Equal(PropertyCapitalClassification.ThreeOfFourHint, hint.Code);
        Assert.Equal(3, hint.ImprovementIds.Count);
    }

    /// <summary>
    /// Gezaehlt werden BEREICHE, nicht Massnahmen. Dreimal die Heizung in fuenf Jahren ist ein
    /// Bereich - wer hier Massnahmen zaehlt, meldet bei jedem, der seine Heizung pflegt.
    /// </summary>
    [Fact]
    public void Three_works_on_the_same_area_are_not_three_areas()
    {
        var improvements = new[]
        {
            Item("heating", 4_000m, 2024),
            Item("heating", 3_000m, 2025),
            Item("heating", 2_000m, 2026)
        };

        Assert.Empty(PropertyCapitalClassification.Hints(improvements, null, null));
    }

    /// <summary>Ausserhalb der fuenf Jahre ist es kein Zusammenhang mehr.</summary>
    [Fact]
    public void Core_areas_spread_over_more_than_five_years_stay_quiet()
    {
        var improvements = new[]
        {
            Item("heating", 15_000m, 2018),
            Item("plumbing", 9_000m, 2024),
            Item("electrical", 7_000m, 2026)
        };

        Assert.Empty(PropertyCapitalClassification.Hints(improvements, null, null));
    }

    /// <summary>
    /// Die 15-%-Regel: mehr als 15 % des Gebaeude-Kaufpreises innerhalb von drei Jahren nach dem
    /// Kauf. Sie braucht beides - das Kaufdatum UND den Preis -, sonst gibt es nichts zu messen.
    /// </summary>
    [Fact]
    public void More_than_fifteen_percent_within_three_years_of_the_purchase_raises_a_question()
    {
        var improvements = new[] { Item("bathroom", 35_000m, 2025) };

        var hint = Assert.Single(PropertyCapitalClassification.Hints(
            improvements, new DateOnly(2024, 3, 1), 200_000m));
        Assert.Equal(PropertyCapitalClassification.FifteenPercentHint, hint.Code);
    }

    [Fact]
    public void Just_under_the_threshold_stays_quiet()
    {
        var improvements = new[] { Item("bathroom", 30_000m, 2025) };

        Assert.Empty(PropertyCapitalClassification.Hints(
            improvements, new DateOnly(2024, 3, 1), 200_000m));
    }

    [Fact]
    public void Work_more_than_three_years_after_the_purchase_stays_quiet()
    {
        var improvements = new[] { Item("bathroom", 35_000m, 2029) };

        Assert.Empty(PropertyCapitalClassification.Hints(
            improvements, new DateOnly(2024, 3, 1), 200_000m));
    }

    /// <summary>
    /// Wer bereits wertsteigernd eingestuft hat, braucht keinen Rat, es zu tun. Eine Meldung ohne
    /// Inhalt ist die Art Meldung, die man wegklickt, ohne sie zu lesen - und dann auch die naechste.
    /// </summary>
    [Fact]
    public void Nothing_is_raised_for_work_the_user_already_classified_as_value_increasing()
    {
        var improvements = new[]
        {
            Item("heating", 15_000m, 2024, treatment: PropertyImprovementTreatments.ValueIncreasing),
            Item("plumbing", 9_000m, 2025, treatment: PropertyImprovementTreatments.ValueIncreasing),
            Item("electrical", 7_000m, 2026, treatment: PropertyImprovementTreatments.ValueIncreasing)
        };

        Assert.Empty(PropertyCapitalClassification.Hints(improvements, new DateOnly(2024, 1, 1), 100_000m));
    }

    /// <summary>
    /// Eine laufende Massnahme ist noch keine Tatsache. Ohne Abschlussdatum faellt sie aus beiden
    /// Regeln heraus - sonst zaehlte ein Vorhaben, das noch nicht begonnen hat.
    /// </summary>
    [Fact]
    public void Unfinished_work_counts_for_neither_rule()
    {
        var improvements = new[]
        {
            new PropertyImprovementFacts(Guid.NewGuid(), "heating", 15_000m, null, null),
            new PropertyImprovementFacts(Guid.NewGuid(), "plumbing", 9_000m, null, null),
            new PropertyImprovementFacts(Guid.NewGuid(), "electrical", 7_000m, null, null)
        };

        Assert.Empty(PropertyCapitalClassification.Hints(improvements, new DateOnly(2024, 1, 1), 50_000m));
    }
}
