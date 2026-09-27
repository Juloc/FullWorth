using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.Shared;

/// <summary>
/// Das Feld <c>:86:</c> einer MT940-Buchung, zerlegt (DK-Format der deutschen Banken).
///
/// Eine Stelle fuer den FinTS-Abruf (FullWorth.FinTs), den MT940-Dateiimport (Backend) und die
/// Reparatur schon gespeicherter Buchungen - als verlinkte Datei wie <c>SecretBootstrap.cs</c>, weil
/// die drei Projekte einander nicht kennen. Vorher gab es zwei Leser: der FinTS-Abruf reichte das Feld
/// roh durch ("051?00Gutschrift?10006200?20SVWZ+..."), und der Dateiimport zerlegte es halb.
///
/// Der Aufbau: drei Ziffern Geschaeftsvorfallcode, dann Unterfelder <c>?NN</c>:
/// <list type="bullet">
/// <item><c>?00</c> Buchungstext ("Gutschrift", "Lastschrifteinzug", "VISA Debitkartenumsatz").</item>
/// <item><c>?10</c> Primanota - fuer niemanden lesbar, faellt weg.</item>
/// <item><c>?20</c>-<c>?29</c> und <c>?60</c>-<c>?63</c> Verwendungszweck in Stuecken zu 27 Zeichen. Sie
/// werden OHNE Trenner aneinandergehaengt: die Bank schneidet nach Zeichenzahl, nicht nach Wort.</item>
/// <item><c>?30</c> BIC/BLZ, <c>?31</c> IBAN/Konto der Gegenseite - die IBAN braucht die
/// Umbuchungserkennung, um zwei eigene Konten zu erkennen.</item>
/// <item><c>?32</c>/<c>?33</c> Name der Gegenseite, ebenfalls ohne Trenner zusammengesetzt.</item>
/// </list>
/// Das Feld selbst ist nach 65 Zeichen hart umbrochen. Der Umbruch ist kein Leerzeichen - mit einem
/// eingefuegten Leerzeichen wurde "Hirschner" zu "Hirschne r".
///
/// Im Verwendungszweck stehen oft SEPA-Kennungen (<c>EREF+</c>, <c>MREF+</c>, <c>CRED+</c>,
/// <c>SVWZ+</c>, ...). Der eigentliche Zweck ist der hinter <c>SVWZ+</c>; fehlen die Kennungen,
/// ist der ganze Text der Zweck.
/// </summary>
internal sealed record Mt940Information(
    string? PostingText,
    string? Purpose,
    string? CounterpartyName,
    string? CounterpartyAccount,
    string? CounterpartyBankCode,
    string? EndToEndReference,
    string? MandateReference,
    string? CreditorId)
{
    private static readonly Regex SepaTag = new(
        @"(?<tag>EREF|KREF|MREF|CRED|DEBT|SVWZ|ABWA|ABWE|IBAN|BIC|COAM|OAMT)\+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Zerlegt den Inhalt von <c>:86:</c> - mit oder ohne die harten Zeilenumbrueche.</summary>
    internal static Mt940Information Parse(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return new(null, null, null, null, null, null, null, null);
        var text = field.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

        // Ohne Unterfelder ist es freier Text - dann ist er der Zweck.
        var start = text.IndexOf('?');
        if (start < 0 || start > 3 || (start > 0 && !text[..start].All(char.IsAsciiDigit)))
            return FromPurpose(null, Clean(text), null, null, null);

        string? posting = null, bankCode = null, account = null;
        var purpose = new StringBuilder();
        var name = new StringBuilder();
        foreach (var part in text[(start + 1)..].Split('?'))
        {
            if (part.Length < 2 || !char.IsAsciiDigit(part[0]) || !char.IsAsciiDigit(part[1])) continue;
            var key = int.Parse(part[..2]);
            var body = part[2..];
            switch (key)
            {
                case 0: posting = Clean(body); break;
                case >= 20 and <= 29 or >= 60 and <= 63: purpose.Append(body); break;
                case 30: bankCode = Clean(body); break;
                case 31: account = Clean(body)?.Replace(" ", string.Empty); break;
                case 32 or 33: name.Append(body); break;
            }
        }
        return FromPurpose(posting, Clean(purpose.ToString()), Clean(name.ToString()), account, bankCode);
    }

    private static Mt940Information FromPurpose(string? posting, string? purpose, string? name, string? account, string? bankCode)
    {
        if (purpose is null) return new(posting, null, name, account, bankCode, null, null, null);
        var tags = SepaTag.Matches(purpose);
        if (tags.Count == 0) return new(posting, purpose, name, account, bankCode, null, null, null);

        // Was vor der ersten Kennung steht, ist freier Text; danach gehoert jedes Stueck seiner Kennung.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var free = Clean(purpose[..tags[0].Index]);
        for (var i = 0; i < tags.Count; i++)
        {
            var from = tags[i].Index + tags[i].Length;
            var to = i + 1 < tags.Count ? tags[i + 1].Index : purpose.Length;
            if (Clean(purpose[from..to]) is { } value) values.TryAdd(tags[i].Groups["tag"].Value, value);
        }
        var readable = values.GetValueOrDefault("SVWZ") ?? free;
        return new(posting, readable, name, account ?? values.GetValueOrDefault("IBAN"), bankCode,
            values.GetValueOrDefault("EREF") is { } eref && !eref.Equals("NOTPROVIDED", StringComparison.OrdinalIgnoreCase) ? eref : null,
            values.GetValueOrDefault("MREF"), values.GetValueOrDefault("CRED"));
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var collapsed = Regex.Replace(value.Trim(), @"\s{2,}", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
