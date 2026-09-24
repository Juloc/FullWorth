using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Die gemerkten Auswertungen ziehen aus den Benutzereinstellungen in ihre eigene Tabelle (#177).
///
/// Dieselbe Bauart wie bei der Konten-Darstellung, und dieselbe Ursache: <c>SavedAnalyses</c> stand
/// samt vollem CRUD unter <c>/api/saved-analyses</c> im Baum und hatte keinen Aufrufer, waehrend die
/// Auswertungsseite ihre Merkzettel in den Einstellungs-Blob <c>analytics.savedAnalyses</c> legte.
///
/// Der Umzug geht in Richtung der Tabelle, weil der Blob drei Dinge nicht kann:
///
/// <list type="number">
///   <item>Ein Blob ist ein Feld. Loeschen hiess: die ganze Liste lesen, eine Zeile herausfiltern und
///         alles zurueckschreiben - wer das gleichzeitig in zwei Fenstern tut, verliert einen
///         Eintrag, ohne dass irgendetwas fehlschlaegt.</item>
///   <item>Die Ids waren frei erfunden (<c>crypto.randomUUID</c> oder ein Zeitstempel in Base36). Die
///         Tabelle vergibt echte.</item>
///   <item>Kein Protokoll. Die Tabelle schreibt beim Anlegen, Aendern und Loeschen einen
///         Audit-Eintrag.</item>
/// </list>
///
/// Der Blob hielt je Eintrag <c>{id, name, config:{measure, dimension, period, chartType}}</c>. Die
/// Tabelle haelt eine vollstaendige Abfrage plus Darstellung; der Zeitraum wandert dabei als
/// <c>period</c> mit, damit eine gemerkte Auswertung weiterhin "letzte 12 Monate" bedeutet und nicht
/// dieselben zwoelf Monate von damals. <c>from</c> und <c>to</c> bleiben leer: genau das laesst die
/// Oberflaeche den Zeitraum beim Oeffnen neu ausrechnen.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260924120000_SavedAnalysesTakeOverTheBlob")]
public sealed class SavedAnalysesTakeOverTheBlob : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql);

    /// <summary>
    /// Wie bei <see cref="AccountExperienceTakesOverTheVisuals"/> als Konstante, damit ein Test genau
    /// diese Anweisungen ausfuehren kann: die Testdatenbank entsteht mit allen Migrationen und ohne
    /// Daten, der interessante Fall - ein alter Blob ist da - tritt dort nie auf.
    /// </summary>
    internal const string Sql = """
        INSERT INTO "SavedAnalyses" ("Id","FullWorthSpaceId","OwnerUserId","Name","SchemaVersion","ConfigJson","CreatedAt","UpdatedAt")
        SELECT gen_random_uuid(),
               p."FullWorthSpaceId",
               p."FinanceUserId",
               LEFT(item->>'name', 160),
               1,
               jsonb_build_object(
                 'query', jsonb_build_object(
                   'measure',   COALESCE(item->'config'->>'measure', 'spend'),
                   'dimension', COALESCE(item->'config'->>'dimension', 'month'),
                   'from',      NULL,
                   'to',        NULL),
                 'chartType', COALESCE(item->'config'->>'chartType', 'bar'),
                 'period',    item->'config'->>'period'),
               p."UpdatedAt",
               p."UpdatedAt"
        FROM "UserPreferences" p
        CROSS JOIN LATERAL jsonb_array_elements(p."ValueJson"::jsonb->'items') AS item
        JOIN "Users" u ON u."Id" = p."FinanceUserId"
        WHERE p."Key" = 'analytics.savedAnalyses'
          AND jsonb_typeof(p."ValueJson"::jsonb) = 'object'
          AND jsonb_typeof(p."ValueJson"::jsonb->'items') = 'array'
          AND NULLIF(TRIM(COALESCE(item->>'name', '')), '') IS NOT NULL;

        DELETE FROM "UserPreferences" WHERE "Key" = 'analytics.savedAnalyses';
        """;

    /// <summary>
    /// Zurueck geht nichts. Der Blob liesse sich aus der Tabelle zwar wieder zusammensetzen, aber die
    /// erfundenen Ids von damals sind fort - und eine Ruecksicherung, die andere Ids erzeugt als die,
    /// die sie ersetzen soll, ist keine.
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
