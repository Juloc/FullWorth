using System.Net;
using System.Text;
using FullWorth.Backend.Modules.Intelligence;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Logo-Recherche ohne Cloud (#176) - die Grenzen, nicht der Glücksfall.
///
/// Das Issue nennt vier Fragen als Produktentscheidungen: welche Ziele, was darf hinaus, was kommt
/// zurueck, wie oft. Drei davon stehen als Code in <see cref="BrandLogoFetcher"/> und
/// <see cref="BrandAssetVerifier"/>, und genau die pruefen diese Tests - nicht die Absicht.
///
/// Der gefaehrliche Fall ist konkret: ein Haendlername kommt aus einer Buchung, eine Buchung kommt von
/// aussen. Wer den Namen schreibt, schreibt mittelbar an der Eingabe der KI mit. Was sie antwortet,
/// darf deshalb keine Adresse sein, die diese Instanz irgendwohin schickt.
/// </summary>
public sealed class BrandLogoResearchTests
{
    /// <summary>
    /// Was die KI nennt, ist ein Name - keine Adresse. Alles, womit sich ein Ziel umlenken liesse,
    /// faellt hier durch, bevor irgendetwas abgerufen wird.
    /// </summary>
    [Theory]
    [InlineData("https://rewe.de")]            // Schema
    [InlineData("rewe.de/logo.svg")]           // Pfad
    [InlineData("rewe.de:8080")]               // Port
    [InlineData("user@rewe.de")]               // Anmeldedaten
    [InlineData("rewe.de?x=1")]                // Abfrage
    [InlineData("rewe.de#x")]                  // Fragment
    [InlineData("127.0.0.1")]                  // Adresse statt Name
    [InlineData("[::1]")]
    [InlineData("localhost")]                  // keine Punkte, keine Top-Level-Domain
    [InlineData("169.254.169.254")]            // Metadaten der Cloud-Anbieter
    [InlineData("rewe .de")]
    [InlineData("-rewe.de")]
    [InlineData("rewe.de.")]                   // faengt der Punkt am Ende noch ab? (normalisiert)
    [InlineData("")]
    [InlineData(null)]
    public void Only_a_bare_domain_is_accepted(string? value)
    {
        var normalized = BrandLogoFetcher.NormalizeDomain(value);

        // "rewe.de." ist derselbe Name mit abschliessendem Wurzelpunkt und darf durch - alles andere nicht.
        if (value == "rewe.de.") Assert.Equal("rewe.de", normalized);
        else Assert.Null(normalized);
    }

    [Theory]
    [InlineData("REWE.de", "rewe.de")]
    [InlineData("  logo.brand.co.uk  ", "logo.brand.co.uk")]
    public void A_bare_domain_survives_normalization(string value, string expected) =>
        Assert.Equal(expected, BrandLogoFetcher.NormalizeDomain(value));

    /// <summary>
    /// Ein Name, der im eigenen Netz landet, wird gar nicht erst abgerufen. Ohne diese Pruefung waere
    /// "hol das Logo von x" ein Weg, den Port 8080 nebenan abzufragen - klassisch SSRF.
    /// </summary>
    [Theory]
    [InlineData("localhost.localdomain")]
    public async Task A_name_that_resolves_into_the_own_network_is_never_fetched(string domain)
    {
        var calls = 0;
        var fetcher = new BrandLogoFetcher(new HttpClient(new CountingHandler(() => calls++)));

        var result = await fetcher.FetchAsync(domain, CancellationToken.None);

        Assert.Equal(BrandLogoFetch.Rejected, result.Outcome);
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// Der Pfad kommt von FullWorth, nicht aus der Antwort. Und es ist immer https - ein Logo ueber
    /// http koennte unterwegs ausgetauscht werden.
    /// </summary>
    [Fact]
    public async Task The_path_is_built_here_and_the_scheme_is_always_https()
    {
        var urls = new List<string>();
        var fetcher = new BrandLogoFetcher(new HttpClient(new CountingHandler(null, request =>
        {
            urls.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));

        await fetcher.FetchAsync("example.com", CancellationToken.None);

        Assert.NotEmpty(urls);
        Assert.All(urls, url => Assert.StartsWith("https://example.com/", url, StringComparison.Ordinal));
        Assert.All(urls, url => Assert.EndsWith(".svg", url, StringComparison.Ordinal));
    }

    /// <summary>
    /// Nur ein SVG. Eine HTML-Seite, die sich als Bild ausgibt, wird nicht angenommen - und ein
    /// korrekt deklariertes PNG auch nicht: die Marken-Ablage kennt nur SVG, und beim Ausliefern wird
    /// erneut geprueft.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("image/png")]
    [InlineData(null)]
    public async Task Anything_that_is_not_an_svg_is_ignored(string? mediaType)
    {
        var fetcher = new BrandLogoFetcher(new HttpClient(new CountingHandler(null, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray())
            };
            response.Content.Headers.ContentType = mediaType is null ? null : new(mediaType);
            return response;
        })));

        var result = await fetcher.FetchAsync("example.com", CancellationToken.None);

        Assert.Equal(BrandLogoFetch.NotFound, result.Outcome);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public async Task A_correct_svg_comes_back()
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><circle r=\"4\"/></svg>";
        var fetcher = new BrandLogoFetcher(new HttpClient(new CountingHandler(null, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(svg))
            };
            response.Content.Headers.ContentType = new("image/svg+xml");
            return response;
        })));

        var result = await fetcher.FetchAsync("example.com", CancellationToken.None);

        Assert.Equal(BrandLogoFetch.Ok, result.Outcome);
        Assert.Equal(svg, Encoding.UTF8.GetString(result.Bytes!));
        // Und es kommt durch dieselbe Haertung wie ein Logo aus einem signierten Paket.
        var verified = BrandAssetVerifier.VerifySvg(result.Bytes!, result.MediaType);
        Assert.Equal("image/svg+xml", verified.MediaType);
    }

    /// <summary>
    /// Ein Logo ist klein. Eine Antwort ohne Content-Length koennte sonst endlos liefern, und der
    /// Speicher waere weg, bevor irgendjemand etwas davon haette.
    /// </summary>
    [Fact]
    public async Task An_oversized_answer_is_dropped_instead_of_read_to_the_end()
    {
        var fetcher = new BrandLogoFetcher(new HttpClient(new CountingHandler(null, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndlessStream())
            };
            response.Content.Headers.ContentType = new("image/svg+xml");
            return response;
        })));

        var result = await fetcher.FetchAsync("example.com", CancellationToken.None);

        Assert.Equal(BrandLogoFetch.NotFound, result.Outcome);
        Assert.Null(result.Bytes);
    }

    /// <summary>
    /// Eine Umleitung waere die frei gewaehlte Adresse durch die Hintertuer: die Pruefung gilt dem
    /// Namen, den die KI genannt hat, nicht dem, zu dem der Server danach weiterschickt.
    /// </summary>
    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        var seen = new List<string>();
        var fetcher = new BrandLogoFetcher(new HttpClient(new CountingHandler(null, request =>
        {
            seen.Add(request.RequestUri!.Host);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://127.0.0.1:8080/logo.svg");
            return response;
        })));

        var result = await fetcher.FetchAsync("example.com", CancellationToken.None);

        Assert.Equal(BrandLogoFetch.NotFound, result.Outcome);
        Assert.All(seen, host => Assert.Equal("example.com", host));
    }

    /// <summary>
    /// Der Schluessel, unter dem die Oberflaeche ein Logo findet: Grossbuchstaben ohne diakritische
    /// Zeichen, alles andere ein Leerzeichen. Das ist genau die Regel, mit der features/ux-kit.js den
    /// Haendlernamen einer Buchung vergleicht - sonst faende ein Alias mit "Ä" nie seinen Haendler.
    /// </summary>
    [Theory]
    [InlineData("REWE Markt GmbH", "REWE MARKT GMBH")]
    [InlineData("Café Größer", "CAFE GROSSER")]
    [InlineData("  dm-drogerie  markt ", "DM DROGERIE MARKT")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void The_alias_key_matches_what_the_interface_compares_against(string? value, string? expected) =>
        Assert.Equal(expected, BrandAliasKey.Of(value));

    /// <summary>
    /// Die Versuchsliste gehoert der Instanz und ueberlebt das Loeschen eines Kontos. Deshalb steht
    /// dort der Hash des Namens und nicht der Name: eine Gegenpartei ist nicht immer eine Firma, und
    /// eine Ueberweisung von "Max Mustermann" haette sonst dauerhaft einen Personennamen hinterlassen,
    /// den niemand mehr loeschen kann.
    /// </summary>
    [Fact]
    public void The_attempt_list_remembers_a_hash_and_not_the_name()
    {
        var hash = BrandLogoResearchService.HashOf("MAX MUSTERMANN");

        Assert.Equal(64, hash.Length);
        Assert.All(hash, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.DoesNotContain("MUSTERMANN", hash, StringComparison.OrdinalIgnoreCase);
        // Derselbe Name, derselbe Hash - sonst waere die Frage "schon versucht?" nicht zu beantworten.
        Assert.Equal(hash, BrandLogoResearchService.HashOf("MAX MUSTERMANN"));
        Assert.NotEqual(hash, BrandLogoResearchService.HashOf("MAX MUSTERMANM"));
    }

    /// <summary>
    /// Die Adresspruefung selbst, Adresse fuer Adresse. Sie steht oeffentlich, weil der
    /// Verbindungsaufbau sie ein zweites Mal braucht: der Name wird vor dem Abruf aufgeloest UND vom
    /// Client noch einmal, und dazwischen kann sich die Antwort aendern.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.3.9", false)]
    [InlineData("172.31.255.254", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("172.15.255.254", true)]
    public void Only_public_addresses_are_allowed(string address, bool expected) =>
        Assert.Equal(expected, BrandLogoFetcher.IsPublic(System.Net.IPAddress.Parse(address)));

    private sealed class CountingHandler(
        Action? onSend = null,
        Func<HttpRequestMessage, HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend?.Invoke();
            return Task.FromResult(answer?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'x', offset, count); return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
