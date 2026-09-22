using System.Net;
using System.Net.Sockets;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Die Regeln fuer jeden Abruf, den diese Instanz aufgrund von Nutzerdaten macht (#176).
///
/// Es gibt zwei davon - das Logo einer Marke und die Seite eines Anbieters - und sie teilen sich
/// dieselbe Gefahr: ein Haendlername kommt aus einer Buchung, eine Buchung kommt von aussen. Wer den
/// Namen schreibt, schreibt mittelbar an der Eingabe der KI mit, und was sie antwortet, darf keine
/// Adresse sein, die diese Instanz irgendwohin schickt.
///
/// Deshalb stehen die Regeln hier einmal und nicht zweimal.
/// </summary>
public static class PublicWebAddress
{
    /// <summary>
    /// Eine Domain wie <c>rewe.de</c>. Alles andere - Schema, Pfad, Abfrage, Anmeldedaten, Port - ist
    /// nicht erlaubt: was die KI nennt, ist ein Name, keine Adresse.
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

    /// <summary>
    /// Jede Adresse hinter dem Namen muss oeffentlich sein. Geprueft werden ALLE, nicht nur die erste:
    /// ein Name, der auf eine oeffentliche und eine private Adresse zeigt, waere sonst ein Weg ins
    /// eigene Netz, je nachdem welche der Client nimmt.
    /// </summary>
    public static async Task<bool> IsPubliclyRoutableAsync(string domain, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(domain, ct); }
        catch (Exception exception) when (exception is SocketException or ArgumentException) { return false; }
        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    /// <summary>
    /// Ob diese Adresse im oeffentlichen Netz liegt. Auch der Verbindungsaufbau braucht sie: der Name
    /// wird vor dem Abruf aufgeloest UND spaeter vom Client noch einmal, und dazwischen kann sich die
    /// Antwort aendern. Ein Name, der einmal oeffentlich und beim zweiten Mal 127.0.0.1 beantwortet
    /// wird, ist ein bekannter Trick - deshalb prueft der Verbindungsaufbau die Adresse, zu der er
    /// wirklich verbindet (siehe BackendApplication, ConnectCallback).
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
            0 => false,                                     // "dieses Netz"
            10 => false,                                    // privat
            127 => false,                                   // loopback
            169 when bytes[1] == 254 => false,              // link-local, inklusive der Metadaten-Adresse
            172 when bytes[1] is >= 16 and <= 31 => false,  // privat
            192 when bytes[1] == 168 => false,              // privat
            100 when bytes[1] is >= 64 and <= 127 => false, // Carrier-NAT
            >= 224 => false,                                // Multicast und reserviert
            _ => true
        };
    }

    /// <summary>
    /// Liest hoechstens <paramref name="maxBytes"/> - und keinen Deut mehr. Eine Antwort ohne
    /// Content-Length koennte sonst endlos liefern, und der Speicher waere weg, bevor irgendjemand
    /// etwas davon haette.
    /// </summary>
    public static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }
}
