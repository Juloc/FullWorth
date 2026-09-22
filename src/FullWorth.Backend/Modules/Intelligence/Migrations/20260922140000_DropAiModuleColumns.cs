using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Die zwoelf Spalten, die <see cref="AiModuleGrants"/> ersetzt hat.
///
/// Sieben auf der Instanz: ihr Inhalt ist in der vorigen Migration zu Freigaben geworden, und seit
/// dem Umbau der Admin-Schnittstelle schreibt sie niemand mehr.
///
/// Fuenf beim Benutzer - und die sind der eigentliche Grund, warum dieser Umbau richtig war:
/// <c>ReceiptAiEnabled</c>, <c>MerchantAiEnabled</c>, <c>CategoryAiEnabled</c>,
/// <c>ContractAiEnabled</c> und <c>ProductAiEnabled</c> standen seit ihrer Einfuehrung in der
/// Tabelle, und es gab keine einzige Stelle im Code, die sie geschrieben oder gelesen haette. Weder
/// ein Endpunkt noch die Oberflaeche noch ein Job. Sie wurden ersatzlos entfernt; es gibt nichts zu
/// uebernehmen, weil nie etwas darin stand.
///
/// Sie faellt bewusst NACH der Uebernahme und in einem eigenen Schritt: eine Migration, die Daten
/// uebernimmt und ihre Quelle im selben Zug loescht, laesst sich nicht mehr nachpruefen.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260922140000_DropAiModuleColumns")]
public sealed class DropAiModuleColumns : Migration
{
    private static readonly string[] InstanceColumns =
    [
        "ReceiptAiEnabled", "MerchantAiEnabled", "CategoryAiEnabled",
        "ContractAiEnabled", "ProductAiEnabled",
        "LogoResearchEnabled", "InternetResearchEnabled"
    ];

    private static readonly string[] UserColumns =
    [
        "ReceiptAiEnabled", "MerchantAiEnabled", "CategoryAiEnabled",
        "ContractAiEnabled", "ProductAiEnabled"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var column in InstanceColumns)
            migrationBuilder.Sql($"""ALTER TABLE "AiInstanceSettings" DROP COLUMN IF EXISTS "{column}";""");

        foreach (var column in UserColumns)
            migrationBuilder.Sql($"""ALTER TABLE "AiUserSettings" DROP COLUMN IF EXISTS "{column}";""");
    }

    /// <summary>
    /// Zurueck kommen die Spalten leer. Der Inhalt der Instanz-Spalten steht in den Freigaben und
    /// laesst sich von dort wiederherstellen; die des Benutzers waren nie gefuellt.
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var column in InstanceColumns)
            migrationBuilder.Sql(
                $"""ALTER TABLE "AiInstanceSettings" ADD COLUMN IF NOT EXISTS "{column}" boolean NOT NULL DEFAULT false;""");

        foreach (var column in UserColumns)
            migrationBuilder.Sql(
                $"""ALTER TABLE "AiUserSettings" ADD COLUMN IF NOT EXISTS "{column}" boolean NULL;""");
    }
}
