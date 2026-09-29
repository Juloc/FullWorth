using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Die Spalte heisst jetzt nach dem, was sie bedeutet, statt nach dem Empfaenger, den es nicht
/// mehr gibt.
///
/// <c>CloudEligible</c> hiess "darf an die FullWorth Intelligence Cloud". Die Cloud ist seit dem
/// 2026-09-26 abgeschafft, die Eigenschaft selbst aber nicht: sie unterscheidet weiterhin
/// verallgemeinerbares Wissen ("REWE ist Lebensmittel" gilt fuer jeden) von einer Feststellung
/// ueber diesen Haushalt ("diese Buchung gehoert zu Urlaub"), und genau diese Unterscheidung
/// braucht die Instanz, um aus eigenen Rueckmeldungen eigenes Wissen zu machen.
///
/// Ein Name, der auf etwas Verschwundenes zeigt, ist schlimmer als ein unschoener Name: er laedt
/// dazu ein, die Spalte fuer tot zu halten und beim naechsten Aufraeumen mitzunehmen.
///
/// <c>IntelligenceDigests</c> schreibt das Feld auch in den gespeicherten Bericht. Alte Berichte
/// tragen dort weiter <c>cloudEligible</c> - sie werden nicht angefasst, und
/// <c>digests.js</c> liest deshalb <c>generalizable ?? cloudEligible</c>.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260927100000_FeedbackGeneralizable")]
public sealed class FeedbackGeneralizable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "IntelligenceFeedbackEvents" RENAME COLUMN "CloudEligible" TO "Generalizable";
ALTER INDEX IF EXISTS "IX_IntelligenceFeedbackEvents_CloudEligible_CreatedAt"
    RENAME TO "IX_IntelligenceFeedbackEvents_Generalizable_CreatedAt";
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER INDEX IF EXISTS "IX_IntelligenceFeedbackEvents_Generalizable_CreatedAt"
    RENAME TO "IX_IntelligenceFeedbackEvents_CloudEligible_CreatedAt";
ALTER TABLE "IntelligenceFeedbackEvents" RENAME COLUMN "Generalizable" TO "CloudEligible";
""");
    }
}
