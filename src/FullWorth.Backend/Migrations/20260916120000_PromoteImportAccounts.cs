using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Stuft die Importkonten hoch, die als stille Behaelter angelegt wurden: archiviert, ausserhalb des
/// Vermoegens, ohne Kontogruppe. Neue Importe entstehen seit dem Umbau als vollwertige Konten - ohne
/// diese Migration blieben nur die Bestandskonten zurueck, und genau sie tragen die Historie, deretwegen
/// jemand ueberhaupt importiert hat.
///
/// Sie bewegt kein Geld. Jede Summe im System kommt aus "BalanceSnapshots"; ein Konto ohne Kontostand
/// traegt exakt 0 bei, egal welche Flaggen gesetzt sind. Die Vermoegenskurve kann hier also nicht
/// springen - das kann erst das spaetere Nachtragen eines Kontostands, und das ist eine Entscheidung
/// des Nutzers.
///
/// "UseForBalanceHistory" wird bewusst NICHT angefasst: ohne Anker wuerde die Rueckrechnung sonst
/// Vergangenheit erfinden. Das setzt der Kontostand, wenn er kommt (AccountStore.SetManualBalanceAsync).
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260916120000_PromoteImportAccounts")]
public sealed class PromoteImportAccounts : Migration
{
    /// <summary>
    /// Als Konstante, damit ein Test genau dieses SQL ausfuehren kann statt einer Abschrift davon -
    /// eine Abschrift haette den Fehler, den der Test finden soll, nur mitkopiert.
    /// </summary>
    internal const string PromoteSql = """
-- 1) Erst die Doppel: gibt es zu einem Importkonto bereits ein aktives, mitzaehlendes Konto mit
--    derselben IBAN, dann ist es dasselbe Bankkonto auf zwei Wegen. Es hochzustufen hiesse, dieses
--    Geld ab sofort doppelt zu zaehlen - die Migration waere selbst die Fehlerquelle. Es wird
--    stattdessen als Doppel markiert, nachvollziehbar und umkehrbar.
UPDATE "Accounts" a
SET "DuplicateOfAccountId" = live."Id",
    "IncludeInNetWorthBeforeLink" = COALESCE(a."IncludeInNetWorthBeforeLink", a."IncludeInNetWorth"),
    "IncludeInNetWorth" = FALSE,
    "UpdatedAt" = now()
FROM "Accounts" live
WHERE a."Provider" = 'finanzguru-import'
  AND a."DuplicateOfAccountId" IS NULL
  AND a."ImportLinkedAccountId" IS NULL
  AND a."IbanLookup" IS NOT NULL
  AND live."Id" <> a."Id"
  AND live."FullWorthSpaceId" = a."FullWorthSpaceId"
  AND live."IbanLookup" = a."IbanLookup"
  AND live."IsActive"
  AND live."IncludeInNetWorth"
  AND live."Provider" <> 'finanzguru-import';

-- 2) Die uebrigen werden richtige Konten. Ausgenommen bleibt, was schon einem echten Konto zugeordnet
--    ist ("ImportLinkedAccountId") - so eines ist ausgeraeumt und soll stillgelegt bleiben - und was
--    gar keine Buchungen traegt, denn ein leeres Konto in der Liste waere eine Zeile ueber nichts.
UPDATE "Accounts" a
SET "IsActive" = TRUE,
    "IncludeInNetWorth" = TRUE,
    "UpdatedAt" = now()
WHERE a."Provider" = 'finanzguru-import'
  AND NOT a."IsActive"
  AND a."ImportLinkedAccountId" IS NULL
  AND a."DuplicateOfAccountId" IS NULL
  AND EXISTS (SELECT 1 FROM "Transactions" t WHERE t."AccountId" = a."Id");

-- 3) Die fehlende Kontogruppe nachtragen. Der Import baute sein Konto selbst und setzte sie nie -
--    seit #125 gibt es ein Konto ohne Gruppe nicht mehr, diese hingen also seither daneben.
UPDATE "Accounts" a
SET "GroupId" = g."Id",
    "UpdatedAt" = now()
FROM "AccountGroups" g
WHERE a."Provider" = 'finanzguru-import'
  AND a."GroupId" IS NULL
  AND g."FullWorthSpaceId" = a."FullWorthSpaceId"
  AND g."IsDefault";
""";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(PromoteSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Die Hochstufung laesst sich nicht zielgenau zuruecknehmen: welches Konto vorher archiviert
        // war, steht nach dem UPDATE nirgends mehr, und ein pauschales Zurueckstufen wuerde auch die
        // Konten treffen, die der Nutzer seither selbst sichtbar gemacht hat. Ein Down, das mehr
        // kaputt macht als es herstellt, waere schlechter als keines.
        //
        // Die Doppel-Markierung dagegen ist umkehrbar, denn sie hat sich ihren Vorzustand gemerkt.
        migrationBuilder.Sql("""
UPDATE "Accounts"
SET "IncludeInNetWorth" = COALESCE("IncludeInNetWorthBeforeLink", "IncludeInNetWorth"),
    "IncludeInNetWorthBeforeLink" = NULL,
    "DuplicateOfAccountId" = NULL,
    "UpdatedAt" = now()
WHERE "Provider" = 'finanzguru-import'
  AND "DuplicateOfAccountId" IS NOT NULL;
""");
    }
}
