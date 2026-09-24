using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Aussehen und Gesehen-Stand der Konten ziehen aus den Benutzereinstellungen in ihre eigenen
/// Tabellen um (#177).
///
/// Es gab dafuer zwei Systeme. Das eine, <c>/api/account-experience/*</c>, war fertig gebaut und
/// hatte keinen Aufrufer. Das andere lief: die Kontenseite legte Symbol und Farben je Konto und je
/// Gruppe in drei Einstellungs-Blobs ab (<c>accounts.visuals</c>, <c>account-groups.visuals</c>,
/// <c>transactions.seenAt</c>) - pro Benutzer, pro Bereich, als JSON.
///
/// Der Umzug geht in die Richtung der Tabellen, aus drei Gruenden, die alle gemessen und nicht
/// gemeint sind:
///
/// <list type="number">
///   <item>Der Gesehen-Stand stand im Blob als Liste von bis zu 500 Buchungs-Ids. Die Seite holte
///         dafuer bei JEDEM Laden 500 Buchungen, nur um zu wissen, ob irgendwo ein Punkt
///         hingehoert. Der Server zaehlt dasselbe je Konto in einer Abfrage.</item>
///   <item>Das Umsortieren schrieb eine Anfrage je Gruppe und je Konto. Bricht die Hälfte ab, steht
///         eine halbe Reihenfolge. <c>POST /api/account-experience/reorder</c> ist eine
///         Transaktion.</item>
///   <item>Das Aussehen gehoert dem Bereich, nicht dem Benutzer: wer mit jemandem teilt, sah bisher
///         andere Symbole als der andere, ohne dass das je jemand entschieden haette.</item>
/// </list>
///
/// Zwei Dinge macht diese Migration deshalb:
///
/// <list type="number">
///   <item>Den Gruppen fehlte die Hintergrundfarbe - der Blob hatte drei Werte, die Tabelle zwei.
///         Ohne diese Spalte waere der Umzug ein Datenverlust, und das ist kein Umzug.</item>
///   <item>Sie traegt die vorhandenen Blobs hinueber. Gibt es zu einem Konto mehrere (ein Blob je
///         Benutzer), gewinnt der zuletzt geschriebene - eine Tabelle je Bereich kann nur einen
///         halten, und die juengste Entscheidung ist die naechstliegende.</item>
/// </list>
///
/// Der Gesehen-Stand des Blobs ist ein einziger Zeitpunkt fuer alles; er wird auf jedes Konto des
/// Bereichs geschrieben, das der Benutzer sehen darf. Ohne das waere am Tag nach dem Update jede
/// Buchung wieder ungelesen - technisch richtig und fuer den Benutzer eine Falschmeldung.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260924100000_AccountExperienceTakesOverTheVisuals")]
public sealed class AccountExperienceTakesOverTheVisuals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql);

    /// <summary>
    /// Der Umzug als eine Zeichenkette, damit ein Test genau DIESE Anweisungen ausfuehren kann und
    /// nicht eine nachgebaute Fassung davon. Eine Daten-Migration laesst sich sonst nicht pruefen: die
    /// Testdatenbank entsteht mit allen Migrationen und ohne Daten, der interessante Fall - alte Blobs
    /// sind da - tritt dort nie auf.
    ///
    /// Alles darin ist wiederholbar (ON CONFLICT DO NOTHING, IF NOT EXISTS), ein zweiter Lauf aendert
    /// also nichts.
    /// </summary>
    internal const string Sql = """
        ALTER TABLE "AccountGroupAppearances" ADD COLUMN IF NOT EXISTS "BackgroundColor" varchar(9) NULL;

        -- Konten. Der Blob ist {"<accountId>":{"icon":..,"color":..,"background":..}}; DISTINCT ON
        -- nimmt je Konto den zuletzt geschriebenen Eintrag.
        INSERT INTO "AccountAppearances" ("AccountId","Icon","IconColor","BackgroundColor","UpdatedAt")
        SELECT DISTINCT ON (a."Id")
               a."Id",
               NULLIF(v.value->>'icon',''),
               UPPER(NULLIF(v.value->>'color','')),
               UPPER(NULLIF(v.value->>'background','')),
               p."UpdatedAt"
        FROM "UserPreferences" p
        CROSS JOIN LATERAL jsonb_each(p."ValueJson"::jsonb) AS v(key, value)
        JOIN "Accounts" a ON a."Id"::text = v.key AND a."FullWorthSpaceId" = p."FullWorthSpaceId"
        WHERE p."Key" = 'accounts.visuals'
          AND jsonb_typeof(p."ValueJson"::jsonb) = 'object'
          AND jsonb_typeof(v.value) = 'object'
        ORDER BY a."Id", p."UpdatedAt" DESC
        ON CONFLICT ("AccountId") DO NOTHING;

        -- Gruppen. Dieselbe Form, andere Spaltennamen.
        INSERT INTO "AccountGroupAppearances" ("GroupId","Icon","Color","BackgroundColor","UpdatedAt")
        SELECT DISTINCT ON (g."Id")
               g."Id",
               NULLIF(v.value->>'icon',''),
               UPPER(NULLIF(v.value->>'color','')),
               UPPER(NULLIF(v.value->>'background','')),
               p."UpdatedAt"
        FROM "UserPreferences" p
        CROSS JOIN LATERAL jsonb_each(p."ValueJson"::jsonb) AS v(key, value)
        JOIN "AccountGroups" g ON g."Id"::text = v.key AND g."FullWorthSpaceId" = p."FullWorthSpaceId"
        WHERE p."Key" = 'account-groups.visuals'
          AND jsonb_typeof(p."ValueJson"::jsonb) = 'object'
          AND jsonb_typeof(v.value) = 'object'
        ORDER BY g."Id", p."UpdatedAt" DESC
        ON CONFLICT ("GroupId") DO NOTHING;

        -- Gesehen-Stand: ein Zeitpunkt im Blob, ein Eintrag je Konto des Benutzers. "seenAt" ist ein
        -- ISO-Zeitpunkt; ist er unlesbar, bleibt das Konto ungesehen statt einen falschen Stichtag
        -- zu bekommen.
        INSERT INTO "AccountTransactionSeenStates" ("UserId","AccountId","LastSeenAt")
        SELECT p."FinanceUserId", a."Id", (p."ValueJson"::jsonb->>'seenAt')::timestamptz
        FROM "UserPreferences" p
        JOIN "Accounts" a ON a."FullWorthSpaceId" = p."FullWorthSpaceId"
        WHERE p."Key" = 'transactions.seenAt'
          AND jsonb_typeof(p."ValueJson"::jsonb) = 'object'
          AND (p."ValueJson"::jsonb->>'seenAt') IS NOT NULL
          AND (p."ValueJson"::jsonb->>'seenAt') ~ '^\d{4}-\d{2}-\d{2}T'
        ON CONFLICT ("UserId","AccountId") DO NOTHING;

        -- Die Blobs selbst. Sie stehen jetzt an zwei Stellen, und die zweite gewinnt nie wieder -
        -- eine Kopie, die niemand mehr liest, ist genau die Art Altbestand, die spaeter als Wahrheit
        -- missverstanden wird.
        DELETE FROM "UserPreferences"
        WHERE "Key" IN ('accounts.visuals', 'account-groups.visuals', 'transactions.seenAt');
        """;

    /// <summary>
    /// Zurueck geht die Spalte, nicht die Bewegung. Die Blobs aus den Tabellen wieder
    /// zusammenzusetzen hiesse raten, welcher Benutzer welchen geschrieben hat - diese Zuordnung ist
    /// beim Hinweg bewusst aufgegeben worden, weil das Aussehen dem Bereich gehoert.
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "AccountGroupAppearances" DROP COLUMN IF EXISTS "BackgroundColor";
        """);
}
