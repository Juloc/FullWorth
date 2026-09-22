namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>Was ein Abrufversuch ergeben hat. <see cref="Bytes"/> ist nur bei <c>ok</c> gefuellt.</summary>
public sealed record BrandLogoFetch(string Outcome, string? Url, byte[]? Bytes, string? MediaType)
{
    public const string Ok = "ok";
    public const string NotFound = "not_found";
    public const string Rejected = "rejected";
    public const string Unreachable = "unreachable";
}

/// <summary>
/// Holt ein Logo von der Domain einer Marke (#176).
///
/// Das ist die einzige Stelle, an der diese Instanz eine Adresse abruft, die mittelbar aus Nutzerdaten
/// stammt - ein Haendlername kommt aus einer Buchung, und eine Buchung kommt von aussen. Entsprechend
/// eng ist der Weg:
///
/// <list type="number">
///   <item><b>Die KI nennt eine Domain, keine Adresse.</b> Den Pfad baut FullWorth selbst aus einer
///         festen Liste ueblicher Orte. Eine frei gewaehlte URL waere genau der Hebel, den eine
///         Prompt-Injektion in einem Verwendungszweck sucht.</item>
///   <item><b>Nur https, nur oeffentliche Adressen.</b> Die Regeln dafuer stehen in
///         <see cref="PublicWebAddress"/> - sie gelten fuer jeden Abruf, den diese Instanz aufgrund
///         von Nutzerdaten macht, und nicht nur fuer Logos.</item>
///   <item><b>Keine Umleitung.</b> Eine Umleitung waere die frei gewaehlte Adresse durch die
///         Hintertuer.</item>
///   <item><b>Begrenzte Menge und begrenzte Zeit.</b> Ein Logo ist klein; alles Grosse ist kein Logo.</item>
/// </list>
///
/// Was zurueckkommt, prueft <see cref="BrandAssetVerifier"/> - dieselbe Haertung wie bei einem Logo aus
/// einem signierten Paket. Angenommen wird deshalb nur SVG: das ist keine Einschraenkung dieser
/// Funktion, sondern das Format, das die Marken-Ablage kennt und das beim Ausliefern erneut geprueft
/// wird. Eine Marke ohne sicheres SVG bekommt kein Logo - eine ehrliche Antwort.
/// </summary>
public sealed class BrandLogoFetcher(HttpClient httpClient)
{
    /// <summary>Uebliche Orte eines Marken-SVG. Der Pfad kommt von hier, nie aus der Antwort der KI.</summary>
    private static readonly string[] Paths =
    [
        "/favicon.svg",
        "/logo.svg",
        "/assets/logo.svg",
        "/static/logo.svg",
        "/images/logo.svg",
        "/apple-touch-icon.svg"
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public async Task<BrandLogoFetch> FetchAsync(string domain, CancellationToken ct)
    {
        var normalized = PublicWebAddress.NormalizeDomain(domain);
        if (normalized is null) return new BrandLogoFetch(BrandLogoFetch.Rejected, null, null, null);

        if (!await PublicWebAddress.IsPubliclyRoutableAsync(normalized, ct))
            return new BrandLogoFetch(BrandLogoFetch.Rejected, null, null, null);

        var reachedAnything = false;
        foreach (var path in Paths)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"https://{normalized}{path}";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("image/svg+xml");
                using var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                reachedAnything = true;

                // Eine Umleitung waere die frei gewaehlte Adresse durch die Hintertuer - der Client
                // folgt keiner, und hier wird sie nicht von Hand nachgebaut.
                if (!response.IsSuccessStatusCode) continue;

                var mediaType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
                if (mediaType != "image/svg+xml") continue;
                if (response.Content.Headers.ContentLength > BrandAssetVerifier.MaximumAssetBytes) continue;

                var bytes = await PublicWebAddress.ReadBoundedAsync(
                    response, BrandAssetVerifier.MaximumAssetBytes, timeout.Token);
                if (bytes is null) continue;

                return new BrandLogoFetch(BrandLogoFetch.Ok, url, bytes, mediaType);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
            {
                // Ein Ort, den es nicht gibt, ist kein Fehler - es gibt fuenf weitere.
            }
        }

        return new BrandLogoFetch(
            reachedAnything ? BrandLogoFetch.NotFound : BrandLogoFetch.Unreachable, null, null, null);
    }
}
