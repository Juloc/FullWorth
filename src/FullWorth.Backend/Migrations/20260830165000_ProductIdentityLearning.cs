using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830165000_ProductIdentityLearning")]
public partial class ProductIdentityLearning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION fullworth_normalize_product_text(value text) RETURNS text AS $$
BEGIN
  RETURN regexp_replace(lower(trim(coalesce(value,''))), '[^[:alnum:]]', '', 'g');
END;
$$ LANGUAGE plpgsql IMMUTABLE;

CREATE OR REPLACE FUNCTION fullworth_prefill_purchase_item_product() RETURNS trigger AS $$
DECLARE
  space_id uuid;
  product_id uuid;
  default_category uuid;
BEGIN
  SELECT p."FullWorthSpaceId" INTO space_id FROM "Purchases" p WHERE p."Id"=NEW."PurchaseId";
  IF space_id IS NULL OR NEW."Name" IS NULL THEN RETURN NEW; END IF;

  SELECT a."ProductIdentityId", p."DefaultCategoryId"
    INTO product_id, default_category
  FROM "ProductIdentityAliases" a
  JOIN "ProductIdentities" p ON p."Id"=a."ProductIdentityId" AND p."FullWorthSpaceId"=space_id
  WHERE a."FullWorthSpaceId"=space_id
    AND a."NormalizedText"=fullworth_normalize_product_text(NEW."Name")
  LIMIT 1;

  IF product_id IS NOT NULL AND NEW."CategoryId" IS NULL AND default_category IS NOT NULL THEN
    NEW."CategoryId" := default_category;
    NEW."CategorizationSource" := 'product';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fullworth_link_purchase_item_product() RETURNS trigger AS $$
DECLARE
  space_id uuid;
  product_id uuid;
  confidence numeric(5,4);
  alias_source varchar(32);
BEGIN
  SELECT p."FullWorthSpaceId" INTO space_id FROM "Purchases" p WHERE p."Id"=NEW."PurchaseId";
  IF space_id IS NULL OR NEW."Name" IS NULL THEN RETURN NEW; END IF;

  SELECT a."ProductIdentityId", a."Confidence", a."Source"
    INTO product_id, confidence, alias_source
  FROM "ProductIdentityAliases" a
  JOIN "ProductIdentities" p ON p."Id"=a."ProductIdentityId" AND p."FullWorthSpaceId"=space_id
  WHERE a."FullWorthSpaceId"=space_id
    AND a."NormalizedText"=fullworth_normalize_product_text(NEW."Name")
  LIMIT 1;

  IF product_id IS NOT NULL THEN
    INSERT INTO "PurchaseItemProductLinks" ("PurchaseItemId","ProductIdentityId","Confidence","Source","UpdatedAt")
    VALUES (NEW."Id",product_id,confidence,coalesce(alias_source,'alias'),now())
    ON CONFLICT ("PurchaseItemId") DO NOTHING;
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_PurchaseItems_ProductPrefill" ON "PurchaseItems";
CREATE TRIGGER "TR_PurchaseItems_ProductPrefill"
BEFORE INSERT ON "PurchaseItems"
FOR EACH ROW EXECUTE FUNCTION fullworth_prefill_purchase_item_product();

DROP TRIGGER IF EXISTS "TR_PurchaseItems_ProductLink" ON "PurchaseItems";
CREATE TRIGGER "TR_PurchaseItems_ProductLink"
AFTER INSERT ON "PurchaseItems"
FOR EACH ROW EXECUTE FUNCTION fullworth_link_purchase_item_product();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_PurchaseItems_ProductLink" ON "PurchaseItems";
DROP TRIGGER IF EXISTS "TR_PurchaseItems_ProductPrefill" ON "PurchaseItems";
DROP FUNCTION IF EXISTS fullworth_link_purchase_item_product();
DROP FUNCTION IF EXISTS fullworth_prefill_purchase_item_product();
DROP FUNCTION IF EXISTS fullworth_normalize_product_text(text);
""");
    }
}
