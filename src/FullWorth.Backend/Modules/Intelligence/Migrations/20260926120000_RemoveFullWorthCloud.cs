using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Raeumt ab, was von der FullWorth Cloud uebrig ist.
///
/// Fuenfzehn Tabellen: der Verbindungszustand mit Zustimmung und Zugangsdaten, die Ausgangs-
/// warteschlange, die Paketmaschinerie samt angeheftetem Signaturschluessel, und die drei
/// Referenzstufen, die nur ein Paket fuellen konnte - Ontologie, Vertragsanbieter, Produkte.
///
/// Drei Tabellen bleiben und wechseln nur den Schreiber: OfficialBrandAssets und
/// OfficialBrandAliases fuellt jetzt der mitgelieferte Katalog, OfficialMerchantMappings wird zur
/// instanzeigenen Haendlerzuordnung. Ihre Zeilen stammen bisher aus einem Paket, das es nicht mehr
/// gibt - sie werden geleert, und PackId/PackVersion fallen weg, weil sie auf nichts mehr zeigen.
///
/// Zwei Dinge, die man hier vergisst und die nichts meldet:
///
/// 1. BrandAssetBlobs ist inhaltsadressiert und hat keine Besitzerspalte. Loescht man eine
///    Asset-Zeile, bleiben die Bytes liegen und niemand merkt es. Deshalb das NOT EXISTS ueber
///    ALLE drei verbleibenden Quellen - ein Hash, den sich ein Paketlogo mit einem selbst
///    recherchierten teilt, muss ueberleben.
/// 2. Buchungen mit CategorizationSource = 'cloud' bleiben unangetastet. Der Wert steht weiter in
///    der Menge der neu berechenbaren Quellen in TransactionRuleEngine; wuerde man ihn hier
///    umschreiben, kaemen alle diese Buchungen als "noch nicht geprueft" zurueck in die
///    Warteschlange, oder sie froeren mit einer Begruendung ein, die es nicht mehr gibt.
///
/// Diese Migration ist einwegig unter dem dokumentierten Rollback: der ist ein Abbild-Pin und
/// fuehrt kein Down() aus. Deshalb steht sie allein und am Ende.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260926120000_RemoveFullWorthCloud")]
public sealed class RemoveFullWorthCloud : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DELETE FROM "BrandAssetBlobs" b
WHERE NOT EXISTS (SELECT 1 FROM "OfficialBrandAssets" o WHERE o."ContentSha256" = b."ContentSha256")
  AND NOT EXISTS (SELECT 1 FROM "CustomBrandAssets" c WHERE c."ContentSha256" = b."ContentSha256")
  AND NOT EXISTS (SELECT 1 FROM "ResearchedBrandAssets" r WHERE r."ContentSha256" = b."ContentSha256");

DELETE FROM "OfficialMerchantMappings";
ALTER TABLE "OfficialMerchantMappings" DROP COLUMN IF EXISTS "PackId";
ALTER TABLE "OfficialMerchantMappings" DROP COLUMN IF EXISTS "PackVersion";

