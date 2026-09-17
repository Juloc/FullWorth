using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Architecture;

/// <summary>
/// Waechter fuer die Knopf-Rollen-Migration (Issue #157, Scheibe "components/buttons.js"): <c>.ghost</c>,
/// <c>.primary-action</c> und die BLOSSE Klasse <c>danger</c> (nicht <c>btn-danger</c>) wurden aus dem CSS
/// entfernt, weil jeder verbliebene Knopf jetzt <c>buttonClass(ButtonRole.X)</c> aus
/// <c>components/buttons.js</c> traegt. Eine dieser drei Klassen in neuem Code heisst: irgendwo wurde von
/// Hand eine Knopf-Optik zusammengebaut statt die gemeinsame Rolle zu benutzen - und diese drei Klassen
/// haben inzwischen KEIN CSS mehr, das sie stylt, also faellt der Knopf auf das naechste, zufaellig
/// passende Selektorpaar zurueck (siehe die Korrektur in pages/settings/page.js: drei
/// <c>class="ghost" data-close</c>-Knoepfe fingen sich im generischen
/// <c>.dialog-card [data-close]:not(.icon-button)</c>-Rund-Icon-Stil, obwohl sie Text trugen).
///
/// Die zweite Haelfte prueft unklassierte <c>&lt;button&gt;</c>-Tags: ein Knopf ganz ohne <c>class=</c>
/// ist nur dann in Ordnung, wenn ein anderes Merkmal (<c>data-*</c>, <c>value=</c>, <c>role=</c>,
/// <c>aria-*</c>) zeigt, dass es sich um eine etablierte Ausnahme handelt - einen Tab, einen Chip, eine
/// Kopfzeilen-X-Schaltflaeche mit <c>data-close</c> und aehnliche nicht-Rollen-Steuerelemente, von denen
/// es in dieser App bereits gut hundert gibt (Registerkarten, Auswahl-Chips, das Kopfzeilen-X). Ein Knopf
/// ganz ohne jedes Merkmal ist der Fall, den dieser Waechter tatsaechlich fangen soll: jemand hat
/// vergessen, eine Rolle zu vergeben.
/// </summary>
public sealed class ButtonRoleGuardTests
{
    private static string ListenPfad => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Architecture", "button-role-violations.txt");

    private static readonly string[] BareRoleClasses = ["ghost", "danger", "primary-action"];

    private static readonly Regex ClassAttr = new(
        @"class\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled);

    private static readonly Regex ButtonTag = new(@"<button\b[^>]*>", RegexOptions.Compiled);

    private static readonly Regex HasClassAttr = new(@"\bclass\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Jedes dieser Merkmale markiert einen Knopf als etwas anderes als eine unrollierte generische
    // Aktion: ein Registerkarten-/Auswahl-/Mengen-Steuerelement (data-*), ein Formularwert
    // (value=), eine ARIA-Rolle oder ein ARIA-Attribut (role=/aria-*). Alle bereits im Baum
    // vorhandenen unklassierten Knoepfe tragen mindestens eines davon - siehe den Bootstrap-Check
    // unten, der genau das behauptet.
    //
    // `${` zaehlt ebenfalls: app.js baut das Mehr-Menue mit
    // `<button type="button" ${target}${active}>` - target ist IMMER data-open="..."/data-go="...",
    // active manchmal class="active", aber beides steckt in Variablen, die eine einfache Regex nicht
    // aufloesen kann. Ein Attribut, das sich der statischen Pruefung entzieht, ist kein Nachweis eines
    // vergessenen Knopf-Stils - eher das Gegenteil, in dieser Codebasis wird eine echte generische
    // Aktion immer mit einem woertlichen class="..." geschrieben.
    private static readonly Regex EstablishedMarker = new(
        @"\bdata-[a-z0-9-]+\b|\bvalue\s*=|\brole\s*=|\baria-[a-z0-9-]+\b|\$\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void No_further_file_hand_rolls_a_button_that_the_shared_roles_already_cover()
    {
        var aktuell = Verstoesse();
        var pfad = Path.GetFullPath(ListenPfad);

        if (!File.Exists(pfad))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.WriteAllLines(pfad, aktuell);
            Assert.Fail($"Die Liste wurde neu geschrieben ({aktuell.Length} Zeilen): {pfad}");
        }

        var bekannt = File.ReadAllLines(pfad).Where(zeile => zeile.Trim().Length > 0).ToArray();
        var neu = aktuell.Except(bekannt, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(neu.Length == 0,
            "Diese Dateien rollen einen Knopf von Hand statt buttonClass(ButtonRole.X) aus "
            + "components/buttons.js zu verwenden:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", neu));
    }

    /// <summary>Ratsche: ein migrierter Knopf verschwindet auch aus der Liste.</summary>
    [Fact]
    public void The_list_claims_no_file_that_is_clean_again()
    {
        var pfad = Path.GetFullPath(ListenPfad);
        if (!File.Exists(pfad)) return;

        var bekannt = File.ReadAllLines(pfad).Where(zeile => zeile.Trim().Length > 0);
        var sauber = bekannt.Except(Verstoesse(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(sauber.Length == 0,
            "Diese Dateien sind migriert - bitte aus tests/.../Architecture/button-role-violations.txt "
            + "streichen:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", sauber));
    }

    private static string[] Verstoesse()
    {
        var wwwroot = WwwRoot();
        var offenders = new List<string>();

        foreach (var datei in Directory.EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(wwwroot, datei).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == "components/buttons.js") continue;
            if (relative.Contains("/lib/", StringComparison.Ordinal)) continue;

            var source = File.ReadAllText(datei);
            if (HatBareRolleKlasse(source) || HatUnklassiertenAktionsknopf(source))
                offenders.Add(relative);
        }

        return offenders.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HatBareRolleKlasse(string source)
    {
        foreach (Match match in ClassAttr.Matches(source))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(token => BareRoleClasses.Contains(token, StringComparer.Ordinal))) return true;
        }
        return false;
    }

    private static bool HatUnklassiertenAktionsknopf(string source)
    {
        foreach (Match match in ButtonTag.Matches(source))
        {
            var tag = match.Value;
            if (HasClassAttr.IsMatch(tag)) continue;
            if (EstablishedMarker.IsMatch(tag)) continue;
            return true;
        }
        return false;
    }

    private static string WwwRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot");
    }
}
