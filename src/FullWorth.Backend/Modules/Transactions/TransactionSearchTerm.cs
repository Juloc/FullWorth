using System.Text;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>
/// Was der Benutzer tippt, als <c>tsquery</c> (#161).
///
/// <c>to_tsquery</c> hat eine eigene Syntax: <c>&amp;</c>, <c>|</c>, <c>!</c>, Klammern, <c>:*</c>.
/// Eine Eingabe roh durchzureichen hiesse, dass ein Doppelpunkt in einem Verwendungszweck plötzlich
/// Syntax ist - und eine ungueltige Abfrage wirft, statt nichts zu finden. Die Suche wuerde also bei
/// bestimmten Zeichen einen Fehler zeigen, nicht ein leeres Ergebnis.
///
/// Deshalb wird jedes Wort einzeln zitiert und mit <c>:*</c> versehen, und die Woerter werden mit
/// UND verbunden: "rewe markt" findet, was beide Wortanfaenge enthaelt - dieselbe Erwartung, die
/// jeder von einer Suchzeile hat.
/// </summary>
public static class TransactionSearchTerm
{
    /// <summary>So viele Woerter wertet niemand aus, und mehr macht die Abfrage nur teurer.</summary>
    private const int MaxTerms = 8;

    /// <summary>Null, wenn nichts Durchsuchbares uebrig bleibt - dann gibt es keine Textbedingung.</summary>
    public static string? ToPrefixQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var builder = new StringBuilder();
        var count = 0;
        foreach (var word in input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Nur Buchstaben und Ziffern. Alles andere ist entweder Syntax von to_tsquery oder
            // Zeichen, die 'simple' ohnehin als Wortgrenze behandelt.
            var cleaned = new string(word.Where(char.IsLetterOrDigit).ToArray());
            if (cleaned.Length == 0) continue;

            if (count > 0) builder.Append(" & ");
            builder.Append('\'').Append(cleaned).Append("':*");
            if (++count == MaxTerms) break;
        }

        return count == 0 ? null : builder.ToString();
    }
}
