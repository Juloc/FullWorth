using System.Net;
using System.Net.Sockets;

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
///   <item><b>Nur https, nur oeffentliche Adressen.</b> Vor dem Verbinden wird der Name aufgeloest und
///         jede Adresse geprueft: loopback, privates Netz, link-local und die Metadaten-Adressen der
///         Cloud-Anbieter sind ausgeschlossen. Sonst waere "hol das Logo von x" ein Weg, den Port 8080
///         im eigenen Netz abzufragen.</item>
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

    /// <summary>
    /// Eine Domain wie <c>rewe.de</c>. Alles andere - Schema, Pfad, Abfrage, Anmeldedaten, Port - ist
    /// hier nicht erlaubt: was die KI nennt, ist ein Name, keine Adresse.
    /// </summary>
    public static string? NormalizeDomain(string? value)
    {
        var domain = value?.Trim().ToLowerInvariant().TrimEnd('.');
        if (string.IsNullOrEmpty(domain) || domain.Length > 253) return null;
        if (domain.Contains('/') || domain.Contains('@') || domain.Contains(':') ||
            domain.Contains('?') || domain.Contains('#') || domain.Contains(' ')) return null;
        if (!domain.Contains('.')) return null;
        if (IPAddress.TryParse(domain, out _)) return null;

        var labels = domain.Split('.');
        if (labels.Length < 2 || labels.Any(label =>
                label.Length is 0 or > 63 ||
                label.StartsWith('-') || label.EndsWith('-') ||
                !label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')))
            return null;
        // Eine reine Zifferngruppe am Ende waere keine Top-Level-Domain.
        return labels[^1].All(char.IsAsciiDigit) ? null : domain;
    }

    public async Task<BrandLogoFetch> FetchAsync(string domain, CancellationToken ct)
    {
        var normalized = NormalizeDomain(domain);
        if (normalized is null) return new BrandLogoFetch(BrandLogoFetch.Rejected, null, null, null);

        if (!await IsPubliclyRoutableAsync(normalized, ct))
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

                var bytes = await ReadBoundedAsync(response, timeout.Token);
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

    /// <summary>
    /// Liest hoechstens so viel, wie ein Logo sein darf - und keinen Deut mehr. Eine Antwort ohne
    /// Content-Length koennte sonst endlos liefern.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > BrandAssetVerifier.MaximumAssetBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>
    /// Jede Adresse hinter dem Namen muss oeffentlich sein. Geprueft werden ALLE, nicht nur die erste:
    /// ein Name, der auf eine oeffentliche und eine private Adresse zeigt, waere sonst ein Weg ins
    /// eigene Netz, je nachdem welche der Client nimmt.
    /// </summary>
    private static async Task<bool> IsPubliclyRoutableAsync(string domain, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(domain, ct); }
        catch (Exception exception) when (exception is SocketException or ArgumentException) { return false; }
        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    /// <summary>
    /// Ob diese Adresse im oeffentlichen Netz liegt. Oeffentlich, weil auch der Verbindungsaufbau sie
    /// braucht: der Name wird hier aufgeloest UND spaeter vom Client noch einmal, und dazwischen kann
    /// sich die Antwort aendern. Ein Name, der einmal oeffentlich und beim zweiten Mal 127.0.0.1
    /// beantwortet wird, ist ein bekannter Trick - deshalb prueft der Verbindungsaufbau die Adresse,
    /// zu der er wirklich verbindet (siehe BackendApplication, ConnectCallback).
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            // Eindeutig lokale Adressen (fc00::/7) und die eingebettete IPv4-Adresse mitpruefen.
            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC) return false;
            return !address.IsIPv4MappedToIPv6 || IsPublic(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 => false,                                  // "dieses Netz"
            10 => false,                                 // privat
            127 => false,                                // loopback
            169 when bytes[1] == 254 => false,           // link-local, inklusive der Metadaten-Adresse
            172 when bytes[1] is >= 16 and <= 31 => false, // privat
            192 when bytes[1] == 168 => false,           // privat
            100 when bytes[1] is >= 64 and <= 127 => false, // Carrier-NAT
            >= 224 => false,                             // Multicast und reserviert
            _ => true
        };
    }
}