DROP TABLE IF EXISTS "OfficialProductAliases";
DROP TABLE IF EXISTS "OfficialProductGtins";
DROP TABLE IF EXISTS "OfficialProducts";
DROP TABLE IF EXISTS "OfficialContractSignatures";
DROP TABLE IF EXISTS "OfficialContractProviders";
DROP TABLE IF EXISTS "OfficialOntologyRedirects";
DROP TABLE IF EXISTS "OfficialOntologyAliases";
DROP TABLE IF EXISTS "OfficialOntologyEntities";
DROP TABLE IF EXISTS "KnowledgePackTrustedKeys";
DROP TABLE IF EXISTS "KnowledgePackArchives";
DROP TABLE IF EXISTS "KnowledgePackInstallations";
DROP TABLE IF EXISTS "CloudSubmissionOutbox";
DROP TABLE IF EXISTS "CloudIntelligenceConsents";
DROP TABLE IF EXISTS "CloudInstanceCredentials";
DROP TABLE IF EXISTS "CloudConnectionStates";
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Leer wiederhergestellt. Die Daten sind fort und sollen es sein - was hier zurueckkommt,
        // ist nur die Form, damit `dotnet ef database update <vorherige>` in einer Entwicklungs-
        // schleife nicht scheitert.
        migrationBuilder.Sql("""
ALTER TABLE "OfficialMerchantMappings" ADD COLUMN IF NOT EXISTS "PackId" character varying(80) NOT NULL DEFAULT '';
ALTER TABLE "OfficialMerchantMappings" ADD COLUMN IF NOT EXISTS "PackVersion" character varying(40) NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS "CloudConnectionStates" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ScopeKey" character varying(64) NOT NULL,
    "InstanceId" uuid NOT NULL,
    "Mode" character varying(20) NOT NULL,
    "SetupDecisionAt" timestamp with time zone,
    "EntitlementStatus" character varying(40),
    "LastErrorCode" character varying(80),
    "LastRegistrationAt" timestamp with time zone,
    "LastSubmissionAt" timestamp with time zone,
    "LastPackCheckAt" timestamp with time zone
);

CREATE TABLE IF NOT EXISTS "CloudIntelligenceConsents" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "InstanceId" uuid NOT NULL,
    "AcceptedByUserId" uuid NOT NULL,
    "PolicyVersion" character varying(40) NOT NULL,
    "Locale" character varying(20) NOT NULL,
    "ClientVersion" character varying(40) NOT NULL,
    "AcceptedAt" timestamp with time zone NOT NULL,
    "RevokedAt" timestamp with time zone
);

CREATE TABLE IF NOT EXISTS "CloudInstanceCredentials" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "InstanceId" uuid NOT NULL,
    "SecretCipher" text NOT NULL,
    "Fingerprint" character varying(80) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS "CloudSubmissionOutbox" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "InstanceId" uuid NOT NULL,
    "FeedbackEventId" uuid,
    "IdempotencyKey" character varying(160) NOT NULL,
    "SchemaVersion" character varying(10) NOT NULL,
    "EventType" character varying(60) NOT NULL,
    "PayloadJson" text NOT NULL,
    "Status" character varying(20) NOT NULL,
    "Attempts" integer NOT NULL DEFAULT 0,
    "NextAttemptAt" timestamp with time zone,
    "LastError" character varying(400),
    "CreatedAt" timestamp with time zone NOT NULL,
    "SentAt" timestamp with time zone
);

CREATE TABLE IF NOT EXISTS "KnowledgePackInstallations" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ScopeKey" character varying(64) NOT NULL,
    "PackId" character varying(80) NOT NULL,
    "Version" character varying(40) NOT NULL,
    "SchemaVersion" character varying(10) NOT NULL,
    "Region" character varying(20) NOT NULL,
    "ContentSha256" character varying(64) NOT NULL,
    "SignatureBase64" text NOT NULL,
    "PayloadBase64" text NOT NULL,
    "InstalledAt" timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS "KnowledgePackArchives" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "PackId" character varying(80) NOT NULL,
    "Version" character varying(40) NOT NULL,
    "ContentSha256" character varying(64) NOT NULL,
    "PayloadBase64" text NOT NULL,
    "ArchivedAt" timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS "KnowledgePackTrustedKeys" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Endpoint" character varying(400) NOT NULL,
    "Algorithm" character varying(40) NOT NULL,
    "PublicKeyPem" text NOT NULL,
    "Fingerprint" character varying(120) NOT NULL,
    "OfferedFingerprint" character varying(120),
    "PinnedAt" timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialOntologyEntities" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "EntityType" character varying(32) NOT NULL,
    "CanonicalKey" character varying(180) NOT NULL,
    "DisplayName" character varying(200) NOT NULL,
    "ParentCanonicalKey" character varying(180),
    "Status" character varying(20) NOT NULL,
    "Version" integer NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialOntologyAliases" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "EntityType" character varying(32) NOT NULL,
    "CanonicalKey" character varying(180) NOT NULL,
    "Alias" character varying(200) NOT NULL,
    "NormalizedAlias" character varying(200) NOT NULL,
    "Locale" character varying(20) NOT NULL,
    "Country" character varying(8),
    "Confidence" numeric(6,5) NOT NULL,
    "DistinctInstances" integer NOT NULL,
    "Version" integer NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialOntologyRedirects" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "EntityType" character varying(32) NOT NULL,
    "FromCanonicalKey" character varying(180) NOT NULL,
    "ToCanonicalKey" character varying(180) NOT NULL,
    "Version" integer NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialContractProviders" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ProviderKey" character varying(180) NOT NULL,
    "CanonicalName" character varying(200) NOT NULL,
    "Domain" character varying(255),
    "ProviderCategory" character varying(80),
    "Country" character varying(8),
    "BrandKey" character varying(120),
    "Version" integer NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialContractSignatures" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ProviderKey" character varying(180) NOT NULL,
    "MerchantFingerprint" character varying(200) NOT NULL,
    "ExpectedRecurrence" character varying(40),
    "Confidence" numeric(6,5) NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialProducts" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ProductKey" character varying(180) NOT NULL,
    "CanonicalName" character varying(200) NOT NULL,
    "BrandKey" character varying(120),
    "CategoryKey" character varying(180),
    "PackageQuantity" numeric(18,4),
    "PackageUnit" character varying(20),
    "Country" character varying(8),
    "Version" integer NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialProductGtins" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ProductKey" character varying(180) NOT NULL,
    "Gtin" character varying(20) NOT NULL
);

CREATE TABLE IF NOT EXISTS "OfficialProductAliases" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ProductKey" character varying(180) NOT NULL,
    "AliasKey" character varying(300) NOT NULL,
    "MerchantContext" character varying(180),
    "Confidence" numeric(6,5) NOT NULL
);
""");
    }
}
