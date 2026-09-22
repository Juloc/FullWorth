using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Wofuer eine KI arbeiten darf, als Daten statt als Spalten.
///
/// Vorher war jede Funktion eine eigene Spalte - fuenf auf der Instanz und dieselben fuenf noch
/// einmal beim Benutzer, wobei die beim Benutzer nie jemand gelesen hat. Eine neue Funktion kostete
/// damit einen Schemawechsel.
///
/// Die Uebernahme ist der eigentliche Inhalt dieser Migration. Sie muss zwei Dinge treffen:
///
/// 1. <b>Was eingeschaltet war, bleibt eingeschaltet.</b> Jeder gesetzte Schalter wird zu einer
///    Freigabe fuer den Zugang, der gerade eingetragen ist.
/// 2. <b>Der Coach hatte nie einen Schalter.</b> Er lief, sobald ueberhaupt ein Zugang da war. Ohne
///    eine Freigabe fuer ihn waere er nach dem Update aus - ein Feature, das ein Update still
///    abschaltet, ist schlimmer als eines, das nie lief. Er bekommt sie deshalb bedingungslos.
///
/// MerchantAiEnabled und CategoryAiEnabled werden zu EINER Freigabe. Sie wurden ohnehin nur
/// gemeinsam abgefragt ("MerchantAiEnabled AND CategoryAiEnabled") - es ist eine Funktion.
///
/// Die alten Spalten bleiben vorerst stehen. Sie fallen, wenn die Oberflaeche auf die Modulliste
/// umgestellt ist; eine Migration, die Daten uebernimmt UND ihre Quelle im selben Schritt loescht,
/// laesst sich nicht mehr nachpruefen.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260922120000_AiModuleGrants")]
public sealed class AiModuleGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "AiModuleGrants" (
    "Id" uuid NOT NULL,
    "CredentialId" uuid NOT NULL,
    "Module" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AiModuleGrants" PRIMARY KEY ("Id"),
    -- Eine Freigabe ohne ihren Zugang ist nichts. Die Kante traegt ausserdem das Loeschen eines
    -- Kontos: dessen eigene Zugaenge verschwinden, und ihre Freigaben mit ihnen.
    CONSTRAINT "FK_AiModuleGrants_AiCredentials_CredentialId"
        FOREIGN KEY ("CredentialId") REFERENCES "AiCredentials" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiModuleGrants_CredentialId_Module"
    ON "AiModuleGrants" ("CredentialId", "Module");
""");

        // Jeder gesetzte Schalter der Instanz wird zur Freigabe ihres eingetragenen Zugangs. Ohne
        // Zugang gibt es nichts freizugeben - dann bleibt die Tabelle leer und die Instanz ist
        // weiterhin unkonfiguriert, was richtig ist.
        migrationBuilder.Sql("""
INSERT INTO "AiModuleGrants" ("Id", "CredentialId", "Module", "CreatedAt")
SELECT gen_random_uuid(), s."CredentialId", grant_module, now()
FROM "AiInstanceSettings" s
CROSS JOIN LATERAL (
    SELECT 'categorization' AS grant_module WHERE s."MerchantAiEnabled" AND s."CategoryAiEnabled"
    UNION ALL SELECT 'receipts' WHERE s."ReceiptAiEnabled"
    UNION ALL SELECT 'products' WHERE s."ProductAiEnabled"
    UNION ALL SELECT 'contracts' WHERE s."ContractAiEnabled"
    UNION ALL SELECT 'logo-research' WHERE s."LogoResearchEnabled"
    UNION ALL SELECT 'internet-research' WHERE s."InternetResearchEnabled"
    -- Der Coach kannte keinen Schalter: er lief, sobald ein Zugang da war.
    UNION ALL SELECT 'coach'
) AS modules(grant_module)
WHERE s."CredentialId" IS NOT NULL
ON CONFLICT ("CredentialId", "Module") DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "AiModuleGrants";""");
}
