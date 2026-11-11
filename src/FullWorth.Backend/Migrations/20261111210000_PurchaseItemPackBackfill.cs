using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Holt die Packungsgroesse aus bestehenden Positionen in ihre eigenen Felder. Die Auslese hat sie bisher in die
/// Mengenangabe ("500 g", "2 × 100 g") oder in den Namen ("Knoblauch 200 g", "Eier, 10er") geschrieben, und
/// <c>PackageQuantity</c> blieb in jeder Zeile leer - ein Vergleich ueber die Packung war deshalb nie moeglich.
///
/// Dieselben Regeln wie <c>PackSize</c> im Code, hier fuer den Bestand: nur eindeutige Faelle (Angabe am Ende des
/// Namens oder die ganze Mengenangabe), nur Zeilen ohne Packung, und der Name bleibt in <c>RawName</c> erhalten.
/// Wiederholbar: eine Zeile mit Packung wird nicht noch einmal angefasst.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20261111210000_PurchaseItemPackBackfill")]
public partial class PurchaseItemPackBackfill : Migration
{
    public const string BackfillSql = """
        -- 1. Die Mengenangabe ist nur die Packung: "500 g", "0,5 l", "1 kg".
        WITH m AS (
          SELECT "Id", regexp_match(lower(btrim("QuantityUnit")), '^(\d{1,4}(?:[.,]\d{1,3})?)\s*(kg|g|ml|cl|l)$') AS r
          FROM "PurchaseItems"
          WHERE "PackageQuantity" IS NULL AND lower(btrim("QuantityUnit")) ~ '^\d{1,4}([.,]\d{1,3})?\s*(kg|g|ml|cl|l)$')
        UPDATE "PurchaseItems" i SET
          "PackageQuantity" = replace(m.r[1], ',', '.')::numeric * CASE WHEN m.r[2] = 'cl' THEN 10 ELSE 1 END,
          "PackageUnit" = CASE WHEN m.r[2] = 'cl' THEN 'ml' ELSE m.r[2] END,
          "QuantityUnit" = 'piece'
        FROM m WHERE i."Id" = m."Id";

        -- 2. ... oder mehrere davon: "2 × 100 g", "5x55g".
        WITH m AS (
          SELECT "Id", regexp_match(lower(btrim("QuantityUnit")), '^(\d{1,3})\s*[x×]\s*(\d{1,4}(?:[.,]\d{1,3})?)\s*(kg|g|ml|cl|l)$') AS r
          FROM "PurchaseItems"
          WHERE "PackageQuantity" IS NULL AND lower(btrim("QuantityUnit")) ~ '^\d{1,3}\s*[x×]\s*\d{1,4}([.,]\d{1,3})?\s*(kg|g|ml|cl|l)$')
        UPDATE "PurchaseItems" i SET
          "PackageCount" = CASE WHEN m.r[1]::int > 1 THEN m.r[1]::int END,
          "PackageQuantity" = replace(m.r[2], ',', '.')::numeric * CASE WHEN m.r[3] = 'cl' THEN 10 ELSE 1 END,
          "PackageUnit" = CASE WHEN m.r[3] = 'cl' THEN 'ml' ELSE m.r[3] END,
          "QuantityUnit" = 'piece'
        FROM m WHERE i."Id" = m."Id";

        -- 3. Die Packung steht am Ende des Namens: "Knoblauch 200 g". Der Name verliert sie, RawName behaelt sie.
        WITH m AS (
          SELECT "Id", regexp_match(btrim("Name"), '^(.{2,}?)[\s,]+(\d{1,4}(?:[.,]\d{1,3})?)\s*(kg|g|ml|cl|l)\.?$', 'i') AS r
          FROM "PurchaseItems"
          WHERE "PackageQuantity" IS NULL AND btrim("Name") ~* '^.{2,}?[\s,]+\d{1,4}([.,]\d{1,3})?\s*(kg|g|ml|cl|l)\.?$')
        UPDATE "PurchaseItems" i SET
          "RawName" = CASE WHEN btrim(i."RawName") = '' THEN i."Name" ELSE i."RawName" END,
          "Name" = btrim(m.r[1], ' ,'),
          "PackageQuantity" = replace(m.r[2], ',', '.')::numeric * CASE WHEN lower(m.r[3]) = 'cl' THEN 10 ELSE 1 END,
          "PackageUnit" = CASE WHEN lower(m.r[3]) = 'cl' THEN 'ml' ELSE lower(m.r[3]) END
        FROM m WHERE i."Id" = m."Id" AND length(btrim(m.r[1], ' ,')) >= 2;

        -- 4. "Eier, 10er", "Eier 6 Stück": Stueckzahl am Ende des Namens.
        WITH m AS (
          SELECT "Id", regexp_match(btrim("Name"), '^(.{2,}?)[\s,]+(\d{1,3})\s*(?:er|stk\.?|st\.?|stück|stueck)$', 'i') AS r
          FROM "PurchaseItems"
          WHERE "PackageQuantity" IS NULL AND btrim("Name") ~* '^.{2,}?[\s,]+\d{1,3}\s*(er|stk\.?|st\.?|stück|stueck)$')
        UPDATE "PurchaseItems" i SET
          "RawName" = CASE WHEN btrim(i."RawName") = '' THEN i."Name" ELSE i."RawName" END,
          "Name" = btrim(m.r[1], ' ,'),
          "PackageQuantity" = m.r[2]::numeric,
          "PackageUnit" = 'piece'
        FROM m WHERE i."Id" = m."Id" AND length(btrim(m.r[1], ' ,')) >= 2;

        -- 5. Der Preis je kg/l/Stueck der Zeilen, die jetzt eine Packung haben (wie PurchaseArticleCalculator.BaseUnitPrice).
        UPDATE "PurchaseItems" SET "BaseUnitPrice" = round(
            COALESCE("UnitPrice", "TotalPrice" / NULLIF("Quantity", 0))
            / ("PackageQuantity" * COALESCE("PackageCount", 1)
               / CASE "PackageUnit" WHEN 'g' THEN 1000 WHEN 'ml' THEN 1000 ELSE 1 END), 2)
        WHERE "BaseUnitPrice" IS NULL AND "PackageQuantity" > 0 AND "PackageUnit" IN ('kg', 'g', 'l', 'ml', 'piece')
          AND COALESCE("UnitPrice", "TotalPrice" / NULLIF("Quantity", 0)) IS NOT NULL;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(BackfillSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Gelesene Werte lassen sich nicht zurueckschreiben; der urspruengliche Name steht in RawName.
    }
}
