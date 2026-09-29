using FullWorth.Backend.Modules.Intelligence.Brands;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class BrandSlugDerivationTests
{
    /// <summary>
    /// Gefaltet, nicht transliteriert: aus "ü" wird "u" und nicht "ue". So schreibt der
    /// Simple-Icons-Katalog seine Kurznamen, und danach richtet sich diese Funktion - eine deutsche
    /// Umschrift traefe dort nichts. Das scharfe S ist die Ausnahme, weil es keine gefaltete Form
    /// hat: es wird zu "ss", sonst fiele es als Nicht-ASCII ersatzlos weg.
    /// </summary>
    [Theory]
    [InlineData("Deutsche Telekom GmbH", "deutschetelekomgmbh")]
    [InlineData("H&M", "handm")]
    [InlineData("Müller Drogerie", "mullerdrogerie")]
    [InlineData("Größer & Söhne", "grosserandsohne")]
    [InlineData("1&1 Telecom", "1and1telecom")]
    public void The_slug_folds_diacritics_and_spells_out_the_ampersand(string name, string expected) =>
        Assert.Equal(expected, BrandSlugDerivation.NormalizeSlug(name));

    /// <summary>
    /// Der Fall, um den es geht: auf der Buchung steht die Gesellschaft, im Katalog die Marke.
    /// Die Liste muss vom Genauesten zum Allgemeinsten laufen, sonst gewinnt der kurze Treffer.
    /// </summary>
    [Fact]
    public void Region_and_legal_form_fall_away_until_the_brand_remains()
    {
        var slugs = BrandSlugDerivation.CandidateSlugs("VODAFONE WEST GMBH").ToList();

        Assert.Equal("vodafonewestgmbh", slugs[0]);
        Assert.Contains("vodafone", slugs);
        Assert.True(
            slugs.IndexOf("vodafonewestgmbh") < slugs.IndexOf("vodafone"),
            "Der genauere Kandidat muss vor dem allgemeineren stehen.");
    }

    [Fact]
    public void The_domain_contributes_its_registrable_label()
    {
        var slugs = BrandSlugDerivation.CandidateSlugs("Acme Services GmbH", "www.acme.example");
        Assert.Contains("acme", slugs);
    }

    [Fact]
    public void No_more_than_twelve_candidates_leave_the_derivation() =>
        Assert.True(BrandSlugDerivation
            .CandidateSlugs("Eins Zwei Drei Vier Fuenf Sechs Sieben Acht Neun Zehn Elf Zwoelf Dreizehn")
            .Count <= BrandSlugDerivation.MaximumCandidates);

    [Fact]
    public void A_duplicate_candidate_appears_once() =>
        Assert.Equal(
            BrandSlugDerivation.CandidateSlugs("REWE GmbH", "rewe.de").Distinct().Count(),
            BrandSlugDerivation.CandidateSlugs("REWE GmbH", "rewe.de").Count);

    // ---- Der Stamm: eine Zeile fuer eine ganze Kette --------------------------------------------

    [Fact]
    public void The_stem_covers_the_whole_chain_instead_of_one_branch() =>
        Assert.Equal("EDEKA", BrandSlugDerivation.UnambiguousStem(
            "EDEKA MARKT 4711 BERLIN", "edeka", _ => null));

    /// <summary>
    /// Trifft der Stamm eine ANDERE bekannte Marke, gibt es keinen Stamm. Das ist die Regel, die
    /// verhindert, dass "AMAZON" auch "AMAZON PRIME" verschluckt - ein Fehler, den der
    /// Kategorisierungskatalog von Hand durch seine Reihenfolge umgeht.
    /// </summary>
    [Fact]
    public void An_ambiguous_stem_yields_nothing() =>
        Assert.Null(BrandSlugDerivation.UnambiguousStem(
            "AMAZON PRIME VIDEO", "primevideo", stem => stem == "AMAZON" ? "amazon" : null));

    [Fact]
    public void A_stem_shorter_than_four_characters_is_refused() =>
        Assert.Null(BrandSlugDerivation.UnambiguousStem("DM", "dm", _ => null));

    [Fact]
    public void A_stem_already_owned_by_the_same_brand_is_fine() =>
        Assert.Equal("VODAFONE", BrandSlugDerivation.UnambiguousStem(
            "VODAFONE WEST GMBH", "vodafone", stem => stem == "VODAFONE" ? "vodafone" : null));
}
