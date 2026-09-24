using System.Text.RegularExpressions;
using FullWorth.Backend.Modules.Transactions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Die Sammelbearbeitung der Buchungen und der Endpunkt, den sie ruft (#177).
///
/// Der Endpunkt war fertig gebaut und hatte keinen Aufrufer — 452 Zeilen, die niemand erreichen
/// konnte. Die Oberfläche dazu hängt jetzt an der Auswahl, die es auf der Buchungsseite ohnehin gab.
///
/// Geprüft wird hier das, was kein Compiler prüft: dass die Felder, die das Frontend schickt, den
/// Feldern entsprechen, die der Endpunkt entgegennimmt. Zwischen beiden liegt JSON — ein Tippfehler
/// im Namen wird stillschweigend zu „nicht gesetzt", und die Massenänderung ändert dann weniger als
/// angezeigt, ohne dass irgendwo ein Fehler auftaucht. Genau diese Art Bruch fängt in diesem Haus
/// sonst nichts, weil es über die Repository-Grenze geht.
/// </summary>
public sealed class TransactionBulkEditUiTests
{
    private static string Modul() => Frontend("pages", "transactions", "bulk-edit.js");

    private static string Frontend(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { Root(), "src", "FullWorth.Web", "wwwroot" }.Concat(parts).ToArray()));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_field_the_dialog_sends_exists_on_the_endpoint_record()
    {
        var quelle = Modul();
        var rumpf = Between(quelle, "return {", "};");

        var gesendet = Regex.Matches(rumpf, @"^\s{4}(\w+):", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(gesendet);

        var bekannt = typeof(AdvancedTransactionBulkRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var feld in gesendet)
            Assert.True(bekannt.Contains(feld),
                $"bulk-edit.js schickt \"{feld}\" - AdvancedTransactionBulkRequest kennt das nicht. "
                + "JSON schluckt den Tippfehler, und die Änderung fällt still kleiner aus als angekündigt.");
    }

    /// <summary>
    /// Die Sicherung des Endpunkts muss die Oberfläche auch wirklich bedienen: er lehnt ab, wenn die
    /// Zahl der Treffer nicht die ist, die der Aufrufer erwartet hat (409), und verlangt eine
    /// ausdrückliche Bestätigung. Schickte das Frontend hier eine Konstante oder ließe es die
    /// Bestätigung weg, wäre die Sicherung Zierde.
    /// </summary>
    [Fact]
    public void The_dialog_sends_the_real_count_and_both_confirmations()
    {
        var quelle = Modul();

        Assert.Contains("expectedCount: ids.length", quelle, StringComparison.Ordinal);
        Assert.Contains("confirmSelection: true", quelle, StringComparison.Ordinal);
        // Die Notiz ist nicht umkehrbar: ohne gesetztes Kästchen wird sie nicht ersetzt, und die
        // zweite Bestätigung ist an dasselbe Kästchen gebunden statt fest auf true zu stehen.
        Assert.Contains("confirmReplaceNotes: replaceNotes", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmReplaceNotes: true", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// 409 heißt „die Auswahl hat sich geändert", nicht „das hat nicht funktioniert". Der Unterschied
    /// entscheidet, was der Benutzer als Nächstes tut: nachsehen statt noch einmal klicken.
    /// </summary>
    [Fact]
    public void A_changed_selection_is_reported_as_such_and_not_as_a_generic_failure()
    {
        var quelle = Modul();

        Assert.Contains("409", quelle, StringComparison.Ordinal);
        Assert.Contains("Die Auswahl hat sich inzwischen geändert", quelle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_button_sits_on_the_selection_that_already_existed()
    {
        var seite = Frontend("pages", "transactions", "page.js");

        Assert.Contains("data-selection-edit", seite, StringComparison.Ordinal);
        Assert.Contains("openBulkEdit(ctx, coachSelection.getSelectedIds()", seite, StringComparison.Ordinal);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Marke fehlt: {start}");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Marke fehlt: {end}");
        return text[(from + start.Length)..to];
    }
}
