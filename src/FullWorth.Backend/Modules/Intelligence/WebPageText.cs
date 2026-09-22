using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Der lesbare Text einer Webseite (#176).
///
/// Was hier herauskommt, geht anschliessend an eine KI - und ist damit die eine Zutat des ganzen
/// Systems, die weder vom Nutzer noch von FullWorth stammt. Entsprechend wenig darf sie sein: kein
/// Skript, kein Stil, keine Kommentare, kein Markup, und begrenzt in der Laenge.
///
/// Das macht die Seite nicht vertrauenswuerdig - eine Webseite kann Saetze enthalten, die wie
/// Anweisungen aussehen. Dagegen hilft kein Filter, sondern nur, dass der Aufrufer sie als Daten
/// kennzeichnet und die Antwort auf ein Schema festlegt (siehe <see cref="InternetResearchService"/>).
/// </summary>
public static partial class WebPageText
{
    [GeneratedRegex(@"<(script|style|template|noscript|svg)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RemovableBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Title();

    /// <summary>
    /// Der Titel der Seite - meist der Name, unter dem sich ein Anbieter selbst nennt, und damit
    /// besser als der Text auf einem Kontoauszug. Null, wenn die Seite keinen hat.
    /// </summary>
    public static string? ExtractTitle(string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        try
        {
            var match = Title().Match(html);
            if (!match.Success) return null;
            var text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(match.Groups[1].Value, " ")), " ").Trim();
            if (text.Length == 0) return null;
            return text.Length <= maxChars ? text : text[..maxChars];
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>
    /// Null, wenn nichts Lesbares uebrig bleibt. Die Laengenbegrenzung ist kein Schoenheitsmittel: was
    /// an die KI geht, wird bezahlt, und eine Startseite ist selten dort interessant, wo sie lang wird.
    /// </summary>
    public static string? Extract(string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        string stripped;
        try
        {
            stripped = Tags().Replace(Comments().Replace(RemovableBlocks().Replace(html, " "), " "), " ");
        }
        catch (RegexMatchTimeoutException)
        {
            // Eine Seite, die sich nicht in zwei Sekunden entkleiden laesst, ist keine, die wir lesen.
            return null;
        }

        var text = Whitespace().Replace(WebUtility.HtmlDecode(stripped), " ").Trim();
        if (text.Length == 0) return null;
        return text.Length <= maxChars ? text : text[..maxChars];
    }
}

/// <summary>
/// Holt den Text einer Startseite - derselbe enge Weg wie beim Logo (<see cref="PublicWebAddress"/>),
/// nur mit einem anderen Inhaltstyp.
///
/// Geholt wird ausschliesslich die WURZEL der Domain. Kein Pfad aus der Antwort der KI, kein Folgen
/// von Verweisen, keine zweite Seite: sobald ein Ziel aus dem Ergebnis eines Modells kommt, ist es
/// kein begrenzter Nachschlag mehr, sondern ein Browser - und genau das schliesst #176 aus.
/// </summary>
public sealed class WebPageFetcher(HttpClient httpClient)
{
    /// <summary>Mehr ist keine Startseite, sondern ein Download.</summary>
    private const int MaximumBytes = 512 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<(string Outcome, string? Url, string? Html)> FetchHomepageAsync(string domain, CancellationToken ct)
    {
        var normalized = PublicWebAddress.NormalizeDomain(domain);
        if (normalized is null) return (BrandLogoFetch.Rejected, null, null);
        if (!await PublicWebAddress.IsPubliclyRoutableAsync(normalized, ct)) return (BrandLogoFetch.Rejected, null, null);

        var url = $"https://{normalized}/";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return (BrandLogoFetch.NotFound, url, null);

            var mediaType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
            if (mediaType is not ("text/html" or "application/xhtml+xml")) return (BrandLogoFetch.NotFound, url, null);
            if (response.Content.Headers.ContentLength > MaximumBytes) return (BrandLogoFetch.NotFound, url, null);

            var bytes = await PublicWebAddress.ReadBoundedAsync(response, MaximumBytes, timeout.Token);
            if (bytes is null) return (BrandLogoFetch.NotFound, url, null);

            var charset = response.Content.Headers.ContentType?.CharSet;
            return (BrandLogoFetch.Ok, url, Decode(bytes, charset));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return (BrandLogoFetch.Unreachable, url, null);
        }
    }

    /// <summary>
    /// UTF-8 ist die Annahme, und eine falsche Annahme darf nicht werfen: was sich nicht dekodieren
    /// laesst, wird ersetzt statt abgebrochen - der Text ist ohnehin nur eine Leseprobe.
    /// </summary>
    private static string Decode(byte[] bytes, string? charset)
    {
        var name = charset?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(name))
        {
            try { return Encoding.GetEncoding(name, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetString(bytes); }
            catch (ArgumentException) { /* unbekannte Angabe - dann eben UTF-8 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
