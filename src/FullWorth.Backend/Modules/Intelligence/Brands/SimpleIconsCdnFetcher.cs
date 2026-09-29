using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Intelligence.Brands;

/// <summary>Was ein Abruf beim Icon-Spiegel ergeben hat. <see cref="Bytes"/> ist nur bei <c>ok</c> gefuellt.</summary>
public sealed record SimpleIconsFetch(string Outcome, string? Url, byte[]? Bytes)
{
    public const string Ok = "ok";

    /// <summary>Die Marke gibt es dort nicht. Eine Antwort, kein Fehler.</summary>
    public const string NotFound = "not_found";

    /// <summary>Der Kurzname hat die Form nicht erfuellt und wurde gar nicht erst abgeschickt.</summary>
    public const string Rejected = "rejected";

    /// <summary>Netz oder Zeitueberschreitung. Sagt nichts ueber die Marke.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>
    /// 429 oder 5xx. Sagt ebenfalls nichts ueber die Marke, aber etwas ueber uns: der ganze
    /// Durchlauf hoert danach auf, statt es fuer die naechsten vierundzwanzig Haendler auch noch
    /// zu versuchen.
    /// </summary>
    public const string Throttled = "throttled";
}

/// <summary>
/// Die dritte Sprosse der Logoleiter: der Kurzname, den die Instanz sich selbst ausgerechnet hat,
/// wird beim Simple-Icons-Spiegel nachgeschlagen.
///
/// <b>Das hat eine Datenschutzfolge, und die gehoert ausgesprochen.</b> Der aus dem Haendlernamen
/// abgeleitete Kurzname verlaesst die Maschine. Bei einer Kette ist das harmlos - <c>REWE SAGT
/// DANKE</c> wird zu <c>rewe</c>, und dass jemand irgendwo bei REWE einkauft, ist keine Nachricht.
/// Bei einem Einzelunternehmer ist es das nicht: <c>MUELLER FLIESENLEGER</c> wird zu
/// <c>muellerfliesenleger</c>, und die Adresse *ist* die Nutzlast. Ein Logo laesst sich nicht
/// holen, ohne den Namen zu nennen.
///
/// Deshalb ist das eine Sprosse und keine Selbstverstaendlichkeit: sie ist in den Einstellungen
/// abschaltbar, sie laeuft NACH der Ableitung - was im Abbild liegt, wird nie nachgeschlagen -
/// und der Text neben dem Schalter sagt genau das, was oben steht.
///
/// Wer den Schalter liest, steht eine Ebene hoeher: diese Datei kennt keine Einstellungszeile und
/// keinen Anbieter. Sie holt eine Adresse, die ihr genannt wurde, und sonst nichts - das ist es,
/// was <c>AutopilotArchitectureGuardTests</c> fuer den Ordner <c>Brands/</c> festhaelt.
///
/// Der Weg ist so eng wie beim <see cref="BrandLogoFetcher"/>, aber aus einem anderen Grund: dort
/// nennt eine KI die Adresse, hier ist der Wirt fest und nur der letzte Pfadabschnitt variabel.
///
/// <list type="number">
///   <item><b>Ein Wirt, eine Fassung.</b> Die Fassung kommt aus dem mitgelieferten Katalog
///         (<see cref="BundledBrandCatalog.SimpleIconsVersion"/>), nicht aus einer zweiten
///         Konstante - sonst holt die Instanz Bilder aus einer anderen Menge als die, die sie
///         schon hat, und niemand merkt es.</item>
///   <item><b>Der Kurzname muss die Form erfuellen</b>, sonst wird er gar nicht erst abgeschickt.
///         Simple-Icons-Kurznamen sind Kleinbuchstaben und Ziffern, mehr nicht. Das ist hier kein
///         Sauberkeitsanspruch, sondern die Grenze dessen, was die Maschine verlaesst.</item>
///   <item><b>Keine Umleitung.</b> Der Client folgt keiner; eine Umleitung waere die frei
///         gewaehlte Adresse durch die Hintertuer.</item>
///   <item><b>Begrenzte Menge und begrenzte Zeit.</b> Ein Icon ist ein bis zwei Kilobyte.</item>
/// </list>
///
/// Was zurueckkommt, prueft <see cref="BrandAssetVerifier"/> - dieselbe Haertung wie bei jedem
/// anderen Logo. Der Spiegel wird nicht deshalb geglaubt, weil er der Spiegel ist.
/// </summary>
public sealed class SimpleIconsCdnFetcher(HttpClient httpClient)
{
    /// <summary>
    /// Die Form eines Simple-Icons-Kurznamens. Absichtlich enger als alles, was
    /// <see cref="BrandSlugDerivation.NormalizeSlug"/> erzeugen kann: was nicht hineinpasst,
    /// verlaesst die Maschine nicht.
    /// </summary>
    private static readonly Regex SlugShape = new("^[a-z0-9]{2,60}$", RegexOptions.Compiled);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>Die Adresse zu einem Kurznamen, oder <c>null</c>, wenn er die Form nicht erfuellt.</summary>
    public static string? UrlFor(string slug) =>
        SlugShape.IsMatch(slug)
            ? $"https://cdn.jsdelivr.net/npm/simple-icons@{BundledBrandCatalog.SimpleIconsVersion}/icons/{slug}.svg"
            : null;

    public async Task<SimpleIconsFetch> FetchAsync(string slug, CancellationToken ct)
    {
        var url = UrlFor(slug);
        if (url is null) return new SimpleIconsFetch(SimpleIconsFetch.Rejected, null, null);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("image/svg+xml");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            // Zu viele Anfragen oder ein Fehler auf deren Seite: das liegt nicht an dieser Marke.
            // Der Aufrufer hoert danach auf - ein Spiegel, der gerade nicht mag, wird nicht
            // fuenfundzwanzigmal gefragt.
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                return new SimpleIconsFetch(SimpleIconsFetch.Throttled, url, null);

            // 404 heisst: diese Marke fuehrt Simple Icons nicht. Das ist die haeufigste Antwort und
            // eine gueltige - rund drei Viertel der deutschen Haendler stehen dort nicht.
            if (!response.IsSuccessStatusCode)
                return new SimpleIconsFetch(SimpleIconsFetch.NotFound, url, null);

            if (response.Content.Headers.ContentLength > BrandAssetVerifier.MaximumAssetBytes)
                return new SimpleIconsFetch(SimpleIconsFetch.NotFound, url, null);

            var bytes = await PublicWebAddress.ReadBoundedAsync(
                response, BrandAssetVerifier.MaximumAssetBytes, timeout.Token);
            return bytes is null
                ? new SimpleIconsFetch(SimpleIconsFetch.NotFound, url, null)
                : new SimpleIconsFetch(SimpleIconsFetch.Ok, url, bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return new SimpleIconsFetch(SimpleIconsFetch.Unreachable, url, null);
        }
    }
}
